using SkiaSharp;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// How far an axis can be navigated (Task #055): the narrowest and widest range it may be viewed over and the values it
// may never leave. Lowest and Highest are infinite where the axis has no such end.
public readonly record struct GraphViewLimits(double MinimumSpan, double MaximumSpan, double Lowest, double Highest);

// The arithmetic of zooming and panning an axis (Task #055), on the axis's own values - normal scores on a probability
// axis, which is linear on screen - and in the canvas's coordinates. No pointer, no window, no graph: what a graph window
// does with a wheel step or a drag is worked out here, so it can be checked exactly.
//
//     zoom by s around the value a under the cursor:   minimum' = a - (a - minimum) * s,  maximum' = minimum' + span * s
//     pan by d pixels across a plot w pixels wide:      minimum' = minimum0 - d * span0 / w   (and span' = span0)
//
// so a stays under the cursor and a drag moves the graph with the pointer. A pan is worked out from where the drag began,
// never added up step by step, so it cannot drift. Results are clamped to the axis's limits, never refused: a range
// narrower or wider than the limits is brought to them about the same anchor, and one past an end of the axis is moved
// back inside, keeping its width.
public static class GraphViewNavigator
{
    // One wheel step zooms by this much: in for a step forward, out for a step back.
    public const double ZoomFactor = 1.2;

    // The narrowest and widest range an axis may be viewed over, as fractions of the range it shows before any
    // navigation (its configured range, Auto or chosen).
    public const double MinimumSpanRatio = 1e-6;

    public const double MaximumSpanRatio = 1e3;

    // How far the pointer must move, in either direction, before a press in the plot becomes a pan.
    public const double DragThreshold = 3;

    // The factor the span is multiplied by for a wheel delta: below 1 (in) for a step forward, above 1 (out) for a step
    // back; a fraction of a step (a touchpad) zooms by that fraction of a step.
    public static double ZoomScale(double wheelDelta) => Math.Pow(ZoomFactor, -wheelDelta);

    // The limits of an axis of this kind: its span between MinimumSpanRatio and MaximumSpanRatio of reference - the range
    // it shows before any navigation - and its values inside what the kind allows (at or above 0 on a histogram's axis,
    // 0 to 100 on a percent axis, the probability axis's percentages). The reference itself is always inside them, even
    // where a graph's automatic range reaches past an end (a probability axis padded beyond its farthest percentages).
    public static GraphViewLimits Limits(GraphAxisKind kind, GraphAxisRange reference)
    {
        var (lowest, highest) = kind switch
        {
            GraphAxisKind.NonNegative => (0d, double.PositiveInfinity),
            GraphAxisKind.Percent => (0d, 100d),
            GraphAxisKind.ProbabilityPercent => (
                ProbabilityAxis.Score(GraphAxisRangeRules.MinimumProbabilityPercent),
                ProbabilityAxis.Score(GraphAxisRangeRules.MaximumProbabilityPercent)),
            _ => (double.NegativeInfinity, double.PositiveInfinity)
        };

        return new GraphViewLimits(
            reference.Span * MinimumSpanRatio,
            reference.Span * MaximumSpanRatio,
            Math.Min(lowest, reference.Minimum),
            Math.Max(highest, reference.Maximum));
    }

    // The range zoomed by scale around anchor, a value of the axis, within the limits. A range that is already outside
    // them (a configured range narrower or wider than the limits) is never forced to them by zooming the other way.
    public static GraphAxisRange Zoom(GraphAxisRange range, double anchor, double scale, GraphViewLimits limits)
    {
        var lower = Math.Min(limits.MinimumSpan, range.Span);
        var upper = Math.Max(Math.Min(limits.MaximumSpan, limits.Highest - limits.Lowest), range.Span);
        var span = Math.Clamp(range.Span * scale, lower, upper);
        var minimum = anchor - ((anchor - range.Minimum) / range.Span * span);
        return Inside(minimum, span, limits);
    }

    // The range a drag began over, moved by shift (in the axis's values) and kept inside the limits, its width unchanged.
    public static GraphAxisRange Pan(GraphAxisRange start, double shift, GraphViewLimits limits) =>
        Inside(start.Minimum + shift, start.Span, limits);

    // The value of the axis at a canvas position across the plot area: X from its left edge, Y from its bottom edge.
    public static double ValueAt(GraphAxisField axis, GraphAxisRange range, SKRect plotArea, SKPoint point) =>
        axis == GraphAxisField.X
            ? range.Minimum + ((point.X - plotArea.Left) / (double)plotArea.Width * range.Span)
            : range.Minimum + ((plotArea.Bottom - point.Y) / (double)plotArea.Height * range.Span);

    // How far the range a drag began over moves when the pointer has moved from start to point: the graph follows the
    // pointer, so dragging right shows lower values and dragging down shows higher ones.
    public static double Shift(GraphAxisField axis, GraphAxisRange start, SKRect plotArea, SKPoint from, SKPoint to) =>
        axis == GraphAxisField.X
            ? -(to.X - from.X) / (double)plotArea.Width * start.Span
            : (to.Y - from.Y) / (double)plotArea.Height * start.Span;

    // Whether the pointer has moved far enough from where it was pressed for the press to be a pan.
    public static bool IsDrag(SKPoint from, SKPoint to) =>
        Math.Abs(to.X - from.X) >= DragThreshold || Math.Abs(to.Y - from.Y) >= DragThreshold;

    // A range of this width starting at minimum, moved back inside the limits where it reaches past an end.
    private static GraphAxisRange Inside(double minimum, double span, GraphViewLimits limits)
    {
        if (minimum < limits.Lowest)
        {
            minimum = limits.Lowest;
        }

        if (minimum + span > limits.Highest)
        {
            minimum = limits.Highest - span;
        }

        return new GraphAxisRange(minimum, minimum + span);
    }
}
