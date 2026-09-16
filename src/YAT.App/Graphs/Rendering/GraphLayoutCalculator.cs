using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Divides the canvas into the areas of a graph. Pure arithmetic over the canvas size, what the model actually has
// (title, axis titles, legend) and the measured metrics, so the same graph re-lays out correctly at any window size or
// scaling without any fixed coordinates.
public static class GraphLayoutCalculator
{
    // Below these the graph cannot say anything useful, so nothing but the background is drawn.
    private const float MinimumPlotWidth = 24f;
    private const float MinimumPlotHeight = 24f;

    public static GraphLayout Calculate(SKRect canvas, GraphRenderModel model, GraphLayoutMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(metrics);
        metrics.EnsureValid();

        if (!IsFinite(canvas))
        {
            throw new ArgumentException("The canvas must be a finite rectangle.", nameof(canvas));
        }

        var content = new SKRect(
            canvas.Left + metrics.OuterPadding,
            canvas.Top + metrics.OuterPadding,
            canvas.Right - metrics.OuterPadding,
            canvas.Bottom - metrics.OuterPadding);

        var titleHeight = HasText(model.Title) ? metrics.TitleHeight : 0f;
        var legendWidth = model.Legend is null ? 0f : metrics.LegendWidth;

        // Left band: Y axis title, Y tick labels, ticks. Bottom band: ticks, X tick labels, X axis title.
        var leftBand = metrics.TickLength + metrics.Gap + metrics.YTickLabelWidth
            + (HasText(model.YAxis.Title) ? metrics.AxisTitleHeight + metrics.Gap : 0f);
        var bottomBand = metrics.TickLength + metrics.Gap + metrics.TickLabelHeight
            + (HasText(model.XAxis.Title) ? metrics.AxisTitleHeight + metrics.Gap : 0f);

        // The top Y tick label is centred on the top edge of the plot area, so half of it sits above the plot.
        var topBand = titleHeight + (titleHeight > 0 ? metrics.Gap : 0f) + (metrics.TickLabelHeight / 2f);
        var rightBand = metrics.XTickLabelOverflow + (legendWidth > 0 ? legendWidth + metrics.Gap : 0f);

        var plotArea = new SKRect(
            content.Left + leftBand,
            content.Top + topBand,
            content.Right - rightBand,
            content.Bottom - bottomBand);

        if (plotArea.Width < MinimumPlotWidth || plotArea.Height < MinimumPlotHeight)
        {
            return new GraphLayout(canvas, SKRect.Empty, SKRect.Empty, SKRect.Empty, SKRect.Empty, SKRect.Empty);
        }

        var titleArea = titleHeight > 0
            ? new SKRect(content.Left, content.Top, content.Right, content.Top + titleHeight)
            : SKRect.Empty;

        var legendArea = legendWidth > 0
            ? new SKRect(content.Right - legendWidth, plotArea.Top, content.Right, plotArea.Bottom)
            : SKRect.Empty;

        return new GraphLayout(
            canvas,
            titleArea,
            plotArea,
            new SKRect(plotArea.Left, plotArea.Bottom, plotArea.Right, content.Bottom),
            new SKRect(content.Left, plotArea.Top, plotArea.Left, plotArea.Bottom),
            legendArea);
    }

    private static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
}
