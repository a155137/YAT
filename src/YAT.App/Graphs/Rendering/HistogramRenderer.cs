using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws the bars of a histogram and the normal fits over them, and nothing else: the frame around them belongs to
// SkiaGraphRenderer, and what to count and which curves to draw was decided by HistogramRenderModelBuilder.
//
// A histogram of one series is drawn solid. A grouped one overlays its series in the same bins - they describe the same
// axis, so putting them side by side would misplace them - and fills them semi-transparently with an opaque outline, so
// a distribution drawn later does not hide the one drawn before it.
public sealed class HistogramRenderer : IGraphPlotRenderer
{
    // How much of the fill colour a grouped series keeps. Tuned on screen: dense enough to read one distribution,
    // light enough to see the ones behind it.
    public const byte GroupedFillAlpha = 100;

    // Kept clear on each side of a bar, in layout units, so neighbouring bars stay apart. Dropped when the bars are
    // too narrow to lose it.
    private const float BarGap = 0.5f;

    // Bars narrower than this touch each other: at that width a gap would be most of the bar.
    private const float MinimumGappedBarWidth = 10f;

    // A normal fit is drawn this wide in its series' colour, on a halo of the plot background this wide.
    public const float NormalFitWidth = 1.5f;

    public const float NormalFitHaloWidth = 3f;

    // Bars narrower than this are drawn without their outline, which would otherwise cover them.
    private const float MinimumOutlinedBarWidth = 4f;

    private readonly HistogramRenderModel _model;

    public HistogramRenderer(HistogramRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    public HistogramRenderModel Model => _model;

    public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(theme);

        // The legend is what says the graph has groups: one unnamed series is drawn as a solid histogram.
        var grouped = _model.Frame.Legend is not null;

        // Bars are read as areas, not as shapes: drawn without antialiasing they keep crisp vertical edges at any
        // window size, and an outline keeps exactly the colour of its series.
        using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        using var outline = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };

        // Every bar stands on the zero of the Y axis, which is the bottom of the plot area.
        var baseline = (float)transform.ToScreenY(0);
        if (!float.IsFinite(baseline))
        {
            return;
        }

        // Fills first for every series, then the outlines on top of all of them: the shape of a distribution stays
        // readable even where a series drawn later covers it.
        foreach (var series in _model.Series)
        {
            var color = theme.SeriesColor(series.SeriesIndex);
            fill.Color = grouped ? color.WithAlpha(GroupedFillAlpha) : color;
            DrawBars(canvas, series, transform, baseline, fill);
        }

        if (grouped)
        {
            foreach (var series in _model.Series)
            {
                outline.Color = theme.SeriesColor(series.SeriesIndex);

                // On a narrow bar the outline would be the whole bar: many bins are read as a shape, not as bars, and
                // the fills alone carry it.
                DrawBars(canvas, series, transform, baseline, outline, MinimumOutlinedBarWidth);
            }
        }

        DrawNormalFits(canvas, transform, theme);
    }

    // The normal fits last, over every bar, in series order and each in its series' colour. Each line lies on a halo of
    // the plot background, so it stays readable where it crosses bars of its own colour - the solid bars of an
    // ungrouped histogram as much as the overlaid ones of a grouped one. The curves were worked out by the builder;
    // here they are only drawn, and what lies outside the plot area is clipped.
    private void DrawNormalFits(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
    {
        if (_model.Series.All(series => series.NormalFit is null))
        {
            return;
        }

        using var halo = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = NormalFitHaloWidth,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
            Color = theme.PlotBackground
        };

        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = NormalFitWidth,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round
        };

        foreach (var series in _model.Series)
        {
            if (series.NormalFit is not { } fit || Path(fit, transform) is not { } path)
            {
                continue;
            }

            using (path)
            {
                line.Color = theme.SeriesColor(series.SeriesIndex);
                canvas.DrawPath(path, halo);
                canvas.DrawPath(path, line);
            }
        }
    }

    // The curve through its points on screen, or null when fewer than two of them can be placed. A point the transform
    // cannot place breaks the curve rather than joining its neighbours across the gap.
    private static SKPath? Path(HistogramNormalFit fit, GraphCoordinateTransform transform)
    {
        var path = new SKPath();
        var drawn = 0;
        var joined = false;
        foreach (var point in fit.Points)
        {
            var x = (float)transform.ToScreenX(point.X);
            var y = (float)transform.ToScreenY(point.Height);
            if (!float.IsFinite(x) || !float.IsFinite(y))
            {
                joined = false;
                continue;
            }

            if (joined)
            {
                path.LineTo(x, y);
            }
            else
            {
                path.MoveTo(x, y);
            }

            joined = true;
            drawn++;
        }

        if (drawn < 2)
        {
            path.Dispose();
            return null;
        }

        return path;
    }

    private void DrawBars(
        SKCanvas canvas,
        HistogramSeriesRenderModel series,
        GraphCoordinateTransform transform,
        float baseline,
        SKPaint paint,
        float minimumWidth = 0f)
    {
        for (var index = 0; index < _model.Bins.Count; index++)
        {
            // The bar is drawn to its height on the histogram's Y scale; on the frequency scale that is its count. A bin
            // with nothing in it has no bar on any scale.
            var height = series.Heights[index];
            if (series.Counts[index] <= 0 || !(height > 0))
            {
                continue;
            }

            var bin = _model.Bins[index];
            var left = (float)transform.ToScreenX(bin.LowerEdge);
            var right = (float)transform.ToScreenX(bin.UpperEdge);
            var top = (float)transform.ToScreenY(height);
            if (!float.IsFinite(left) || !float.IsFinite(right) || !float.IsFinite(top))
            {
                continue;
            }

            var width = right - left;
            if (width < minimumWidth)
            {
                continue;
            }

            var gap = width >= MinimumGappedBarWidth ? BarGap : 0f;
            canvas.DrawRect(new SKRect(left + gap, top, right - gap, baseline), paint);
        }
    }
}
