using System.Globalization;
using SkiaSharp;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Draws a graph render model with SkiaSharp, and does nothing else: no statistics, no worksheet data, no knowledge of
// where the model came from. Every graph type shares this frame (background, plot area, grid, axes, ticks, labels,
// titles, legend); what goes inside the plot area is drawn by the graph type's own IGraphPlotRenderer.
//
// The renderer is stateless, so one instance can draw any model into any canvas.
//
// All text is drawn and measured through GraphTextFallback, so characters the graph font lacks come from a system font
// that has them, and the layout measures the text as it is drawn.
public sealed class SkiaGraphRenderer
{
    private const float LegendSwatchSize = 11f;
    private const float LegendPadding = 8f;
    private const float LegendMaximumWidth = 220f;
    private const float LegendEntrySpacing = 5f;

    // Ticks are drawn only where they land on the plot area. This tolerance keeps the ticks at the exact ends of the
    // range, which rounding can push a fraction of a pixel outside it.
    private const float EdgeTolerance = 0.5f;

    // plot: what this graph type draws inside the plot area, or null for a graph frame with nothing in it.
    public void Render(SKCanvas canvas, GraphRenderModel model, SKRect bounds, GraphTheme theme, IGraphPlotRenderer? plot = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(theme);

        if (!IsFinite(bounds) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        using var titleFont = Font(theme.TitleFontSize);
        using var axisTitleFont = Font(theme.AxisTitleFontSize);
        using var tickFont = Font(theme.TickLabelFontSize);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };

        fill.Color = theme.Background;
        canvas.DrawRect(bounds, fill);

        var layout = Arrange(bounds, model, titleFont, axisTitleFont, tickFont, out var metrics);
        if (!layout.HasPlotArea)
        {
            // Too small for a usable plot: the background alone, rather than axes drawn over each other.
            return;
        }

        var transform = new GraphCoordinateTransform(model.XAxis.Range, model.YAxis.Range, layout.PlotArea);

        fill.Color = theme.PlotBackground;
        canvas.DrawRect(layout.PlotArea, fill);

        // A categorical axis (a box plot's) labels only the categories it has room for (Task #060); any other, all its ticks.
        var xTicks = GraphCategoryLabels.Shown(
            model.XAxis,
            value => (float)transform.ToScreenX(value),
            label => GraphTextFallback.MeasureText(tickFont, CategoryLabel(label, tickFont, metrics)),
            theme.TickLabelFontSize / 2);

        DrawGrid(canvas, model, xTicks, layout, transform, theme, stroke);
        DrawPlot(canvas, layout, transform, theme, plot);
        DrawReferenceLines(canvas, model, layout, transform, theme);
        DrawAxisLines(canvas, layout, theme, stroke);
        DrawTicks(canvas, model, xTicks, layout, metrics, transform, theme, stroke, fill, tickFont);
        DrawAxisTitles(canvas, model, layout, theme, fill, axisTitleFont);
        DrawTitle(canvas, model, layout, theme, fill, titleFont);
        DrawReferenceLabels(canvas, model, layout, transform, theme, fill, tickFont);
        DrawLegend(canvas, model, layout, theme, fill, stroke, tickFont);
        DrawStatisticsPanel(canvas, model, layout, theme, fill, stroke, tickFont);
    }

    // How wide a tick label is drawn in this theme.
    internal static float TickLabelWidth(string label, GraphTheme theme)
    {
        using var tickFont = Font(theme.TickLabelFontSize);
        return GraphTextFallback.MeasureText(tickFont, label);
    }

    // The layout Render uses for this model on this canvas, measured with the theme's fonts: where the legend and the
    // statistics panel end up, for tests that look at what was drawn there.
    internal static GraphLayout Layout(GraphRenderModel model, SKRect bounds, GraphTheme theme)
    {
        using var titleFont = Font(theme.TitleFontSize);
        using var axisTitleFont = Font(theme.AxisTitleFontSize);
        using var tickFont = Font(theme.TickLabelFontSize);
        return Arrange(bounds, model, titleFont, axisTitleFont, tickFont, out _);
    }

    // The layout of this model on this canvas. Reference line labels need a band above the plot whose height depends on
    // how many rows their labels take, and that depends on where the lines fall across the plot. The plot's width does
    // not depend on the band's height, so the layout is worked out without the band first, the labels are placed along
    // that width, and the band they need is then reserved. A model without vertical lines is laid out once, as always.
    private static GraphLayout Arrange(
        SKRect bounds,
        GraphRenderModel model,
        SKFont titleFont,
        SKFont axisTitleFont,
        SKFont tickFont,
        out GraphLayoutMetrics metrics)
    {
        metrics = Measure(model, bounds, titleFont, axisTitleFont, tickFont);
        var layout = GraphLayoutCalculator.Calculate(bounds, model, metrics);
        if (!layout.HasPlotArea || !model.ReferenceLines.Any(line => line.Axis == GraphReferenceAxis.X))
        {
            return layout;
        }

        var rows = PlaceReferenceLabels(model, layout.PlotArea, tickFont).Select(label => label.Row + 1).DefaultIfEmpty(0).Max();
        if (rows == 0)
        {
            return layout;
        }

        metrics = metrics with { ReferenceLabelHeight = ReferenceLabelBandHeight(rows, tickFont) };
        return GraphLayoutCalculator.Calculate(bounds, model, metrics);
    }

    // The height of one line of the statistics panel in this theme.
    internal static float StatisticsRowHeight(GraphTheme theme)
    {
        using var tickFont = Font(theme.TickLabelFontSize);
        return Math.Max(LineHeight(tickFont), LegendSwatchSize);
    }

    // The renderer measures the text it is about to draw and hands the sizes to the layout, so the layout needs no font
    // of its own and the labels get the space they actually take.
    private static GraphLayoutMetrics Measure(GraphRenderModel model, SKRect bounds, SKFont titleFont, SKFont axisTitleFont, SKFont tickFont)
    {
        var categoryLabelWidth = CategoryLabelWidth(model, bounds);
        var hasAxisTitle = !string.IsNullOrWhiteSpace(model.XAxis.Title) || !string.IsNullOrWhiteSpace(model.YAxis.Title);

        // The legend's text is measured once here, for the width it has always been given and for arranging it.
        var legend = model.Legend;
        var legendLabels = legend is null
            ? []
            : legend.Entries.Select(entry => GraphTextFallback.MeasureText(tickFont, entry.Label)).ToArray();
        var legendTitle = legend is null || string.IsNullOrWhiteSpace(legend.Title)
            ? 0f
            : GraphTextFallback.MeasureText(tickFont, legend.Title);

        return new GraphLayoutMetrics
        {
            TitleHeight = string.IsNullOrWhiteSpace(model.Title) ? 0f : LineHeight(titleFont),
            AxisTitleHeight = hasAxisTitle ? LineHeight(axisTitleFont) : 0f,
            TickLabelHeight = LineHeight(tickFont),
            YTickLabelWidth = WidestLabel(model.YAxis, tickFont),
            XTickLabelOverflow = Math.Min(WidestLabel(model.XAxis, tickFont), categoryLabelWidth) / 2f,
            CategoryLabelWidth = categoryLabelWidth,
            LegendWidth = LegendWidth(legend, legendTitle, legendLabels),
            LegendHeight = legend is null ? 0f : LegendBoxHeight(legend, tickFont),
            LegendLabelWidths = legendLabels,
            LegendTitleWidth = legendTitle,
            LegendRowHeight = legend is null ? 0f : Math.Max(LineHeight(tickFont), LegendSwatchSize),
            LegendMoreWidth = legend is null
                ? 0f
                : GraphTextFallback.MeasureText(tickFont, MoreText(legend.Entries.Count, digitsOnly: true)),
            StatisticsPanelWidth = model.StatisticsPanel is null ? 0f : StatisticsPanelWidth(model.StatisticsPanel, tickFont)
        };
    }

    private static void DrawGrid(
        SKCanvas canvas,
        GraphRenderModel model,
        IReadOnlyList<GraphAxisTick> xTicks,
        GraphLayout layout,
        GraphCoordinateTransform transform,
        GraphTheme theme,
        SKPaint stroke)
    {
        // A hidden grid (Task #046) is not drawn at all - not drawn in a colour that cannot be seen.
        if (!theme.ShowGrid)
        {
            return;
        }

        stroke.Color = theme.Grid;
        stroke.StrokeWidth = theme.GridThickness;

        var restore = canvas.Save();
        canvas.ClipRect(layout.PlotArea);

        foreach (var tick in xTicks)
        {
            var x = (float)transform.ToScreenX(tick.Value);
            if (IsVisible(x, layout.PlotArea.Left, layout.PlotArea.Right))
            {
                canvas.DrawLine(x, layout.PlotArea.Top, x, layout.PlotArea.Bottom, stroke);
            }
        }

        foreach (var tick in model.YAxis.Ticks)
        {
            var y = (float)transform.ToScreenY(tick.Value);
            if (IsVisible(y, layout.PlotArea.Top, layout.PlotArea.Bottom))
            {
                canvas.DrawLine(layout.PlotArea.Left, y, layout.PlotArea.Right, y, stroke);
            }
        }

        canvas.RestoreToCount(restore);
    }

    // The data of the graph, over the grid and under the axes. The clip is what keeps a point outside the axis ranges
    // from being drawn over the tick labels, the titles, the legend or the window behind them; the transform itself
    // still places such a point where it belongs, outside the plot area.
    private static void DrawPlot(
        SKCanvas canvas,
        GraphLayout layout,
        GraphCoordinateTransform transform,
        GraphTheme theme,
        IGraphPlotRenderer? plot)
    {
        if (plot is null)
        {
            return;
        }

        var restore = canvas.Save();
        canvas.ClipRect(layout.PlotArea);
        plot.RenderPlot(canvas, transform, theme);
        canvas.RestoreToCount(restore);
    }

    // The axes themselves: the bottom and the left edge of the plot area.
    private static void DrawAxisLines(SKCanvas canvas, GraphLayout layout, GraphTheme theme, SKPaint stroke)
    {
        stroke.Color = theme.Axis;
        stroke.StrokeWidth = theme.AxisThickness;
        canvas.DrawLine(layout.PlotArea.Left, layout.PlotArea.Bottom, layout.PlotArea.Right, layout.PlotArea.Bottom, stroke);
        canvas.DrawLine(layout.PlotArea.Left, layout.PlotArea.Top, layout.PlotArea.Left, layout.PlotArea.Bottom, stroke);
    }

    private static void DrawTicks(
        SKCanvas canvas,
        GraphRenderModel model,
        IReadOnlyList<GraphAxisTick> xTicks,
        GraphLayout layout,
        GraphLayoutMetrics metrics,
        GraphCoordinateTransform transform,
        GraphTheme theme,
        SKPaint stroke,
        SKPaint fill,
        SKFont tickFont)
    {
        stroke.Color = theme.Axis;
        stroke.StrokeWidth = theme.AxisThickness;
        fill.Color = theme.SecondaryText;

        var fontMetrics = tickFont.Metrics;
        var labelBaseline = layout.PlotArea.Bottom + metrics.TickLength + metrics.Gap - fontMetrics.Ascent;
        var labelRight = layout.PlotArea.Left - metrics.TickLength - metrics.Gap;

        foreach (var tick in xTicks)
        {
            var x = (float)transform.ToScreenX(tick.Value);
            if (!IsVisible(x, layout.PlotArea.Left, layout.PlotArea.Right))
            {
                continue;
            }

            canvas.DrawLine(x, layout.PlotArea.Bottom, x, layout.PlotArea.Bottom + metrics.TickLength, stroke);
            GraphTextFallback.DrawText(canvas, CategoryLabel(tick.Label, tickFont, metrics), x, labelBaseline, SKTextAlign.Center, tickFont, fill);
        }

        foreach (var tick in model.YAxis.Ticks)
        {
            var y = (float)transform.ToScreenY(tick.Value);
            if (!IsVisible(y, layout.PlotArea.Top, layout.PlotArea.Bottom))
            {
                continue;
            }

            canvas.DrawLine(layout.PlotArea.Left - metrics.TickLength, y, layout.PlotArea.Left, y, stroke);
            GraphTextFallback.DrawText(
                canvas,
                tick.Label,
                labelRight,
                CenteredBaseline(y, fontMetrics),
                SKTextAlign.Right,
                tickFont,
                fill);
        }
    }

    private static void DrawAxisTitles(
        SKCanvas canvas,
        GraphRenderModel model,
        GraphLayout layout,
        GraphTheme theme,
        SKPaint fill,
        SKFont axisTitleFont)
    {
        fill.Color = theme.Text;
        var fontMetrics = axisTitleFont.Metrics;

        if (!string.IsNullOrWhiteSpace(model.XAxis.Title))
        {
            var center = XAxisTitleCenter(layout, axisTitleFont);
            GraphTextFallback.DrawText(
                canvas,
                model.XAxis.Title,
                center.X,
                CenteredBaseline(center.Y, fontMetrics),
                SKTextAlign.Center,
                axisTitleFont,
                fill);
        }

        if (!string.IsNullOrWhiteSpace(model.YAxis.Title))
        {
            // Turned a quarter turn anticlockwise: the glyphs then grow to the left of the baseline, so the baseline is
            // offset by half the text band to centre the title on the left edge of the Y axis area.
            var center = YAxisTitleCenter(layout, axisTitleFont);
            var restore = canvas.Save();
            canvas.Translate(center.X - ((fontMetrics.Ascent + fontMetrics.Descent) / 2f), center.Y);
            canvas.RotateDegrees(-90);
            GraphTextFallback.DrawText(canvas, model.YAxis.Title, 0, 0, SKTextAlign.Center, axisTitleFont, fill);
            canvas.RestoreToCount(restore);
        }
    }

    private static void DrawTitle(SKCanvas canvas, GraphRenderModel model, GraphLayout layout, GraphTheme theme, SKPaint fill, SKFont titleFont)
    {
        if (string.IsNullOrWhiteSpace(model.Title))
        {
            return;
        }

        fill.Color = theme.Text;
        var center = TitleCenter(layout);
        GraphTextFallback.DrawText(
            canvas,
            model.Title,
            center.X,
            CenteredBaseline(center.Y, titleFont.Metrics),
            SKTextAlign.Center,
            titleFont,
            fill);
    }

    // ---- Where the titles are ----
    //
    // The centre of each title's line, which the drawing above and the label geometry below both read, so the area a
    // title can be picked in is where it is drawn. The Y axis title is centred on its line before it is turned.

    private static SKPoint TitleCenter(GraphLayout layout) => new(layout.PlotArea.MidX, layout.TitleArea.MidY);

    private static SKPoint XAxisTitleCenter(GraphLayout layout, SKFont axisTitleFont) =>
        new(layout.PlotArea.MidX, layout.XAxisArea.Bottom - (LineHeight(axisTitleFont) / 2f));

    private static SKPoint YAxisTitleCenter(GraphLayout layout, SKFont axisTitleFont) =>
        new(layout.YAxisArea.Left + (LineHeight(axisTitleFont) / 2f), layout.PlotArea.MidY);

    // The titles this model shows on a canvas this size, each as the box its line of text takes - as wide as the text
    // is drawn and as tall as the line, standing on end for the turned Y axis title - with the area of the layout it
    // belongs to. A title the model does not have, or a canvas too small for a plot, gives none. Geometry only: which
    // title is under a pointer is GraphLabelHitTest's business.
    internal static IReadOnlyList<GraphLabelGeometry> LabelGeometry(
        GraphRenderModel model,
        SKRect bounds,
        GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(theme);

        if (!IsFinite(bounds) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return [];
        }

        using var titleFont = Font(theme.TitleFontSize);
        using var axisTitleFont = Font(theme.AxisTitleFontSize);
        using var tickFont = Font(theme.TickLabelFontSize);
        var layout = Arrange(bounds, model, titleFont, axisTitleFont, tickFont, out _);
        if (!layout.HasPlotArea)
        {
            return [];
        }

        var labels = new List<GraphLabelGeometry>(3);
        if (!string.IsNullOrWhiteSpace(model.Title))
        {
            var width = GraphTextFallback.MeasureText(titleFont, model.Title);
            var box = Box(TitleCenter(layout), width, LineHeight(titleFont));
            labels.Add(new GraphLabelGeometry(GraphLabelField.Title, box, layout.TitleArea));
        }

        if (!string.IsNullOrWhiteSpace(model.XAxis.Title))
        {
            var width = GraphTextFallback.MeasureText(axisTitleFont, model.XAxis.Title);
            var box = Box(XAxisTitleCenter(layout, axisTitleFont), width, LineHeight(axisTitleFont));
            labels.Add(new GraphLabelGeometry(GraphLabelField.XAxisTitle, box, layout.XAxisArea));
        }

        if (!string.IsNullOrWhiteSpace(model.YAxis.Title))
        {
            var width = GraphTextFallback.MeasureText(axisTitleFont, model.YAxis.Title);
            var box = Box(YAxisTitleCenter(layout, axisTitleFont), LineHeight(axisTitleFont), width);
            labels.Add(new GraphLabelGeometry(GraphLabelField.YAxisTitle, box, layout.YAxisArea));
        }

        return labels;
    }

    // The axes this model shows on a canvas this size as they can be picked (Task #052), from the very layout Render
    // draws them with: each axis's area of the layout and the plot edge its line is drawn on, X's area reaching on to
    // the right by the half tick label the last X tick overhangs the plot with - but never into the legend or the
    // statistics panel. A canvas too small for a plot gives none. Geometry only: which axis is under a pointer is
    // GraphAxisHitTest's business, and whether that axis can be edited is the graph type's.
    internal static GraphAxesGeometry AxisGeometry(GraphRenderModel model, SKRect bounds, GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(theme);

        if (!IsFinite(bounds) || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return GraphAxesGeometry.None;
        }

        using var titleFont = Font(theme.TitleFontSize);
        using var axisTitleFont = Font(theme.AxisTitleFontSize);
        using var tickFont = Font(theme.TickLabelFontSize);
        var layout = Arrange(bounds, model, titleFont, axisTitleFont, tickFont, out var metrics);
        if (!layout.HasPlotArea)
        {
            return GraphAxesGeometry.None;
        }

        var excluded = new[] { layout.TitleArea, layout.LegendArea, layout.StatisticsPanelArea, layout.ReferenceLabelArea }
            .Where(area => !area.IsEmpty)
            .ToList();

        var xArea = layout.XAxisArea;
        var right = Math.Min(xArea.Right + metrics.XTickLabelOverflow, layout.Canvas.Right);
        foreach (var area in excluded.Where(area => area.Left >= xArea.Right && area.Top < xArea.Bottom && area.Bottom > xArea.Top))
        {
            right = Math.Min(right, area.Left);
        }

        xArea.Right = Math.Max(xArea.Right, right);
        var plot = layout.PlotArea;
        return new GraphAxesGeometry(
            [
                new GraphAxisGeometry(
                    GraphAxisField.X,
                    xArea,
                    GraphAxisHitTest.LineBand(new SKPoint(plot.Left, plot.Bottom), new SKPoint(plot.Right, plot.Bottom), theme.AxisThickness)),
                new GraphAxisGeometry(
                    GraphAxisField.Y,
                    layout.YAxisArea,
                    GraphAxisHitTest.LineBand(new SKPoint(plot.Left, plot.Top), new SKPoint(plot.Left, plot.Bottom), theme.AxisThickness))
            ],
            excluded);
    }

    private static SKRect Box(SKPoint center, float width, float height) =>
        new(center.X - (width / 2f), center.Y - (height / 2f), center.X + (width / 2f), center.Y + (height / 2f));

    // The legend foundation: the reserved area with one row per series, drawn only when the model has series. There is
    // no interaction, and an empty legend is never laid out.
    private static void DrawLegend(
        SKCanvas canvas,
        GraphRenderModel model,
        GraphLayout layout,
        GraphTheme theme,
        SKPaint fill,
        SKPaint stroke,
        SKFont font)
    {
        if (model.Legend is null || layout.LegendArea.IsEmpty)
        {
            return;
        }

        // A legend on another side, or too big for the single column it always had, as the layout arranged it.
        if (layout.LegendArrangement is { } arrangement)
        {
            DrawArrangedLegend(canvas, model.Legend, layout.LegendArea, arrangement, theme, fill, stroke, font);
            return;
        }

        var fontMetrics = font.Metrics;
        var rowHeight = Math.Max(LineHeight(font), LegendSwatchSize);

        // The box keeps to its rows instead of filling the reserved area, which stays as tall as the plot so that a
        // legend with many series still has somewhere to go.
        var box = layout.LegendArea;
        box.Bottom = Math.Min(box.Top + LegendBoxHeight(model.Legend, font), layout.LegendArea.Bottom);

        stroke.Color = theme.LegendBorder;
        stroke.StrokeWidth = 1f;
        canvas.DrawRect(box, stroke);

        var top = box.Top + LegendPadding;
        var left = box.Left + LegendPadding;

        var restore = canvas.Save();
        canvas.ClipRect(box);

        if (!string.IsNullOrWhiteSpace(model.Legend.Title))
        {
            fill.Color = theme.Text;
            GraphTextFallback.DrawText(
                canvas,
                model.Legend.Title,
                left,
                top - fontMetrics.Ascent,
                SKTextAlign.Left,
                font,
                fill);
            top += rowHeight + LegendEntrySpacing;
        }

        foreach (var entry in model.Legend.Entries)
        {
            var centerY = top + (rowHeight / 2f);

            fill.Color = theme.SeriesColor(entry.SeriesIndex);
            canvas.DrawRect(
                new SKRect(left, centerY - (LegendSwatchSize / 2f), left + LegendSwatchSize, centerY + (LegendSwatchSize / 2f)),
                fill);

            fill.Color = theme.SecondaryText;
            GraphTextFallback.DrawText(
                canvas,
                entry.Label,
                left + LegendSwatchSize + LegendEntrySpacing,
                CenteredBaseline(centerY, fontMetrics),
                SKTextAlign.Left,
                font,
                fill);

            top += rowHeight + LegendEntrySpacing;
        }

        canvas.RestoreToCount(restore);
    }

    // A legend as GraphLegendLayout arranged it (Task #044): its box, the title across its top or leading its first
    // row, and each cell - a swatch and a label, or "… N more" for the entries that did not fit. A label longer than
    // its cell is cut short with an ellipsis rather than cut through a glyph; colours, labels and their order are the
    // legend's own.
    private static void DrawArrangedLegend(
        SKCanvas canvas,
        GraphLegendModel legend,
        SKRect box,
        GraphLegendArrangement arrangement,
        GraphTheme theme,
        SKPaint fill,
        SKPaint stroke,
        SKFont font)
    {
        var fontMetrics = font.Metrics;

        stroke.Color = theme.LegendBorder;
        stroke.StrokeWidth = 1f;
        canvas.DrawRect(box, stroke);

        var restore = canvas.Save();
        canvas.ClipRect(box);

        if (arrangement.TitleWidth > 0 && !string.IsNullOrWhiteSpace(legend.Title))
        {
            fill.Color = theme.Text;
            GraphTextFallback.DrawText(
                canvas,
                Ellipsize(legend.Title, font, arrangement.TitleWidth),
                box.Left + GraphLegendLayout.Padding,
                box.Top + GraphLegendLayout.Padding - fontMetrics.Ascent,
                SKTextAlign.Left,
                font,
                fill);
        }

        foreach (var cell in arrangement.Cells)
        {
            var bounds = cell.Bounds;
            bounds.Offset(box.Left, box.Top);
            var baseline = CenteredBaseline(bounds.MidY, fontMetrics);

            if (cell.IsMore)
            {
                fill.Color = theme.SecondaryText;
                var more = Ellipsize(MoreText(arrangement.HiddenCount), font, cell.LabelWidth);
                GraphTextFallback.DrawText(canvas, more, bounds.Left, baseline, SKTextAlign.Left, font, fill);
                continue;
            }

            var entry = legend.Entries[cell.EntryIndex];
            fill.Color = theme.SeriesColor(entry.SeriesIndex);
            var half = LegendSwatchSize / 2f;
            canvas.DrawRect(
                new SKRect(bounds.Left, bounds.MidY - half, bounds.Left + LegendSwatchSize, bounds.MidY + half),
                fill);

            fill.Color = theme.SecondaryText;
            GraphTextFallback.DrawText(
                canvas,
                Ellipsize(entry.Label, font, cell.LabelWidth),
                bounds.Left + LegendSwatchSize + LegendEntrySpacing,
                baseline,
                SKTextAlign.Left,
                font,
                fill);
        }

        canvas.RestoreToCount(restore);
    }

    // "… 12 more": what stands for the entries of a legend that did not fit. With digitsOnly, the widest it can be for
    // a legend of this many entries (every digit a 0), to measure the room it needs.
    internal static string MoreText(int hidden, bool digitsOnly = false) =>
        digitsOnly
            ? $"… {new string('0', hidden.ToString(CultureInfo.InvariantCulture).Length)} more"
            : $"… {hidden.ToString(CultureInfo.InvariantCulture)} more";

    // ---- Reference lines ----
    //
    // Lines across the plot at chosen values, over the data and under the axes, clipped to the plot area like the data.
    // They are drawn in the theme's neutral annotation colour, never a series colour: a specification limit dashed, a
    // target a little heavier with a long-short dash, so the two read apart without colour meaning good or bad.
    //
    // The label of a vertical line sits in the band above the plot, over its line. Labels that would run into each
    // other move up a row - the first row, nearest the plot, that has room - and a label near either end of the plot is
    // pushed inward so that it stays over the plot instead of running off it.

    private const float ReferenceLabelGap = 6f;
    private const float ReferenceLabelRowSpacing = 2f;
    private const float ReferenceLabelBottomGap = 3f;

    private static readonly float[] SpecificationLimitDash = [6f, 4f];
    private static readonly float[] TargetDash = [12f, 3f, 3f, 3f];

    // Where one label of a vertical reference line goes: its left edge and width across the plot, and its row in the
    // band (0 is the row nearest the plot).
    internal readonly record struct ReferenceLabelPlacement(GraphReferenceLine Line, float Left, float Width, int Row);

    // The labels of the model's vertical lines that land on the plot, placed across a plot area. Lines are taken from
    // left to right (in model order where they coincide); each label is centred on its line, moved inward when that
    // would take it past either end of the plot, and put in the lowest row where it clears every label already there.
    internal static IReadOnlyList<ReferenceLabelPlacement> PlaceReferenceLabels(GraphRenderModel model, SKRect plotArea, SKFont font)
    {
        if (plotArea.Width <= 0 || plotArea.Height <= 0)
        {
            return [];
        }

        var transform = new GraphCoordinateTransform(model.XAxis.Range, model.YAxis.Range, plotArea);
        var candidates = model.ReferenceLines
            .Select((line, order) => (Line: line, Order: order, X: (float)transform.ToScreenX(line.Value)))
            .Where(candidate => candidate.Line.Axis == GraphReferenceAxis.X
                && !string.IsNullOrEmpty(candidate.Line.Label)
                && IsVisible(candidate.X, plotArea.Left, plotArea.Right))
            .OrderBy(candidate => candidate.X)
            .ThenBy(candidate => candidate.Order);

        var rowEnds = new List<float>();
        var placements = new List<ReferenceLabelPlacement>();
        foreach (var candidate in candidates)
        {
            var width = GraphTextFallback.MeasureText(font, candidate.Line.Label);
            var left = Math.Clamp(candidate.X - (width / 2f), plotArea.Left, Math.Max(plotArea.Left, plotArea.Right - width));

            var row = rowEnds.FindIndex(end => end + ReferenceLabelGap <= left);
            if (row < 0)
            {
                row = rowEnds.Count;
                rowEnds.Add(0f);
            }

            rowEnds[row] = left + width;
            placements.Add(new ReferenceLabelPlacement(candidate.Line, left, width, row));
        }

        return placements;
    }

    // The band that holds this many rows of labels, with a little room between the lowest row and the plot.
    internal static float ReferenceLabelBandHeight(int rows, SKFont font) =>
        rows <= 0 ? 0f : ReferenceLabelBottomGap + (rows * LineHeight(font)) + ((rows - 1) * ReferenceLabelRowSpacing);

    private static void DrawReferenceLines(
        SKCanvas canvas,
        GraphRenderModel model,
        GraphLayout layout,
        GraphCoordinateTransform transform,
        GraphTheme theme)
    {
        if (model.ReferenceLines.Count == 0)
        {
            return;
        }

        var plotArea = layout.PlotArea;
        using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = theme.Annotation };
        using var limitDash = SKPathEffect.CreateDash(SpecificationLimitDash, 0f);
        using var targetDash = SKPathEffect.CreateDash(TargetDash, 0f);

        var restore = canvas.Save();
        canvas.ClipRect(plotArea);

        foreach (var line in model.ReferenceLines)
        {
            var isTarget = line.Kind == GraphReferenceLineKind.Target;
            stroke.StrokeWidth = isTarget ? theme.TargetThickness : theme.SpecificationLimitThickness;
            stroke.PathEffect = isTarget ? targetDash : limitDash;

            if (line.Axis == GraphReferenceAxis.X)
            {
                var x = (float)transform.ToScreenX(line.Value);
                if (IsVisible(x, plotArea.Left, plotArea.Right))
                {
                    // Drawn from the bottom up, so every line's dash pattern starts at the axis.
                    canvas.DrawLine(x, plotArea.Bottom, x, plotArea.Top, stroke);
                }
            }
            else
            {
                var y = (float)transform.ToScreenY(line.Value);
                if (IsVisible(y, plotArea.Top, plotArea.Bottom))
                {
                    canvas.DrawLine(plotArea.Left, y, plotArea.Right, y, stroke);
                }
            }
        }

        stroke.PathEffect = null;
        canvas.RestoreToCount(restore);
    }

    private static void DrawReferenceLabels(
        SKCanvas canvas,
        GraphRenderModel model,
        GraphLayout layout,
        GraphCoordinateTransform transform,
        GraphTheme theme,
        SKPaint fill,
        SKFont font)
    {
        if (model.ReferenceLines.Count == 0)
        {
            return;
        }

        fill.Color = theme.Annotation;
        var fontMetrics = font.Metrics;
        var lineHeight = LineHeight(font);

        var band = layout.ReferenceLabelArea;
        if (!band.IsEmpty)
        {
            var restore = canvas.Save();
            canvas.ClipRect(band);
            foreach (var label in PlaceReferenceLabels(model, layout.PlotArea, font))
            {
                var bottom = band.Bottom - ReferenceLabelBottomGap - (label.Row * (lineHeight + ReferenceLabelRowSpacing));
                GraphTextFallback.DrawText(
                    canvas,
                    label.Line.Label,
                    label.Left,
                    bottom - fontMetrics.Descent,
                    SKTextAlign.Left,
                    font,
                    fill);
            }

            canvas.RestoreToCount(restore);
        }

        // A horizontal line has no band: its label sits just above it at the right end of the plot. No graph draws
        // one yet; the frame handles both axes so that the line primitive means the same on either.
        var plotArea = layout.PlotArea;
        var plotRestore = canvas.Save();
        canvas.ClipRect(plotArea);
        foreach (var line in model.ReferenceLines.Where(line => line.Axis == GraphReferenceAxis.Y))
        {
            var y = (float)transform.ToScreenY(line.Value);
            if (IsVisible(y, plotArea.Top, plotArea.Bottom))
            {
                GraphTextFallback.DrawText(
                    canvas,
                    line.Label,
                    plotArea.Right - ReferenceLabelGap,
                    y - ReferenceLabelBottomGap - fontMetrics.Descent,
                    SKTextAlign.Right,
                    font,
                    fill);
            }
        }

        canvas.RestoreToCount(plotRestore);
    }

    // ---- Statistics panel ----
    //
    // The statistics beside the plot, in a box styled like the legend. Ungrouped, it is a title and a row per
    // statistic - Mean, StDev, N, or those of them the panel shows - with the value right-aligned. Grouped, it is a
    // table: a header with the grouping column's name and the statistic names, then one row per group with the group's
    // colour swatch, its label on the left and its numbers on the right - or, when the table is too wide for the panel
    // (Task #062: up to eight statistics), a block per group: its swatch and label, then a line per statistic. Which of
    // the two is decided from the panel's width each time it is drawn. Every text was decided when the model was built;
    // this only places it.
    //
    // Nothing scrolls and no font shrinks: a label that does not fit the panel's width ends in an ellipsis, and when
    // not every group fits the panel's height, the last row that does says how many more there are.

    private const float StatisticsMaximumWidth = 280f;
    private const float StatisticsColumnGap = 10f;
    private const float StatisticsMinimumLabelWidth = 24f;

    // The name of each statistic, as the panel heads it.
    private static string StatisticName(GraphStatisticsItem item) => item switch
    {
        GraphStatisticsItem.Mean => "Mean",
        GraphStatisticsItem.StandardDeviation => "StDev",
        GraphStatisticsItem.Count => "N",
        GraphStatisticsItem.Minimum => "Min",
        GraphStatisticsItem.FirstQuartile => "Q1",
        GraphStatisticsItem.Median => "Median",
        GraphStatisticsItem.ThirdQuartile => "Q3",
        _ => "Max"
    };

    // A row's text for each statistic, as it was decided when the row was built.
    private static string StatisticText(GraphStatisticsRow row, GraphStatisticsItem item) => item switch
    {
        GraphStatisticsItem.Mean => row.MeanText,
        GraphStatisticsItem.StandardDeviation => row.StandardDeviationText,
        GraphStatisticsItem.Count => row.CountText,
        GraphStatisticsItem.Minimum => FiveNumberText(row, five => five.MinimumText),
        GraphStatisticsItem.FirstQuartile => FiveNumberText(row, five => five.FirstQuartileText),
        GraphStatisticsItem.Median => FiveNumberText(row, five => five.MedianText),
        GraphStatisticsItem.ThirdQuartile => FiveNumberText(row, five => five.ThirdQuartileText),
        _ => FiveNumberText(row, five => five.MaximumText)
    };

    // A five-number summary text (Task #062), or "—" for a row made without the summary.
    private static string FiveNumberText(GraphStatisticsRow row, Func<GraphFiveNumberSummary, string> text) =>
        row.FiveNumbers is { } five ? text(five) : GraphStatisticsPanelBuilder.UndefinedText;

    // The names of the statistics the panel shows (Task #045), in the order it shows them.
    private static string[] StatisticsNames(GraphStatisticsPanel panel) => [.. panel.Items.Select(StatisticName)];

    // The width the panel's content asks for, capped at its maximum width.
    private static float StatisticsPanelWidth(GraphStatisticsPanel panel, SKFont font)
    {
        var width = GraphTextFallback.MeasureText(font, panel.Title);
        if (panel.IsGrouped)
        {
            var (labelWidth, numberWidths) = StatisticsColumns(panel, font);
            width = Math.Max(width, LegendSwatchSize + LegendEntrySpacing + labelWidth + numberWidths.Sum(column => StatisticsColumnGap + column));
        }
        else
        {
            var row = panel.Rows[0];
            var labels = StatisticsNames(panel).Max(name => GraphTextFallback.MeasureText(font, name));
            var values = UngroupedValues(panel, row).Max(value => GraphTextFallback.MeasureText(font, value));
            width = Math.Max(width, labels + StatisticsColumnGap + values);
        }

        return Math.Min(width + (LegendPadding * 2f), StatisticsMaximumWidth);
    }

    // The label column and the number columns of a grouped panel - one per statistic it shows - each as wide as its
    // widest text.
    private static (float Label, float[] Numbers) StatisticsColumns(GraphStatisticsPanel panel, SKFont font)
    {
        var label = GraphTextFallback.MeasureText(font, panel.GroupHeader ?? string.Empty);
        var items = panel.Items;
        var numbers = StatisticsNames(panel).Select(name => GraphTextFallback.MeasureText(font, name)).ToArray();
        foreach (var row in panel.Rows)
        {
            label = Math.Max(label, GraphTextFallback.MeasureText(font, row.Label));
            for (var column = 0; column < items.Count; column++)
            {
                numbers[column] = Math.Max(
                    numbers[column], GraphTextFallback.MeasureText(font, StatisticText(row, items[column])));
            }
        }

        return (label, numbers);
    }

    private static string[] UngroupedValues(GraphStatisticsPanel panel, GraphStatisticsRow row) =>
        [.. panel.Items.Select(item => StatisticText(row, item))];

    private static void DrawStatisticsPanel(
        SKCanvas canvas,
        GraphRenderModel model,
        GraphLayout layout,
        GraphTheme theme,
        SKPaint fill,
        SKPaint stroke,
        SKFont font)
    {
        var panel = model.StatisticsPanel;
        var area = layout.StatisticsPanelArea;
        if (panel is null || area.IsEmpty || area.Height <= 0)
        {
            return;
        }

        var rowHeight = Math.Max(LineHeight(font), LegendSwatchSize);
        var fontMetrics = font.Metrics;

        // A grouped table too wide for the panel - more statistics than its columns have room for (Task #062) - is
        // drawn as a block per series instead, decided here from the width the panel has, every time it is drawn.
        var stacked = panel.IsGrouped && !FitsAsTable(panel, font, area.Width - (LegendPadding * 2f));
        var headerLines = panel.IsGrouped && !stacked ? 2 : 1;
        var dataLines = panel.IsGrouped ? panel.Rows.Count : panel.Items.Count;
        int shownData;
        int lines;
        bool overflow;
        var partial = 0;
        var more = 0;
        if (stacked)
        {
            (shownData, partial, more) = FitStatisticsBlocks(panel, area.Height, rowHeight);
            overflow = more > 0;
            // A first series shown in part takes its label, its statistics shown and "…".
            lines = headerLines + (shownData * (1 + panel.Items.Count)) + (partial > 0 ? partial + 2 : 0)
                + (overflow ? 1 : 0);
        }
        else
        {
            (shownData, _) = FitStatisticsLines(panel, area.Height, rowHeight);
            overflow = shownData < dataLines;
            lines = headerLines + shownData;
        }

        var box = area;
        box.Bottom = Math.Min(box.Top + (LegendPadding * 2f) + (lines * rowHeight) + (Math.Max(lines - 1, 0) * LegendEntrySpacing), area.Bottom);

        stroke.Color = theme.LegendBorder;
        stroke.StrokeWidth = 1f;
        canvas.DrawRect(box, stroke);

        var restore = canvas.Save();
        canvas.ClipRect(box);

        var left = box.Left + LegendPadding;
        var right = box.Right - LegendPadding;
        var top = box.Top + LegendPadding;

        fill.Color = theme.Text;
        GraphTextFallback.DrawText(
            canvas,
            Ellipsize(panel.Title, font, right - left),
            left,
            top - fontMetrics.Ascent,
            SKTextAlign.Left,
            font,
            fill);
        top += rowHeight + LegendEntrySpacing;

        if (stacked)
        {
            DrawStatisticsBlocks(
                canvas, panel, theme, fill, font, left, right, top, rowHeight, shownData, partial, more);
        }
        else if (panel.IsGrouped)
        {
            DrawStatisticsTable(canvas, panel, theme, fill, font, left, right, top, rowHeight, shownData, overflow);
        }
        else
        {
            var names = StatisticsNames(panel);
            var values = UngroupedValues(panel, panel.Rows[0]);
            for (var line = 0; line < shownData; line++)
            {
                var baseline = CenteredBaseline(top + (rowHeight / 2f), fontMetrics);
                if (overflow && line == shownData - 1)
                {
                    DrawMore(canvas, theme, fill, font, left, baseline, dataLines - line);
                    break;
                }

                fill.Color = theme.SecondaryText;
                GraphTextFallback.DrawText(canvas, names[line], left, baseline, SKTextAlign.Left, font, fill);
                fill.Color = theme.Text;
                GraphTextFallback.DrawText(canvas, values[line], right, baseline, SKTextAlign.Right, font, fill);
                top += rowHeight + LegendEntrySpacing;
            }
        }

        canvas.RestoreToCount(restore);
    }

    // How many data lines of the panel fit a panel area this tall - the "… k more" line included when there is one -
    // and how many rows that line stands in for (0 when everything fits). The lines are the title, the header of a
    // table, then one per group or, ungrouped, one per statistic.
    internal static (int Lines, int More) FitStatisticsLines(GraphStatisticsPanel panel, float height, float rowHeight)
    {
        var headerLines = panel.IsGrouped ? 2 : 1;
        var dataLines = panel.IsGrouped ? panel.Rows.Count : panel.Items.Count;

        // As many data lines as the height allows; if some do not fit, the last one that does becomes "… k more".
        var available = height - (LegendPadding * 2f) + LegendEntrySpacing;
        var fitting = Math.Max(0, (int)Math.Floor(available / (rowHeight + LegendEntrySpacing)) - headerLines);
        var lines = Math.Min(dataLines, fitting);
        return (lines, lines > 0 && lines < dataLines ? dataLines - lines + 1 : 0);
    }

    // Whether a grouped panel's table fits this width: every statistic's column at its own width, and the labels
    // whole or at least StatisticsMinimumLabelWidth wide - the least the table has ever given them.
    internal static bool FitsAsTable(GraphStatisticsPanel panel, SKFont font, float width)
    {
        var (labelWidth, numberWidths) = StatisticsColumns(panel, font);
        var needed = LegendSwatchSize + LegendEntrySpacing + Math.Min(labelWidth, StatisticsMinimumLabelWidth)
            + numberWidths.Sum(column => StatisticsColumnGap + column);
        // A panel sized to its table gets that width back from the layout's arithmetic, give or take a rounding error.
        return needed <= width + 0.5f;
    }

    // How a stacked panel (Task #062) fits a panel area this tall: how many whole series blocks - a block is the
    // series' swatch and label, then a line per statistic - and how many series the "… k more" line under them stands
    // in for (0 when every block fits). The title takes the first line.
    //
    // When not even one whole block fits, the first series is still shown as far as it goes - its label and as many of
    // its statistics as there is room for, then "…" - when that is at least one statistic (Partial: how many); only
    // when there is room for less than that is the panel just "… k more".
    internal static (int Blocks, int Partial, int More) FitStatisticsBlocks(
        GraphStatisticsPanel panel, float height, float rowHeight)
    {
        var available = height - (LegendPadding * 2f) + LegendEntrySpacing;
        var lines = Math.Max(0, (int)Math.Floor(available / (rowHeight + LegendEntrySpacing)) - 1);
        var block = 1 + panel.Items.Count;
        var rows = panel.Rows.Count;
        if (lines >= rows * block)
        {
            return (rows, 0, 0);
        }

        // Not every block fits: the last line says how many series are left out.
        var blocks = Math.Min(rows, Math.Max(0, (lines - 1) / block));
        if (blocks > 0 || lines < 1)
        {
            return (blocks, 0, lines >= 1 ? rows - blocks : 0);
        }

        // Not one whole block: the first series' label, what fits of its statistics, "…" for the rest, and - with
        // more series - "… k more" for them.
        var more = rows > 1 ? 1 : 0;
        var partial = Math.Min(panel.Items.Count - 1, lines - 1 - 1 - more);
        return partial >= 1 ? (0, partial, rows - 1) : (0, 0, rows);
    }

    // A grouped panel too wide for a table (Task #062): a block per series - its colour swatch and label, then each
    // statistic's name on the left and its value on the right - and, when not every block fits, "… k more". With room
    // for no whole block, the first series as far as it fits (partial of its statistics), then "…".
    private static void DrawStatisticsBlocks(
        SKCanvas canvas,
        GraphStatisticsPanel panel,
        GraphTheme theme,
        SKPaint fill,
        SKFont font,
        float left,
        float right,
        float top,
        float rowHeight,
        int shownBlocks,
        int partial,
        int more)
    {
        var fontMetrics = font.Metrics;
        var labelLeft = left + LegendSwatchSize + LegendEntrySpacing;
        var names = StatisticsNames(panel);
        for (var index = 0; index < shownBlocks + (partial > 0 ? 1 : 0); index++)
        {
            var row = panel.Rows[index];
            var statistics = index < shownBlocks ? panel.Items.Count : partial;
            var centerY = top + (rowHeight / 2f);
            if (row.SeriesIndex is { } series)
            {
                fill.Color = theme.SeriesColor(series);
                var half = LegendSwatchSize / 2f;
                canvas.DrawRect(new SKRect(left, centerY - half, left + LegendSwatchSize, centerY + half), fill);
            }

            fill.Color = theme.Text;
            GraphTextFallback.DrawText(
                canvas,
                Ellipsize(row.Label, font, right - labelLeft),
                labelLeft,
                CenteredBaseline(centerY, fontMetrics),
                SKTextAlign.Left,
                font,
                fill);
            top += rowHeight + LegendEntrySpacing;

            for (var item = 0; item < statistics; item++)
            {
                var baseline = CenteredBaseline(top + (rowHeight / 2f), fontMetrics);
                fill.Color = theme.SecondaryText;
                GraphTextFallback.DrawText(canvas, names[item], labelLeft, baseline, SKTextAlign.Left, font, fill);
                fill.Color = theme.Text;
                GraphTextFallback.DrawText(
                    canvas, StatisticText(row, panel.Items[item]), right, baseline, SKTextAlign.Right, font, fill);
                top += rowHeight + LegendEntrySpacing;
            }

            if (statistics < panel.Items.Count)
            {
                // The statistics of the series that did not fit.
                fill.Color = theme.SecondaryText;
                var baseline = CenteredBaseline(top + (rowHeight / 2f), fontMetrics);
                GraphTextFallback.DrawText(canvas, "…", labelLeft, baseline, SKTextAlign.Left, font, fill);
                top += rowHeight + LegendEntrySpacing;
            }
        }

        if (more > 0)
        {
            var baseline = CenteredBaseline(top + (rowHeight / 2f), fontMetrics);
            DrawMore(canvas, theme, fill, font, left, baseline, more);
        }
    }

    private static void DrawStatisticsTable(
        SKCanvas canvas,
        GraphStatisticsPanel panel,
        GraphTheme theme,
        SKPaint fill,
        SKFont font,
        float left,
        float right,
        float top,
        float rowHeight,
        int shownRows,
        bool overflow)
    {
        var fontMetrics = font.Metrics;
        var (_, numberWidths) = StatisticsColumns(panel, font);

        // The numbers keep their widths, right-aligned from the right edge; the group labels take what is left and
        // end in an ellipsis when that is not enough.
        var numberRights = new float[numberWidths.Length];
        var edge = right;
        for (var column = numberWidths.Length - 1; column >= 0; column--)
        {
            numberRights[column] = edge;
            edge -= numberWidths[column] + StatisticsColumnGap;
        }

        var labelLeft = left + LegendSwatchSize + LegendEntrySpacing;
        // edge is now the left of the first number column, less its gap: the labels may reach it.
        var labelWidth = Math.Max(edge - labelLeft, StatisticsMinimumLabelWidth);

        // Header: the grouping column's name over the labels, the statistic names over the numbers.
        var baseline = CenteredBaseline(top + (rowHeight / 2f), fontMetrics);
        fill.Color = theme.Text;
        GraphTextFallback.DrawText(
            canvas,
            Ellipsize(panel.GroupHeader ?? string.Empty, font, labelWidth),
            labelLeft,
            baseline,
            SKTextAlign.Left,
            font,
            fill);
        var names = StatisticsNames(panel);
        for (var column = 0; column < names.Length; column++)
        {
            GraphTextFallback.DrawText(
                canvas,
                names[column],
                numberRights[column],
                baseline,
                SKTextAlign.Right,
                font,
                fill);
        }

        top += rowHeight + LegendEntrySpacing;

        for (var index = 0; index < shownRows; index++)
        {
            var centerY = top + (rowHeight / 2f);
            baseline = CenteredBaseline(centerY, fontMetrics);

            if (overflow && index == shownRows - 1)
            {
                DrawMore(canvas, theme, fill, font, left, baseline, panel.Rows.Count - index);
                break;
            }

            var row = panel.Rows[index];
            if (row.SeriesIndex is { } series)
            {
                fill.Color = theme.SeriesColor(series);
                canvas.DrawRect(
                    new SKRect(left, centerY - (LegendSwatchSize / 2f), left + LegendSwatchSize, centerY + (LegendSwatchSize / 2f)),
                    fill);
            }

            fill.Color = theme.SecondaryText;
            GraphTextFallback.DrawText(
                canvas,
                Ellipsize(row.Label, font, labelWidth),
                labelLeft,
                baseline,
                SKTextAlign.Left,
                font,
                fill);

            fill.Color = theme.Text;
            for (var column = 0; column < panel.Items.Count; column++)
            {
                GraphTextFallback.DrawText(
                    canvas,
                    StatisticText(row, panel.Items[column]),
                    numberRights[column],
                    baseline,
                    SKTextAlign.Right,
                    font,
                    fill);
            }

            top += rowHeight + LegendEntrySpacing;
        }
    }

    // The line that stands in for the rows that do not fit.
    private static void DrawMore(SKCanvas canvas, GraphTheme theme, SKPaint fill, SKFont font, float left, float baseline, int hidden)
    {
        fill.Color = theme.SecondaryText;
        GraphTextFallback.DrawText(canvas, $"… {hidden} more", left, baseline, SKTextAlign.Left, font, fill);
    }

    // The text as it fits in width: whole, or cut short with an ellipsis.
    internal static string Ellipsize(string text, SKFont font, float width)
    {
        if (GraphTextFallback.MeasureText(font, text) <= width)
        {
            return text;
        }

        const string Ellipsis = "…";
        for (var length = text.Length - 1; length > 0; length--)
        {
            // Never between the two halves of a surrogate pair: a character outside the basic plane stays whole.
            if (char.IsLowSurrogate(text[length]) && char.IsHighSurrogate(text[length - 1]))
            {
                continue;
            }

            var candidate = text[..length] + Ellipsis;
            if (GraphTextFallback.MeasureText(font, candidate) <= width)
            {
                return candidate;
            }
        }

        return GraphTextFallback.MeasureText(font, Ellipsis) <= width ? Ellipsis : string.Empty;
    }

    private static bool IsVisible(float position, float lower, float upper) =>
        float.IsFinite(position) && position >= lower - EdgeTolerance && position <= upper + EdgeTolerance;

    private static SKFont Font(float size) => new() { Size = size, Edging = SKFontEdging.Antialias, Subpixel = true };

    private static float LineHeight(SKFont font)
    {
        var metrics = font.Metrics;
        return metrics.Descent - metrics.Ascent;
    }

    // The baseline that centres one line of text on centerY (Ascent is negative, Descent positive).
    private static float CenteredBaseline(float centerY, SKFontMetrics metrics) => centerY - ((metrics.Ascent + metrics.Descent) / 2f);

    // The most of the content's width a category label is drawn across (Task #063.2), so that half of one - what the
    // last label reaches past the plot - never takes more than a quarter of it. A label longer than that is ellipsized:
    // the plot, its axes, its boxes and its title are kept, and only the label gives way.
    public const float MaximumCategoryLabelShare = 0.5f;

    private static float CategoryLabelWidth(GraphRenderModel model, SKRect bounds) =>
        model.XAxis.Scale == GraphAxisScale.Categorical
            ? Math.Max(bounds.Width - (2 * new GraphLayoutMetrics().OuterPadding), 0f) * MaximumCategoryLabelShare
            : float.PositiveInfinity;

    // A tick label as it is drawn: a category label within the width it may take, every other label as it is.
    private static string CategoryLabel(string label, SKFont font, GraphLayoutMetrics metrics) =>
        float.IsPositiveInfinity(metrics.CategoryLabelWidth) ? label : Ellipsize(label, font, metrics.CategoryLabelWidth);

    private static float WidestLabel(GraphAxisModel axis, SKFont font)
    {
        var widest = 0f;
        foreach (var tick in axis.Ticks)
        {
            widest = Math.Max(widest, GraphTextFallback.MeasureText(font, tick.Label));
        }

        return widest;
    }

    // The height of the legend box with all of its rows: padding, the title if any, and one row per entry.
    private static float LegendBoxHeight(GraphLegendModel legend, SKFont font)
    {
        var rowHeight = Math.Max(LineHeight(font), LegendSwatchSize);
        var rows = legend.Entries.Count + (string.IsNullOrWhiteSpace(legend.Title) ? 0 : 1);
        return (LegendPadding * 2f) + (rows * rowHeight) + (Math.Max(rows - 1, 0) * LegendEntrySpacing);
    }

    // The width of the legend box as a legend has always been given it: its widest line, padded, and never more than
    // LegendMaximumWidth. titleWidth is 0 for a legend without a title.
    private static float LegendWidth(GraphLegendModel? legend, float titleWidth, IReadOnlyList<float> labelWidths)
    {
        if (legend is null)
        {
            return 0f;
        }

        var widest = titleWidth;
        foreach (var label in labelWidths)
        {
            widest = Math.Max(widest, LegendSwatchSize + LegendEntrySpacing + label);
        }

        return Math.Min(widest + (LegendPadding * 2f), LegendMaximumWidth);
    }

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
}
