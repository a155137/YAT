using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws the bars of a histogram, and nothing else: the frame around them belongs to SkiaGraphRenderer, and what to
// count was decided by HistogramRenderModelBuilder.
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

        if (!grouped)
        {
            return;
        }

        foreach (var series in _model.Series)
        {
            outline.Color = theme.SeriesColor(series.SeriesIndex);

            // On a narrow bar the outline would be the whole bar: many bins are read as a shape, not as bars, and the
            // fills alone carry it.
            DrawBars(canvas, series, transform, baseline, outline, MinimumOutlinedBarWidth);
        }
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
