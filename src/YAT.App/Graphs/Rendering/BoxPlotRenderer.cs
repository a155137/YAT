using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws the boxes of a box plot, and nothing else: the frame around them belongs to SkiaGraphRenderer, and the boxes
// themselves were worked out by BoxPlotRenderModelBuilder. Nothing here computes a quartile or reads worksheet data.
//
// One box is drawn as the reader expects to see it: the box of the middle half of the data, the median across it, a
// whisker reaching out of each end of the box to its whisker value with a cap, the mean as a cross, and every outlier
// as a small circle on the box's own centre line.
//
// A whisker only ever reaches outward. With interpolated quartiles a whisker can end inside its own box (when the value
// Q3 is interpolated towards is an outlier, the upper whisker is below Q3); that side then has no whisker and no cap -
// nothing is drawn inward through the box, and no whisker is invented at the box edge.
//
// The X axis is categorical - one unit per category - so the width of a box is a fraction of that unit, kept inside
// sensible pixel limits so a plot of two boxes does not draw two slabs and a plot of forty still shows them.
public sealed class BoxPlotRenderer : IGraphPlotRenderer
{
    // How much of a category's slot the box body takes.
    public const double BoxWidthFraction = 0.6;

    // Pixel limits for the box body's width, whatever the axis says.
    public const float MinimumBoxWidth = 5f;
    public const float MaximumBoxWidth = 72f;

    // The whisker caps are drawn half as wide as the box.
    public const double CapWidthFraction = 0.5;

    public const float LineWidth = 1.25f;
    public const float MedianLineWidth = 2f;

    // The mean's cross and the outlier dots, in layout units.
    public const float MeanMarkerSize = 9f;
    public const float OutlierDiameter = 4f;

    // The box body is filled with its series colour at this alpha, so overlapping outliers and the median stay legible.
    public const byte FillAlpha = 70;

    private readonly BoxPlotRenderModel _model;

    public BoxPlotRenderer(BoxPlotRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        _model = model;
    }

    public BoxPlotRenderModel Model => _model;

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
            StrokeCap = SKStrokeCap.Butt
        };

        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        using var outliers = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = SKStrokeCap.Round,
            StrokeWidth = OutlierDiameter
        };

        var halfWidth = BoxHalfWidth(transform);
        var capHalfWidth = (float)(halfWidth * CapWidthFraction);

        foreach (var box in _model.Boxes)
        {
            var centre = (float)transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(box.CategoryIndex));
            var lower = (float)transform.ToScreenY(box.LowerWhisker);
            var q1 = (float)transform.ToScreenY(box.FirstQuartile);
            var median = (float)transform.ToScreenY(box.Median);
            var q3 = (float)transform.ToScreenY(box.ThirdQuartile);
            var upper = (float)transform.ToScreenY(box.UpperWhisker);
            if (!float.IsFinite(centre) || !float.IsFinite(lower) || !float.IsFinite(q1)
                || !float.IsFinite(median) || !float.IsFinite(q3) || !float.IsFinite(upper))
            {
                continue;
            }

            var colour = theme.SeriesColor(box.SeriesIndex);
            stroke.Color = colour;
            stroke.StrokeWidth = LineWidth;

            // Each whisker from the edge of the box outward, and only when its value lies beyond that edge. Compared in
            // data values, not pixels, so a whisker a fraction of a pixel outside the box still counts as outside.
            if (box.UpperWhisker > box.ThirdQuartile)
            {
                canvas.DrawLine(centre, q3, centre, upper, stroke);
                canvas.DrawLine(centre - capHalfWidth, upper, centre + capHalfWidth, upper, stroke);
            }

            if (box.LowerWhisker < box.FirstQuartile)
            {
                canvas.DrawLine(centre, q1, centre, lower, stroke);
                canvas.DrawLine(centre - capHalfWidth, lower, centre + capHalfWidth, lower, stroke);
            }

            // Screen Y grows downward, so Q3 is the top edge of the rectangle.
            var body = new SKRect(centre - halfWidth, q3, centre + halfWidth, q1);
            fill.Color = colour.WithAlpha(FillAlpha);
            canvas.DrawRect(body, fill);
            canvas.DrawRect(body, stroke);

            stroke.StrokeWidth = MedianLineWidth;
            canvas.DrawLine(body.Left, median, body.Right, median, stroke);
            stroke.StrokeWidth = LineWidth;

            DrawMean(canvas, stroke, centre, (float)transform.ToScreenY(box.Mean));

            outliers.Color = colour;
            DrawOutliers(canvas, outliers, transform, centre, box);
        }
    }

    // The mean is a cross, so it is never mistaken for the median line or for an outlier dot.
    private static void DrawMean(SKCanvas canvas, SKPaint stroke, float centre, float mean)
    {
        if (!float.IsFinite(mean))
        {
            return;
        }

        var arm = MeanMarkerSize / 2f;
        canvas.DrawLine(centre - arm, mean - arm, centre + arm, mean + arm, stroke);
        canvas.DrawLine(centre - arm, mean + arm, centre + arm, mean - arm, stroke);
    }

    private static void DrawOutliers(
        SKCanvas canvas,
        SKPaint paint,
        GraphCoordinateTransform transform,
        float centre,
        BoxPlotBoxRenderModel box)
    {
        var values = box.Outliers.Span;
        if (values.Length == 0)
        {
            return;
        }

        var points = new SKPoint[values.Length];
        var count = 0;
        for (var index = 0; index < values.Length; index++)
        {
            var y = (float)transform.ToScreenY(values[index]);

            // The transform answers NaN for a value it cannot place; such a point is simply not drawn.
            if (float.IsFinite(y))
            {
                points[count++] = new SKPoint(centre, y);
            }
        }

        if (count == 0)
        {
            return;
        }

        if (count < points.Length)
        {
            var drawn = new SKPoint[count];
            Array.Copy(points, drawn, count);
            points = drawn;
        }

        canvas.DrawPoints(SKPointMode.Points, points, paint);
    }

    // Half the width of a box body in pixels: a fraction of one category slot, kept between the limits above.
    private static float BoxHalfWidth(GraphCoordinateTransform transform)
    {
        var slot = (float)(transform.ToScreenX(1) - transform.ToScreenX(0));
        if (!float.IsFinite(slot) || slot <= 0)
        {
            return MinimumBoxWidth / 2f;
        }

        return (float)Math.Clamp(slot * BoxWidthFraction, MinimumBoxWidth, MaximumBoxWidth) / 2f;
    }
}
