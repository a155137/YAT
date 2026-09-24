using SkiaSharp;
using YAT.Application.Graphs;

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

    // The most of the content a legend may take (Task #044): of the width beside the plot, of the height above or below
    // it. A legend that needs more shows what fits and "… N more".
    public const float MaximumLegendShareBeside = 0.35f;

    public const float MaximumLegendShareAboveOrBelow = 0.3f;

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

        // The top Y tick label is centred on the top edge of the plot area, so half of it sits above the plot. The
        // reference line labels sit above the plot too, but only across its width, beside that half label rather than
        // over it: the band takes whichever of the two is taller.
        var labelBand = metrics.ReferenceLabelHeight;
        var topBand = titleHeight + (titleHeight > 0 ? metrics.Gap : 0f) + Math.Max(metrics.TickLabelHeight / 2f, labelBand);

        // A legend on another side, or one its single column on the right cannot hold whole (Task #044), is arranged in
        // the room its side gives it. Every other layout below is the one a graph has always had.
        if (model.Legend is { } legend
            && NeedsArranging(model, legend, metrics, content, topBand, bottomBand, panelWidth))
        {
            return Arranged(
                canvas, content, model, legend, metrics, titleHeight, leftBand, bottomBand, labelBand, topBand,
                panelWidth);
        }

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

        var labelArea = labelBand > 0
            ? new SKRect(plotArea.Left, plotArea.Top - labelBand, plotArea.Right, plotArea.Top)
            : SKRect.Empty;

        return new GraphLayout(
            canvas,
            titleArea,
            plotArea,
            new SKRect(plotArea.Left, plotArea.Bottom, plotArea.Right, content.Bottom),
            new SKRect(content.Left, plotArea.Top, plotArea.Left, plotArea.Bottom),
            legendArea,
            panelArea,
            labelArea);
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

    // Whether the legend has to be arranged rather than laid out as it always was: it stands on another side than the
    // right, or on the right its single column cannot hold it whole - more rows than its height takes, or a label wider
    // than the column. Metrics measured without the legend's entries (a layout worked out on its own) keep the legend
    // as it always was.
    private static bool NeedsArranging(
        GraphRenderModel model,
        GraphLegendModel legend,
        GraphLayoutMetrics metrics,
        SKRect content,
        float topBand,
        float bottomBand,
        float panelWidth)
    {
        if (metrics.LegendLabelWidths.Count != legend.Entries.Count)
        {
            return false;
        }

        if (model.LegendPosition != GraphLegendPosition.Right)
        {
            return true;
        }

        // The column the legend has always had: the plot's height, or half of it above a statistics panel.
        var plotTop = content.Top + topBand;
        var plotBottom = content.Bottom - bottomBand;
        var rowsFit = panelWidth <= 0
            ? plotTop + metrics.LegendHeight <= plotBottom
            : metrics.LegendHeight <= (plotBottom - plotTop) * MaximumLegendShareWithPanel;

        var widest = metrics.LegendTitleWidth;
        foreach (var label in metrics.LegendLabelWidths)
        {
            widest = Math.Max(widest, GraphLegendLayout.SwatchSize + GraphLegendLayout.EntrySpacing + label);
        }

        var textFits = widest + (GraphLegendLayout.Padding * 2f) <= GraphLegendLayout.MaximumColumnWidth;
        return !(rowsFit && textFits);
    }

    // The layout with the legend arranged on its side. Right and left: its columns as tall as the plot (half of it
    // above a statistics panel on the right) and together at most MaximumLegendShareBeside of the width. Top and
    // bottom: its rows as wide as the plot and together at most MaximumLegendShareAboveOrBelow of the height. A legend
    // that is not on the right leaves the right-hand column to the statistics panel alone.
    private static GraphLayout Arranged(
        SKRect canvas,
        SKRect content,
        GraphRenderModel model,
        GraphLegendModel legend,
        GraphLayoutMetrics metrics,
        float titleHeight,
        float leftBand,
        float bottomBand,
        float labelBand,
        float topBand,
        float panelWidth)
    {
        var position = model.LegendPosition;
        var beside = position is GraphLegendPosition.Right or GraphLegendPosition.Left;
        var plotTop = content.Top + topBand;
        var plotBottom = content.Bottom - bottomBand;
        var panelBand = panelWidth > 0 ? panelWidth + metrics.Gap : 0f;

        // The room the legend's side gives it.
        float maximumWidth;
        float maximumHeight;
        if (beside)
        {
            maximumWidth = Math.Max(content.Width, 0f) * MaximumLegendShareBeside;
            maximumHeight = Math.Max(plotBottom - plotTop, 0f)
                * (position == GraphLegendPosition.Right && panelWidth > 0 ? MaximumLegendShareWithPanel : 1f);
        }
        else
        {
            maximumWidth = Math.Max(content.Width - leftBand - (metrics.XTickLabelOverflow + panelBand), 0f);
            maximumHeight = Math.Max(content.Height, 0f) * MaximumLegendShareAboveOrBelow;
        }

        var arrangement = GraphLegendLayout.Arrange(
            metrics.LegendLabelWidths,
            metrics.LegendTitleWidth,
            metrics.LegendRowHeight,
            metrics.LegendMoreWidth,
            beside ? GraphLegendFlow.Columns : GraphLegendFlow.Rows,
            maximumWidth,
            maximumHeight);
        var width = arrangement.IsEmpty ? 0f : arrangement.Size.Width;
        var height = arrangement.IsEmpty ? 0f : arrangement.Size.Height;

        // Each side takes the legend's room, and a gap, from the plot.
        var right = position == GraphLegendPosition.Right && width > 0
            ? metrics.XTickLabelOverflow + Math.Max(width, panelWidth) + metrics.Gap
            : metrics.XTickLabelOverflow + panelBand;
        var left = leftBand + (position == GraphLegendPosition.Left && width > 0 ? width + metrics.Gap : 0f);
        var top = topBand + (position == GraphLegendPosition.Top && height > 0 ? height + metrics.Gap : 0f);
        var bottom = bottomBand + (position == GraphLegendPosition.Bottom && height > 0 ? height + metrics.Gap : 0f);

        var plotArea = new SKRect(
            content.Left + left, content.Top + top, content.Right - right, content.Bottom - bottom);
        if (plotArea.Width < MinimumPlotWidth || plotArea.Height < MinimumPlotHeight)
        {
            return new GraphLayout(canvas, SKRect.Empty, SKRect.Empty, SKRect.Empty, SKRect.Empty, SKRect.Empty);
        }

        var titleArea = titleHeight > 0
            ? new SKRect(content.Left, content.Top, content.Right, content.Top + titleHeight)
            : SKRect.Empty;

        var legendArea = SKRect.Empty;
        var panelArea = SKRect.Empty;
        if (position == GraphLegendPosition.Right)
        {
            (legendArea, panelArea) = RightColumnArranged(content, plotArea, metrics, width, height, panelWidth);
        }
        else
        {
            (_, panelArea) = RightColumn(content, plotArea, metrics, 0f, panelWidth, panelWidth);
            if (width > 0)
            {
                var origin = position switch
                {
                    GraphLegendPosition.Left => new SKPoint(content.Left, plotArea.Top),
                    GraphLegendPosition.Top => new SKPoint(
                        plotArea.Left, content.Top + titleHeight + (titleHeight > 0 ? metrics.Gap : 0f)),
                    _ => new SKPoint(plotArea.Left, content.Bottom - height)
                };
                legendArea = new SKRect(origin.X, origin.Y, origin.X + width, origin.Y + height);
            }
        }

        var labelArea = labelBand > 0
            ? new SKRect(plotArea.Left, plotArea.Top - labelBand, plotArea.Right, plotArea.Top)
            : SKRect.Empty;

        // The axes' own bands keep to their side of the legend: the X axis title above a legend below the plot, the Y
        // axis title right of a legend left of it.
        var xAxisArea = new SKRect(plotArea.Left, plotArea.Bottom, plotArea.Right, plotArea.Bottom + bottomBand);
        var yAxisArea = new SKRect(plotArea.Left - leftBand, plotArea.Top, plotArea.Left, plotArea.Bottom);

        return new GraphLayout(canvas, titleArea, plotArea, xAxisArea, yAxisArea, legendArea, panelArea, labelArea)
        {
            LegendArrangement = arrangement
        };
    }

    // The column right of the plot with an arranged legend: the legend on top, as tall as its arrangement, and the
    // statistics panel below it.
    private static (SKRect Legend, SKRect Panel) RightColumnArranged(
        SKRect content,
        SKRect plotArea,
        GraphLayoutMetrics metrics,
        float legendWidth,
        float legendHeight,
        float panelWidth)
    {
        if (legendWidth <= 0)
        {
            return RightColumn(content, plotArea, metrics, 0f, panelWidth, panelWidth);
        }

        var columnWidth = Math.Max(legendWidth, panelWidth);
        var left = panelWidth > 0 ? content.Right - columnWidth : content.Right - legendWidth;
        var legend = new SKRect(left, plotArea.Top, left + legendWidth, plotArea.Top + legendHeight);
        var panel = panelWidth > 0
            ? new SKRect(
                left, Math.Min(legend.Bottom + metrics.Gap, plotArea.Bottom), left + panelWidth, plotArea.Bottom)
            : SKRect.Empty;
        return (legend, panel);
    }

    private static bool HasText(string? text) => !string.IsNullOrWhiteSpace(text);

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
}
