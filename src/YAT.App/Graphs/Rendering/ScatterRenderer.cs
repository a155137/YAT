using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws the markers of a scatter plot, and nothing else: the frame around them belongs to SkiaGraphRenderer, and what
// to draw was decided by ScatterRenderModelBuilder.
//
// Markers are filled circles in the theme colour of their series. They are drawn as Skia points with a round stroke
// cap, in batches, so one series costs a handful of draw calls instead of one per point.
public sealed class ScatterRenderer : IGraphPlotRenderer
{
    // Marker diameter in layout units. Small enough to show the shape of dense data, large enough to see one point.
    public const float MarkerDiameter = 4.5f;

    // Points per draw call. Small enough that the buffer stays off the large object heap on every frame.
    private const int BatchSize = 4096;

    private readonly ScatterRenderModel _model;

    public ScatterRenderer(ScatterRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    public ScatterRenderModel Model => _model;

    public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(theme);

        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = MarkerDiameter
        };

        var batch = new SKPoint[BatchSize];
        foreach (var series in _model.Series)
        {
            paint.Color = theme.SeriesColor(series.SeriesIndex);

            var points = series.Points.Span;
            var count = 0;
            for (var index = 0; index < points.Length; index++)
            {
                var screen = transform.ToScreenPoint(points[index].X, points[index].Y);

                // The transform answers NaN for a value it cannot place; such a point is simply not drawn.
                if (!float.IsFinite(screen.X) || !float.IsFinite(screen.Y))
                {
                    continue;
                }

                batch[count++] = screen;
                if (count == BatchSize)
                {
                    canvas.DrawPoints(SKPointMode.Points, batch, paint);
                    count = 0;
                }
            }

            if (count > 0)
            {
                // Skia draws whole arrays, so the last, partly filled batch is copied to its own: one small array per
                // series, never one per point.
                var tail = new SKPoint[count];
                Array.Copy(batch, tail, count);
                canvas.DrawPoints(SKPointMode.Points, tail, paint);
            }
        }
    }
}
