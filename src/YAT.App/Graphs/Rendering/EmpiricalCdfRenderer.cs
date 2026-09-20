using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws the step functions of an empirical CDF, and nothing else: the frame around them belongs to SkiaGraphRenderer,
// and the distributions were worked out by EmpiricalCdfRenderModelBuilder.
//
// A distribution is right-continuous: it is flat until an observed value, jumps straight up at it, and stays there
// until the next one. The drawing says exactly that - every segment is either horizontal or vertical, and two steps are
// never joined by a diagonal, however few of them are drawn.
//
// The curve is completed across the whole axis: flat at nought percent from the left edge to the first observation, and
// flat at a hundred from the last one to the right edge. Those two segments belong to the drawing, not to the model:
// they are not observations.
public sealed class EmpiricalCdfRenderer : IGraphPlotRenderer
{
    public const float LineWidth = 1.5f;

    private readonly EmpiricalCdfRenderModel _model;

    public EmpiricalCdfRenderer(EmpiricalCdfRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    public EmpiricalCdfRenderModel Model => _model;

    public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(transform);
        ArgumentNullException.ThrowIfNull(theme);

        using var stroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = LineWidth,
            StrokeJoin = SKStrokeJoin.Miter,
            StrokeCap = SKStrokeCap.Butt
        };

        var left = (float)transform.ToScreenX(transform.XRange.Minimum);
        var right = (float)transform.ToScreenX(transform.XRange.Maximum);
        if (!float.IsFinite(left) || !float.IsFinite(right))
        {
            return;
        }

        foreach (var series in _model.Series)
        {
            var points = series.Points.Span;
            if (points.Length == 0)
            {
                continue;
            }

            using var path = new SKPath();
            var bottom = (float)transform.ToScreenY(PercentAxis.Minimum);
            var start = (float)transform.ToScreenX(points[0].Value);
            var first = (float)transform.ToScreenY(points[0].CumulativePercent);
            if (!float.IsFinite(bottom) || !float.IsFinite(start) || !float.IsFinite(first))
            {
                continue;
            }

            // Nothing of the series is below its smallest observation.
            path.MoveTo(left, bottom);
            path.LineTo(start, bottom);
            path.LineTo(start, first);

            var previous = first;
            for (var index = 1; index < points.Length; index++)
            {
                var x = (float)transform.ToScreenX(points[index].Value);
                var y = (float)transform.ToScreenY(points[index].CumulativePercent);
                if (!float.IsFinite(x) || !float.IsFinite(y))
                {
                    continue;
                }

                // Flat up to the next observed value, then straight up at it.
                path.LineTo(x, previous);
                path.LineTo(x, y);
                previous = y;
            }

            // Everything of the series is at or below its largest observation.
            path.LineTo(right, previous);

            stroke.Color = theme.SeriesColor(series.SeriesIndex);
            canvas.DrawPath(path, stroke);
        }
    }
}
