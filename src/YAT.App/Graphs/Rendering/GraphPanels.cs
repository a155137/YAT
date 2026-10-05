using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// One panel of a graph drawn in panels (Task #058): its generated title ("Site = 1") and what its graph type draws
// inside it - the panel's own observations, built against the whole graph's series order and, for a histogram, its bins.
// Everything else a panel shows - its axes, ranges, ticks, reference lines - is the whole graph's, shared by every
// panel. A panel's statistics are not here: they are the rows of the whole graph's statistics panel (Task #062).
public sealed record GraphPanel
{
    public GraphPanel(string title, IGraphPlotRenderer? plot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title;
        Plot = plot;
    }

    public string Title { get; }

    // Null for a panel with nothing to draw.
    public IGraphPlotRenderer? Plot { get; }
}

// Where the panels of a graph go (Task #058): a near-square grid - as many columns as the square root of the panel count,
// rounded up, and as many rows as that takes - filling the plot area the whole graph would have, row by row. The graph's
// title, axis titles and legend keep their places around it, laid out once for the whole graph; every panel is a graph
// frame of its own inside its cell, under its own title, over the whole graph's axes, with its tick labels repeated.
public static class GraphPanelLayout
{
    // The most panels a graph is drawn in.
    public const int MaximumPanels = 9;

    // Room between neighbouring panels.
    public const float Gap = 12f;

    // The columns and rows of n panels: 1 -> 1x1, 2 -> 2x1, 3-4 -> 2x2, 5-6 -> 3x2, 7-9 -> 3x3.
    public static (int Columns, int Rows) Grid(int count)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var columns = (int)Math.Ceiling(Math.Sqrt(count));
        return (columns, (int)Math.Ceiling(count / (double)columns));
    }

    // The frame the whole graph is laid out and drawn around the panels with: its title, its axis titles, its legend
    // and its statistics - the panels' (Task #062) - over axes without ticks and without reference lines, which are
    // the panels'.
    public static GraphRenderModel OuterFrame(GraphRenderModel frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame
            .WithXAxis(Untick(frame.XAxis))
            .WithYAxis(Untick(frame.YAxis))
            .WithReferenceLines([]);
    }

    // The frame of one panel: the whole graph's axes, ticks and reference lines under the panel's title, without axis
    // titles, a legend or statistics, which the whole graph shows once.
    public static GraphRenderModel PanelFrame(GraphRenderModel frame, string title)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return frame
            .WithTitle(title)
            .WithXAxis(frame.XAxis.WithTitle(null))
            .WithYAxis(frame.YAxis.WithTitle(null))
            .WithLegend(null, frame.LegendPosition)
            .WithStatisticsPanel(null);
    }

    // The frame of one panel as it is drawn in its cell: PanelFrame, with only as many of the shared ticks labelled as the
    // cell has room to show apart. A small cell cannot show every tick a whole-graph axis has - a probability axis's
    // seventeen percentages least of all - so every second, third, ... tick is kept until neighbouring labels stand clear
    // of each other: on a probability axis outwards from its middle (50, 20 and 80, ...), on any other from its first
    // tick. The ticks kept are the whole graph's own, at the same places in every panel; nothing is thinned that fits.
    public static GraphRenderModel PanelFrame(GraphRenderModel frame, string title, SKRect cell, GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var panel = PanelFrame(frame, title);
        var layout = SkiaGraphRenderer.Layout(panel, cell, theme);
        if (!layout.HasPlotArea)
        {
            return panel;
        }

        // Up the Y axis labels stack, a line apart; along the X axis they sit side by side, half a letter apart.
        var yRoom = theme.TickLabelFontSize * 1.2;
        var xRoom = panel.XAxis.Ticks.Count == 0
            ? 0
            : panel.XAxis.Ticks.Max(tick => SkiaGraphRenderer.TickLabelWidth(tick.Label, theme)) + (theme.TickLabelFontSize / 2);
        var x = Readable(panel.XAxis, layout.PlotArea.Width, xRoom);
        var y = Readable(panel.YAxis, layout.PlotArea.Height, yRoom);
        return ReferenceEquals(x, panel.XAxis) && ReferenceEquals(y, panel.YAxis) ? panel : panel.WithXAxis(x).WithYAxis(y);
    }

    // The axis with every step-th tick of its own, the smallest step whose neighbours lie room pixels apart or more.
    private static GraphAxisModel Readable(GraphAxisModel axis, float pixels, double room)
    {
        var ticks = axis.Ticks;
        if (ticks.Count < 2 || !axis.Range.IsValid || pixels <= 0)
        {
            return axis;
        }

        var anchor = axis.Scale == GraphAxisScale.Probability ? Middle(ticks) : 0;
        for (var step = 1; step < ticks.Count; step++)
        {
            var kept = ticks.Where((_, index) => (index - anchor) % step == 0).ToArray();
            if (kept.Length < 2 || Closest(kept, axis.Range, pixels) >= room)
            {
                return step == 1 ? axis : new GraphAxisModel(axis.Range, kept, axis.Title) { Scale = axis.Scale };
            }
        }

        return new GraphAxisModel(axis.Range, [ticks[anchor]], axis.Title) { Scale = axis.Scale };
    }

    // The tick of 50 percent, at score 0, or the one nearest it.
    private static int Middle(IReadOnlyList<GraphAxisTick> ticks)
    {
        var middle = 0;
        for (var index = 1; index < ticks.Count; index++)
        {
            if (Math.Abs(ticks[index].Value) < Math.Abs(ticks[middle].Value))
            {
                middle = index;
            }
        }

        return middle;
    }

    // The least distance on screen between neighbouring ticks of an axis pixels long.
    private static double Closest(IReadOnlyList<GraphAxisTick> ticks, GraphAxisRange range, float pixels)
    {
        var closest = double.PositiveInfinity;
        for (var index = 1; index < ticks.Count; index++)
        {
            closest = Math.Min(closest, Math.Abs(ticks[index].Value - ticks[index - 1].Value) / range.Span * pixels);
        }

        return closest;
    }

    // The plot area of the whole graph, which the panels share out; null when the bounds are too small for one.
    public static SKRect? Area(GraphRenderModel frame, SKRect bounds, GraphTheme theme)
    {
        var layout = SkiaGraphRenderer.Layout(OuterFrame(frame), bounds, theme);
        return layout.HasPlotArea ? layout.PlotArea : null;
    }

    // The cell of every panel, in panel order: the area shared out into the grid, a gap between neighbours.
    public static IReadOnlyList<SKRect> Cells(SKRect area, int count)
    {
        var (columns, rows) = Grid(count);
        var width = (area.Width - (Gap * (columns - 1))) / columns;
        var height = (area.Height - (Gap * (rows - 1))) / rows;
        var cells = new SKRect[count];
        for (var index = 0; index < count; index++)
        {
            var left = area.Left + ((index % columns) * (width + Gap));
            var top = area.Top + ((index / columns) * (height + Gap));
            cells[index] = new SKRect(left, top, left + width, top + height);
        }

        return cells;
    }

    // The cells of a graph's panels at these bounds; empty when there is no room for them.
    public static IReadOnlyList<SKRect> Cells(GraphRenderModel frame, int count, SKRect bounds, GraphTheme theme) =>
        Area(frame, bounds, theme) is { } area ? Cells(area, count) : [];

    // The panel a point is in, or null between and around the panels.
    public static int? PanelAt(IReadOnlyList<SKRect> cells, SKPoint point)
    {
        ArgumentNullException.ThrowIfNull(cells);
        for (var index = 0; index < cells.Count; index++)
        {
            if (cells[index].Contains(point))
            {
                return index;
            }
        }

        return null;
    }

    private static GraphAxisModel Untick(GraphAxisModel axis) =>
        new GraphAxisModel(axis.Range, [], axis.Title) { Scale = axis.Scale };
}

// Draws a graph - in panels or not - the one way the screen, Copy Image and every export draw it. Without panels it is
// the graph's frame and plot exactly as SkiaGraphRenderer always drew them.
public static class GraphDrawing
{
    public static void Render(
        SkiaGraphRenderer renderer,
        SKCanvas canvas,
        GraphRenderModel frame,
        IGraphPlotRenderer? plot,
        IReadOnlyList<GraphPanel>? panels,
        SKRect bounds,
        GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(theme);

        if (panels is not { Count: > 0 })
        {
            renderer.Render(canvas, frame, bounds, theme, plot);
            return;
        }

        // The whole graph first - background, title, axis titles, legend - then its plot area given back to the page and
        // shared out among the panels, each drawn as a graph of its own in its cell.
        renderer.Render(canvas, GraphPanelLayout.OuterFrame(frame), bounds, theme, plot: null);
        if (GraphPanelLayout.Area(frame, bounds, theme) is not { } area)
        {
            return;
        }

        using (var page = new SKPaint { Color = theme.Background, Style = SKPaintStyle.Fill })
        {
            var reach = theme.AxisThickness + 1f;
            canvas.DrawRect(new SKRect(area.Left - reach, area.Top - reach, area.Right + reach, area.Bottom + reach), page);
        }

        var cells = GraphPanelLayout.Cells(area, panels.Count);
        for (var index = 0; index < panels.Count; index++)
        {
            renderer.Render(canvas, GraphPanelLayout.PanelFrame(frame, panels[index].Title, cells[index], theme), cells[index], theme, panels[index].Plot);
        }
    }
}
