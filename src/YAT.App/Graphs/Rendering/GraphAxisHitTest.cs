using SkiaSharp;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Where one axis of a graph can be picked (Task #052), in the canvas's coordinates: its own area of the layout - ticks,
// tick labels, title - (for X reaching on under the last tick label where it overhangs the plot) and the plot edge its
// line is drawn on. Made by SkiaGraphRenderer.AxisGeometry from the layout the graph is drawn with.
public sealed record GraphAxisGeometry(GraphAxisField Axis, SKRect Area, SKRect Line);

// The axes of a graph as they can be picked, and the parts of the graph that are never an axis even where they touch
// one (the title, the legend, the statistics panel, the reference line labels).
public sealed record GraphAxesGeometry(IReadOnlyList<GraphAxisGeometry> Axes, IReadOnlyList<SKRect> Excluded)
{
    public static GraphAxesGeometry None { get; } = new([], []);
}

// Which axis of a graph a point is on (Task #052). An axis is picked in its area, or within a few pixels of its line on
// either side; the plot's body, the legend, the statistics panel, the reference line labels and the title never pick
// one, and neither does the corner where both lines meet, which belongs to neither.
public static class GraphAxisHitTest
{
    // How far from its line, on either side, an axis can still be picked.
    public const float LineTolerance = 3f;

    public static GraphAxisField? Find(GraphAxesGeometry geometry, SKPoint point)
    {
        ArgumentNullException.ThrowIfNull(geometry);

        if (geometry.Excluded.Any(area => area.Contains(point)))
        {
            return null;
        }

        var hits = geometry.Axes.Where(axis => axis.Area.Contains(point) || axis.Line.Contains(point)).Select(axis => axis.Axis).Distinct().ToList();
        return hits is [var only] ? only : null;
    }

    // The band around an axis line drawn along a plot edge: the line's own half width and the tolerance on each side.
    public static SKRect LineBand(SKPoint from, SKPoint to, float thickness)
    {
        var reach = (Math.Max(thickness, 0f) / 2f) + LineTolerance;
        return new SKRect(
            Math.Min(from.X, to.X) - reach,
            Math.Min(from.Y, to.Y) - reach,
            Math.Max(from.X, to.X) + reach,
            Math.Max(from.Y, to.Y) + reach);
    }
}
