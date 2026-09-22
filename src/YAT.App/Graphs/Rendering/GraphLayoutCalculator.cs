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

    // The most of the content width a statistics panel may take, whatever its content would like.
    public const float MaximumStatisticsPanelShare = 0.4f;

    // The most of the plot's height the legend may take when a statistics panel sits below it.
    public const float MaximumLegendShareWithPanel = 0.5f;

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

        // The legend and the statistics panel share one column on the right, as wide as the wider of the two. The panel
        // is never dropped to make room: it is only kept from taking more than its share of the width, so on a narrow
        // canvas the plot narrows with it instead of disappearing.
        var panelWidth = model.StatisticsPanel is null
            ? 0f
            : Math.Min(metrics.StatisticsPanelWidth, Math.Max(content.Width, 0f) * MaximumStatisticsPanelShare);
        var columnWidth = Math.Max(legendWidth, panelWidth);

        // Left band: Y axis title, Y tick labels, ticks. Bottom band: ticks, X tick labels, X axis title.
        var leftBand = metrics.TickLength + metrics.Gap + metrics.YTickLabelWidth
            + (HasText(model.YAxis.Title) ? metrics.AxisTitleHeight + metrics.Gap : 0f);
        var bottomBand = metrics.TickLength + metrics.Gap + metrics.TickLabelHeight
            + (HasText(model.XAxis.Title) ? metrics.AxisTitleHeight + metrics.Gap : 0f);

        // The top Y tick label is centred on the top edge of the plot area, so half of it sits above the plot.
        var topBand = titleHeight + (titleHeight > 0 ? metrics.Gap : 0f) + (metrics.TickLabelHeight / 2f);
        var rightBand = metrics.XTickLabelOverflow + (columnWidth > 0 ? columnWidth + metrics.Gap : 0f);

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

        var (legendArea, panelArea) = RightColumn(content, plotArea, metrics, legendWidth, panelWidth, columnWidth);

        return new GraphLayout(
            canvas,
            titleArea,
            plotArea,
            new SKRect(plotArea.Left, plotArea.Bottom, plotArea.Right, content.Bottom),
            new SKRect(content.Left, plotArea.Top, plotArea.Left, plotArea.Bottom),
            legendArea,
            panelArea);
    }

    // The column right of the plot, as tall as the plot: the legend on top, the statistics panel below it.
    //
    // Without a panel the legend is laid out exactly as it always was - flush right, the plot's full height to grow
    // into. With one, both keep their own width at the left of the column; the legend takes the height its entries
    // need, up to half the column, and the panel the rest.
    private static (SKRect Legend, SKRect Panel) RightColumn(
        SKRect content,
        SKRect plotArea,
        GraphLayoutMetrics metrics,
        float legendWidth,
        float panelWidth,
        float columnWidth)
    {
        if (panelWidth <= 0)
        {
            var alone = legendWidth > 0
                ? new SKRect(content.Right - legendWidth, plotArea.Top, content.Right, plotArea.Bottom)
                : SKRect.Empty;
            return (alone, SKRect.Empty);
        }

        var left = content.Right - columnWidth;
        var legend = SKRect.Empty;
        var panelTop = plotArea.Top;

        if (legendWidth > 0)
        {
            var legendHeight = Math.Min(metrics.LegendHeight, plotArea.Height * MaximumLegendShareWithPanel);
            legend = new SKRect(left, plotArea.Top, left + legendWidth, plotArea.Top + legendHeight);
            panelTop = legend.Bottom + metrics.Gap;
        }

        var panel = new SKRect(left, Math.Min(panelTop, plotArea.Bottom), left + panelWidth, plotArea.Bottom);
        return (legend, panel);
    }

    private static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
}
