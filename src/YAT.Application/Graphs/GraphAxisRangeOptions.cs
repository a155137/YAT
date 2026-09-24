namespace YAT.Application.Graphs;

// The two axes of a graph, as a range option names them.
public enum GraphAxisField
{
    X,
    Y
}

// One end of an axis range.
public enum GraphAxisBound
{
    Minimum,
    Maximum
}

// What an axis of a graph type reads, as far as a range the user types for it is concerned (see GraphTypeDefinition).
// The graph type declares it; the validator reads the allowed values from it and the setup offers a range only for an
// axis that has one.
public enum GraphAxisKind
{
    // No range can be chosen: the axis is not numeric (a box plot's categories) or the graph type has no such axis.
    None,

    // Any number: a measurement axis.
    Numeric,

    // A number that cannot be below zero: what a histogram's bars measure (a count, a percent or a density).
    NonNegative,

    // A percentage from 0 to 100, read linearly: an empirical CDF's cumulative percent.
    Percent,

    // A percentage on a probability scale: a probability plot's percent, typed as the percent it shows (1 for 1 %)
    // and placed at the normal score it belongs to. 0 and 100 lie infinitely far out, so only
    // MinimumProbabilityPercent to MaximumProbabilityPercent can be typed.
    ProbabilityPercent
}

// The range of one axis as the user chose it: a minimum, a maximum, both or neither. An end without a value is chosen
// automatically, exactly as without the option, so the default - neither - is the graph as it always was.
//
// A range is a viewport over the graph: it decides what part of the graph is shown, never what the graph computes.
// Bins, statistics, fitted lines and specifications are the same whatever it is, and data outside it is simply not
// seen.
public sealed record GraphAxisRangeOption(double? Minimum = null, double? Maximum = null)
{
    public static GraphAxisRangeOption Auto { get; } = new();

    public bool IsAuto => Minimum is null && Maximum is null;

    public double? For(GraphAxisBound bound) => bound == GraphAxisBound.Minimum ? Minimum : Maximum;
}

// The ranges a graph's two axes are shown over (Task #043). Part of the graph's configuration: a setting of the graph
// itself, which a graph window can change afterwards without its data. Graph types that do not declare
// GraphCapability.AxisRange ignore it, and an axis whose kind is None ignores its range; the data query and the graph
// types' builders never read it.
//
// Values are in the units the axis shows: the data's own, or percent on a percent or probability axis.
public sealed record GraphAxisRangeOptions(GraphAxisRangeOption X, GraphAxisRangeOption Y)
{
    // Both axes chosen automatically: the graph as it always was.
    public static GraphAxisRangeOptions Default { get; } = new(GraphAxisRangeOption.Auto, GraphAxisRangeOption.Auto);

    public GraphAxisRangeOption For(GraphAxisField axis) => axis == GraphAxisField.X ? X : Y;
}

// Why an axis range cannot be used.
public enum GraphAxisRangeProblemKind
{
    // A value is not a finite number.
    NotFinite,

    // A value lies outside what the axis can show (below 0 on a histogram's axis, outside 0..100 on a percent axis,
    // outside the probability axis's percentages).
    OutsideAxis,

    // The minimum is not below the maximum.
    NotIncreasing,

    // The range is too narrow for its numbers to tell its ends apart.
    TooNarrow
}

// One problem of an axis range: which axis, which end (none for a problem of the range as a whole) and what.
public sealed record GraphAxisRangeProblem(GraphAxisField Axis, GraphAxisBound? Bound, GraphAxisRangeProblemKind Kind);

// The rules axis ranges obey before any data is seen. Whether a range with one end chosen fits the graph's automatic
// other end depends on the data, so that is checked when the graph is presented, not here.
public static class GraphAxisRangeRules
{
    // The percentages a probability axis can be given: its farthest labelled tails.
    public const double MinimumProbabilityPercent = 0.0001;

    public const double MaximumProbabilityPercent = 99.9999;

    // A range narrower than this fraction of its magnitude cannot tell its ends, or ticks between them, apart.
    public const double MinimumRelativeWidth = 1e-12;

    // What is wrong with the ranges of the axes this graph type lets the user choose, X first, each minimum before its
    // maximum. Empty when they can be used. An axis without a range to choose is never checked.
    public static IReadOnlyList<GraphAxisRangeProblem> Check(
        GraphAxisRangeOptions options,
        GraphTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(definition);

        var problems = new List<GraphAxisRangeProblem>();
        Check(problems, GraphAxisField.X, definition.XAxisKind, options.X);
        Check(problems, GraphAxisField.Y, definition.YAxisKind, options.Y);
        return problems;
    }

    // Whether a value can be typed for an axis of this kind.
    public static bool Allows(GraphAxisKind kind, double value) => kind switch
    {
        GraphAxisKind.None => false,
        GraphAxisKind.NonNegative => double.IsFinite(value) && value >= 0,
        GraphAxisKind.Percent => double.IsFinite(value) && value is >= 0 and <= 100,
        GraphAxisKind.ProbabilityPercent => double.IsFinite(value)
            && value is >= MinimumProbabilityPercent and <= MaximumProbabilityPercent,
        _ => double.IsFinite(value)
    };

    // Whether minimum to maximum is a range an axis can be drawn over: increasing, of a width a double can hold, and
    // wide enough for its ends to be told apart.
    public static bool IsUsable(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || !(maximum > minimum))
        {
            return false;
        }

        var width = maximum - minimum;
        return double.IsFinite(width) && width > MinimumRelativeWidth * Math.Max(Math.Abs(minimum), Math.Abs(maximum));
    }

    private static void Check(
        List<GraphAxisRangeProblem> problems,
        GraphAxisField axis,
        GraphAxisKind kind,
        GraphAxisRangeOption option)
    {
        if (kind == GraphAxisKind.None || option.IsAuto)
        {
            return;
        }

        var usable = true;
        foreach (var bound in new[] { GraphAxisBound.Minimum, GraphAxisBound.Maximum })
        {
            if (option.For(bound) is not { } value)
            {
                continue;
            }

            if (!double.IsFinite(value))
            {
                problems.Add(new GraphAxisRangeProblem(axis, bound, GraphAxisRangeProblemKind.NotFinite));
                usable = false;
            }
            else if (!Allows(kind, value))
            {
                problems.Add(new GraphAxisRangeProblem(axis, bound, GraphAxisRangeProblemKind.OutsideAxis));
                usable = false;
            }
        }

        // Only a range with both ends chosen can be out of order or too narrow before the data is seen.
        if (!usable || option is not { Minimum: { } minimum, Maximum: { } maximum })
        {
            return;
        }

        if (!(minimum < maximum))
        {
            problems.Add(new GraphAxisRangeProblem(axis, null, GraphAxisRangeProblemKind.NotIncreasing));
        }
        else if (!IsUsable(minimum, maximum))
        {
            problems.Add(new GraphAxisRangeProblem(axis, null, GraphAxisRangeProblemKind.TooNarrow));
        }
    }
}
