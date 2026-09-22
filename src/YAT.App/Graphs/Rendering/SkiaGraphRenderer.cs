using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws a graph render model with SkiaSharp, and does nothing else: no statistics, no worksheet data, no knowledge of
// where the model came from. Every graph type shares this frame (background, plot area, grid, axes, ticks, labels,
// titles, legend); what goes inside the plot area is drawn by the graph type's own IGraphPlotRenderer.
//
// The renderer is stateless, so one instance can draw any model into any canvas.
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

        DrawGrid(canvas, model, layout, transform, theme, stroke);
        DrawPlot(canvas, layout, transform, theme, plot);
        DrawReferenceLines(canvas, model, layout, transform, theme);
        DrawAxisLines(canvas, layout, theme, stroke);
        DrawTicks(canvas, model, layout, metrics, transform, theme, stroke, fill, tickFont);
        DrawAxisTitles(canvas, model, layout, theme, fill, axisTitleFont);
        DrawTitle(canvas, model, layout, theme, fill, titleFont);
        DrawReferenceLabels(canvas, model, layout, transform, theme, fill, tickFont);
        DrawLegend(canvas, model, layout, theme, fill, stroke, tickFont);
        DrawStatisticsPanel(canvas, model, layout, theme, fill, stroke, tickFont);
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
        metrics = Measure(model, titleFont, axisTitleFont, tickFont);
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
    private static GraphLayoutMetrics Measure(GraphRenderModel model, SKFont titleFont, SKFont axisTitleFont, SKFont tickFont)
    {
        var hasAxisTitle = !string.IsNullOrWhiteSpace(model.XAxis.Title) || !string.IsNullOrWhiteSpace(model.YAxis.Title);

        return new GraphLayoutMetrics
        {
            TitleHeight = string.IsNullOrWhiteSpace(model.Title) ? 0f : LineHeight(titleFont),
            AxisTitleHeight = hasAxisTitle ? LineHeight(axisTitleFont) : 0f,
            TickLabelHeight = LineHeight(tickFont),
            YTickLabelWidth = WidestLabel(model.YAxis, tickFont),
            XTickLabelOverflow = WidestLabel(model.XAxis, tickFont) / 2f,
            LegendWidth = LegendWidth(model.Legend, tickFont),
            LegendHeight = model.Legend is null ? 0f : LegendBoxHeight(model.Legend, tickFont),
            StatisticsPanelWidth = model.StatisticsPanel is null ? 0f : StatisticsPanelWidth(model.StatisticsPanel, tickFont)
        };
    }

    private static void DrawGrid(
        SKCanvas canvas,
        GraphRenderModel model,
        GraphLayout layout,
        GraphCoordinateTransform transform,
        GraphTheme theme,
        SKPaint stroke)
    {
        stroke.Color = theme.Grid;
        stroke.StrokeWidth = theme.GridThickness;

        var restore = canvas.Save();
        canvas.ClipRect(layout.PlotArea);

        foreach (var tick in model.XAxis.Ticks)
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

        foreach (var tick in model.XAxis.Ticks)
        {
            var x = (float)transform.ToScreenX(tick.Value);
            if (!IsVisible(x, layout.PlotArea.Left, layout.PlotArea.Right))
            {
                continue;
            }

            canvas.DrawLine(x, layout.PlotArea.Bottom, x, layout.PlotArea.Bottom + metrics.TickLength, stroke);
            canvas.DrawText(tick.Label, x, labelBaseline, SKTextAlign.Center, tickFont, fill);
        }

        foreach (var tick in model.YAxis.Ticks)
        {
            var y = (float)transform.ToScreenY(tick.Value);
            if (!IsVisible(y, layout.PlotArea.Top, layout.PlotArea.Bottom))
            {
                continue;
            }

            canvas.DrawLine(layout.PlotArea.Left - metrics.TickLength, y, layout.PlotArea.Left, y, stroke);
            canvas.DrawText(tick.Label, labelRight, CenteredBaseline(y, fontMetrics), SKTextAlign.Right, tickFont, fill);
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
            var centerY = layout.XAxisArea.Bottom - (LineHeight(axisTitleFont) / 2f);
            canvas.DrawText(model.XAxis.Title, layout.PlotArea.MidX, CenteredBaseline(centerY, fontMetrics), SKTextAlign.Center, axisTitleFont, fill);
        }

        if (!string.IsNullOrWhiteSpace(model.YAxis.Title))
        {
            // Turned a quarter turn anticlockwise: the glyphs then grow to the left of the baseline, so the baseline is
            // offset by half the text band to centre the title on the left edge of the Y axis area.
            var centerX = layout.YAxisArea.Left + (LineHeight(axisTitleFont) / 2f);
            var restore = canvas.Save();
            canvas.Translate(centerX - ((fontMetrics.Ascent + fontMetrics.Descent) / 2f), layout.PlotArea.MidY);
            canvas.RotateDegrees(-90);
            canvas.DrawText(model.YAxis.Title, 0, 0, SKTextAlign.Center, axisTitleFont, fill);
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
        canvas.DrawText(
            model.Title,
            layout.PlotArea.MidX,
            CenteredBaseline(layout.TitleArea.MidY, titleFont.Metrics),
            SKTextAlign.Center,
            titleFont,
            fill);
    }

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
            canvas.DrawText(model.Legend.Title, left, top - fontMetrics.Ascent, SKTextAlign.Left, font, fill);
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
            canvas.DrawText(
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
            var width = font.MeasureText(candidate.Line.Label);
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
                canvas.DrawText(label.Line.Label, label.Left, bottom - fontMetrics.Descent, SKTextAlign.Left, font, fill);
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
                canvas.DrawText(line.Label, plotArea.Right - ReferenceLabelGap, y - ReferenceLabelBottomGap - fontMetrics.Descent, SKTextAlign.Right, font, fill);
            }
        }

        canvas.RestoreToCount(plotRestore);
    }

    // ---- Statistics panel ----
    //
    // The statistics beside the plot, in a box styled like the legend. Ungrouped, it is a title and three rows - Mean,
    // StDev, N - with the value right-aligned. Grouped, it is a table: a header with the grouping column's name and the
    // statistic names, then one row per group with the group's colour swatch, its label on the left and its numbers on
    // the right. Every text was decided when the model was built; this only places it.
    //
    // Nothing scrolls and no font shrinks: a label that does not fit the panel's width ends in an ellipsis, and when
    // not every group fits the panel's height, the last row that does says how many more there are.

    private const float StatisticsMaximumWidth = 280f;
    private const float StatisticsColumnGap = 10f;
    private const float StatisticsMinimumLabelWidth = 24f;

    private static readonly string[] StatisticsNames = ["Mean", "StDev", "N"];

    // The width the panel's content asks for, capped at its maximum width.
    private static float StatisticsPanelWidth(GraphStatisticsPanel panel, SKFont font)
    {
        var width = font.MeasureText(panel.Title);
        if (panel.IsGrouped)
        {
            var (labelWidth, numberWidths) = StatisticsColumns(panel, font);
            width = Math.Max(width, LegendSwatchSize + LegendEntrySpacing + labelWidth + numberWidths.Sum(column => StatisticsColumnGap + column));
        }
        else
        {
            var row = panel.Rows[0];
            var labels = StatisticsNames.Max(name => font.MeasureText(name));
            var values = UngroupedValues(row).Max(value => font.MeasureText(value));
            width = Math.Max(width, labels + StatisticsColumnGap + values);
        }

        return Math.Min(width + (LegendPadding * 2f), StatisticsMaximumWidth);
    }

    // The label column and the three number columns of a grouped panel, each as wide as its widest text.
    private static (float Label, float[] Numbers) StatisticsColumns(GraphStatisticsPanel panel, SKFont font)
    {
        var label = font.MeasureText(panel.GroupHeader ?? string.Empty);
        var numbers = StatisticsNames.Select(name => font.MeasureText(name)).ToArray();
        foreach (var row in panel.Rows)
        {
            label = Math.Max(label, font.MeasureText(row.Label));
            numbers[0] = Math.Max(numbers[0], font.MeasureText(row.MeanText));
            numbers[1] = Math.Max(numbers[1], font.MeasureText(row.StandardDeviationText));
            numbers[2] = Math.Max(numbers[2], font.MeasureText(row.CountText));
        }

        return (label, numbers);
    }

    private static string[] UngroupedValues(GraphStatisticsRow row) => [row.MeanText, row.StandardDeviationText, row.CountText];

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

        var headerLines = panel.IsGrouped ? 2 : 1;
        var dataLines = panel.IsGrouped ? panel.Rows.Count : StatisticsNames.Length;
        var (shownData, _) = FitStatisticsLines(panel, area.Height, rowHeight);
        var overflow = shownData < dataLines;
        var lines = headerLines + shownData;

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
        canvas.DrawText(Ellipsize(panel.Title, font, right - left), left, top - fontMetrics.Ascent, SKTextAlign.Left, font, fill);
        top += rowHeight + LegendEntrySpacing;

        if (panel.IsGrouped)
        {
            DrawStatisticsTable(canvas, panel, theme, fill, font, left, right, top, rowHeight, shownData, overflow);
        }
        else
        {
            var values = UngroupedValues(panel.Rows[0]);
            for (var line = 0; line < shownData; line++)
            {
                var baseline = CenteredBaseline(top + (rowHeight / 2f), fontMetrics);
                if (overflow && line == shownData - 1)
                {
                    DrawMore(canvas, theme, fill, font, left, baseline, dataLines - line);
                    break;
                }

                fill.Color = theme.SecondaryText;
                canvas.DrawText(StatisticsNames[line], left, baseline, SKTextAlign.Left, font, fill);
                fill.Color = theme.Text;
                canvas.DrawText(values[line], right, baseline, SKTextAlign.Right, font, fill);
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
        var dataLines = panel.IsGrouped ? panel.Rows.Count : StatisticsNames.Length;

        // As many data lines as the height allows; if some do not fit, the last one that does becomes "… k more".
        var available = height - (LegendPadding * 2f) + LegendEntrySpacing;
        var fitting = Math.Max(0, (int)Math.Floor(available / (rowHeight + LegendEntrySpacing)) - headerLines);
        var lines = Math.Min(dataLines, fitting);
        return (lines, lines > 0 && lines < dataLines ? dataLines - lines + 1 : 0);
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
        canvas.DrawText(Ellipsize(panel.GroupHeader ?? string.Empty, font, labelWidth), labelLeft, baseline, SKTextAlign.Left, font, fill);
        for (var column = 0; column < StatisticsNames.Length; column++)
        {
            canvas.DrawText(StatisticsNames[column], numberRights[column], baseline, SKTextAlign.Right, font, fill);
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
            canvas.DrawText(Ellipsize(row.Label, font, labelWidth), labelLeft, baseline, SKTextAlign.Left, font, fill);

            fill.Color = theme.Text;
            canvas.DrawText(row.MeanText, numberRights[0], baseline, SKTextAlign.Right, font, fill);
            canvas.DrawText(row.StandardDeviationText, numberRights[1], baseline, SKTextAlign.Right, font, fill);
            canvas.DrawText(row.CountText, numberRights[2], baseline, SKTextAlign.Right, font, fill);

            top += rowHeight + LegendEntrySpacing;
        }
    }

    // The line that stands in for the rows that do not fit.
    private static void DrawMore(SKCanvas canvas, GraphTheme theme, SKPaint fill, SKFont font, float left, float baseline, int hidden)
    {
        fill.Color = theme.SecondaryText;
        canvas.DrawText($"… {hidden} more", left, baseline, SKTextAlign.Left, font, fill);
    }

    // The text as it fits in width: whole, or cut short with an ellipsis.
    internal static string Ellipsize(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width)
        {
            return text;
        }

        const string Ellipsis = "…";
        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + Ellipsis;
            if (font.MeasureText(candidate) <= width)
            {
                return candidate;
            }
        }

        return font.MeasureText(Ellipsis) <= width ? Ellipsis : string.Empty;
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

    private static float WidestLabel(GraphAxisModel axis, SKFont font)
    {
        var widest = 0f;
        foreach (var tick in axis.Ticks)
        {
            widest = Math.Max(widest, font.MeasureText(tick.Label));
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

    private static float LegendWidth(GraphLegendModel? legend, SKFont font)
    {
        if (legend is null)
        {
            return 0f;
        }

        var widest = string.IsNullOrWhiteSpace(legend.Title) ? 0f : font.MeasureText(legend.Title);
        foreach (var entry in legend.Entries)
        {
            widest = Math.Max(widest, LegendSwatchSize + LegendEntrySpacing + font.MeasureText(entry.Label));
        }

        return Math.Min(widest + (LegendPadding * 2f), LegendMaximumWidth);
    }

    private static bool IsFinite(SKRect rect) =>
        float.IsFinite(rect.Left) && float.IsFinite(rect.Top) && float.IsFinite(rect.Right) && float.IsFinite(rect.Bottom);
}
