using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws the points and the fitted lines of a probability plot, and nothing else: the frame around them belongs to
// SkiaGraphRenderer, and what to draw was decided by ProbabilityPlotRenderModelBuilder.
//
// A group's points and its line are drawn in the same colour, so a plot of several groups reads as several straight
// lines with their own observations around them.
public sealed class ProbabilityPlotRenderer : IGraphPlotRenderer
{
    // The same marker size a scatter plot uses: both graphs show one point per observation.
    public const float MarkerDiameter = 4.5f;

    public const float FittedLineWidth = 1.25f;

    // Points per draw call. Small enough that the buffer stays off the large object heap on every frame.
    private const int BatchSize = 4096;

    private readonly ProbabilityPlotRenderModel _model;

    public ProbabilityPlotRenderer(ProbabilityPlotRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    public ProbabilityPlotRenderModel Model => _model;

    public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(theme);

        using var line = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = FittedLineWidth
        };

        using var markers = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = MarkerDiameter
        };

        // The lines first: a line is what the points are read against, not something drawn over them.
        foreach (var series in _model.Series)
        {
            if (series.FittedLine is not { } fitted)
            {
                continue;
            }

            // Across the whole score range the plot shows - the builder's own, or one the user chose - so the line
            // always reaches both edges; the values come from the line itself (ValueAt), nothing is fitted again.
            var bottom = transform.YRange.Minimum;
            var top = transform.YRange.Maximum;
            var from = transform.ToScreenPoint(fitted.ValueAt(bottom), bottom);
            var to = transform.ToScreenPoint(fitted.ValueAt(top), top);
            if (!IsDrawable(from) || !IsDrawable(to))
            {
                continue;
            }

            line.Color = theme.SeriesColor(series.SeriesIndex);
            canvas.DrawLine(from, to, line);
        }

        var batch = new SKPoint[BatchSize];
        foreach (var series in _model.Series)
        {
            markers.Color = theme.SeriesColor(series.SeriesIndex);

            var points = series.Points.Span;
            var count = 0;
            for (var index = 0; index < points.Length; index++)
            {
                var screen = transform.ToScreenPoint(points[index].Value, points[index].Score);

                // The transform answers NaN for a value it cannot place; such a point is simply not drawn.
                if (!IsDrawable(screen))
                {
                    continue;
                }

                batch[count++] = screen;
                if (count == BatchSize)
                {
                    canvas.DrawPoints(SKPointMode.Points, batch, markers);
                    count = 0;
                }
            }

            if (count > 0)
            {
                // Skia draws whole arrays, so the last, partly filled batch is copied to its own: one small array per
                // series, never one per point.
                var tail = new SKPoint[count];
                Array.Copy(batch, tail, count);
                canvas.DrawPoints(SKPointMode.Points, tail, markers);
            }
        }
    }

    private static bool IsDrawable(SKPoint point) => float.IsFinite(point.X) && float.IsFinite(point.Y);
}
