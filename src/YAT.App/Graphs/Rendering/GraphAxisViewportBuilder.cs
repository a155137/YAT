using YAT.Application.Graphs;
using YAT.app.Analyses;

namespace YAT.app.Graphs.Rendering;

// Puts the axis ranges the user chose on a presented frame (Task #043): the last step before the labels. Each axis the
// graph type lets the user choose keeps its automatic end wherever the user left one blank and takes the user's value
// wherever one was typed; the axis then gets the ticks its own scale (GraphAxisScale) reads on over that range.
//
// It is a viewport and nothing more. The frame it is given is the graph type's own, with its statistics panel and its
// specification lines and whatever they made the axes reach (a specification widening X, a normal fit raising a
// histogram's Y); a chosen end overrides that, and anything outside the range - data, a line, a curve - is clipped
// where it is drawn. Nothing is read, queried, built or computed again: not the data, the bins, the fits, the
// statistics or the specification, and not the plot model, which it never sees.
//
// With every axis Auto it returns the very frame it was given.
public static class GraphAxisViewportBuilder
{
    public static GraphRenderModel Attach(
        GraphRenderModel frame,
        GraphTypeDefinition definition,
        GraphAxisRangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        if (!definition.Supports(GraphCapability.AxisRange) || (options.X.IsAuto && options.Y.IsAuto))
        {
            return frame;
        }

        if (GraphAxisRangeRules.Check(options, definition).Count > 0)
        {
            throw new ArgumentException(
                "The axis ranges are not valid; validate the configuration first.", nameof(options));
        }

        // Ranges the rules refuse were refused before the graph was drawn; one that does not fit this graph's
        // automatic other end is only known now, and the user is told which.
        if (Conflicts(frame, definition, options) is [var conflict, ..])
        {
            throw new GraphPreparationException(conflict);
        }

        var result = frame;
        if (Resolve(frame.XAxis, definition, GraphAxisField.X, options.X) is { } x)
        {
            result = result.WithXAxis(x);
        }

        if (Resolve(frame.YAxis, definition, GraphAxisField.Y, options.Y) is { } y)
        {
            result = result.WithYAxis(y);
        }

        return result;
    }

    // What is wrong with these ranges on this frame, in the user's words, X first; empty when they can be shown. A
    // chosen minimum has to lie below the axis's automatic maximum when the maximum is left Auto, and a chosen maximum
    // above the automatic minimum, and the range that makes has to be one an axis can be drawn over. The rules a range
    // obeys on its own (GraphAxisRangeRules) are the configuration's; this is what only the graph can tell.
    public static IReadOnlyList<string> Conflicts(
        GraphRenderModel frame,
        GraphTypeDefinition definition,
        GraphAxisRangeOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        var conflicts = new List<string>();
        if (!definition.Supports(GraphCapability.AxisRange)
            || GraphAxisRangeRules.Check(options, definition).Count > 0)
        {
            return conflicts;
        }

        Conflict(conflicts, frame.XAxis, definition, GraphAxisField.X, options.X);
        Conflict(conflicts, frame.YAxis, definition, GraphAxisField.Y, options.Y);
        return conflicts;
    }

    // The automatic ends of an axis in the units the user types them in: the axis's own values, or the percentages a
    // probability axis's scores belong to.
    public static (double Minimum, double Maximum) AutoRange(GraphAxisModel axis)
    {
        ArgumentNullException.ThrowIfNull(axis);
        return axis.Scale == GraphAxisScale.Probability
            ? (ProbabilityAxis.Percent(axis.Range.Minimum), ProbabilityAxis.Percent(axis.Range.Maximum))
            : (axis.Range.Minimum, axis.Range.Maximum);
    }

    // "X-axis", "Y-axis": the axes as the setup names them.
    public static string AxisName(GraphAxisField axis) => axis == GraphAxisField.X ? "X-axis" : "Y-axis";

    private static void Conflict(
        List<string> conflicts,
        GraphAxisModel axis,
        GraphTypeDefinition definition,
        GraphAxisField field,
        GraphAxisRangeOption option)
    {
        if (!definition.SupportsAxisRange(field) || option.IsAuto)
        {
            return;
        }

        var (minimum, maximum) = Ends(axis, option);
        var auto = AutoRange(axis);
        var name = AxisName(field);
        if (option.Maximum is null && !(minimum < maximum))
        {
            conflicts.Add($"The {name} minimum ({Text(option.Minimum!.Value)}) must be below the automatic " +
                $"{name} maximum ({Text(auto.Maximum)}).");
        }
        else if (option.Minimum is null && !(minimum < maximum))
        {
            conflicts.Add($"The {name} maximum ({Text(option.Maximum!.Value)}) must be above the automatic " +
                $"{name} minimum ({Text(auto.Minimum)}).");
        }
        else if (!GraphAxisRangeRules.IsUsable(minimum, maximum))
        {
            conflicts.Add($"The {name} range is too narrow to be shown.");
        }
    }

    // The axis over the chosen range, or null for an axis left as it is.
    private static GraphAxisModel? Resolve(
        GraphAxisModel axis,
        GraphTypeDefinition definition,
        GraphAxisField field,
        GraphAxisRangeOption option)
    {
        if (!definition.SupportsAxisRange(field) || option.IsAuto)
        {
            return null;
        }

        var (minimum, maximum) = Ends(axis, option);
        var range = new GraphAxisRange(minimum, maximum);
        return new GraphAxisModel(range, Ticks(axis.Scale, range), axis.Title) { Scale = axis.Scale };
    }

    // The ends of the chosen range on the axis's own values: a typed end, placed at its score on a probability axis, or
    // the automatic one.
    private static (double Minimum, double Maximum) Ends(GraphAxisModel axis, GraphAxisRangeOption option)
    {
        double Place(double value) => axis.Scale == GraphAxisScale.Probability ? ProbabilityAxis.Score(value) : value;

        return (
            option.Minimum is { } minimum ? Place(minimum) : axis.Range.Minimum,
            option.Maximum is { } maximum ? Place(maximum) : axis.Range.Maximum);
    }

    // The ticks an axis of this scale reads on over the range.
    private static IReadOnlyList<GraphAxisTick> Ticks(GraphAxisScale scale, GraphAxisRange range) => scale switch
    {
        GraphAxisScale.Count => GraphAxisTicks.NiceCountsWithin(range),
        GraphAxisScale.Probability => ProbabilityAxis.Ticks(range),
        _ => GraphAxisTicks.Nice(range)
    };

    private static string Text(double value) => AnalysisNumberFormat.Statistic(value);
}
