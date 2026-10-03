namespace YAT.Application.Graphs;

// The major ticks of one axis as the user chose them (Task #054): Auto - the ticks the axis's own scale reads on, as a
// graph always had them - a fixed interval, or values of the user's own. Closed: these three are the only kinds.
//
// Ticks are not a range. A range decides what part of the graph is shown; ticks only mark it, and each is chosen apart
// from the other: changing a range keeps the ticks, and changing the ticks keeps the range. Values are in the units the
// axis shows - the data's own, or percent on a percent or probability axis - like a range's.
public abstract record GraphAxisTickOption
{
    private GraphAxisTickOption()
    {
    }

    public static GraphAxisTickOption Auto { get; } = new AutoTicks();

    public bool IsAuto => this is AutoTicks;

    // Every multiple of Interval the axis shows (counted from zero, so 0.02 marks 14.90, 14.92, 14.94, ...); on a
    // probability axis every multiple in percent (10 marks 10 %, 20 %, ... 90 %).
    public sealed record FixedInterval(double Interval) : GraphAxisTickOption;

    // Ticks at these values: ascending, without repeats, whatever order they were given in. All of them are kept; only
    // those the axis shows are drawn, so a value outside the range waits there until the range reaches it.
    public sealed record CustomValues : GraphAxisTickOption
    {
        public CustomValues(IEnumerable<double> values)
        {
            ArgumentNullException.ThrowIfNull(values);

            // -0 and +0 are one value, as they are one tick.
            Values = [.. values.Select(value => value == 0 ? 0 : value).Distinct().Order()];
        }

        public IReadOnlyList<double> Values { get; }

        public bool Equals(CustomValues? other) => other is not null && Values.SequenceEqual(other.Values);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

    private sealed record AutoTicks : GraphAxisTickOption;
}

// The ticks of a graph's two axes. Kept by the graph window with the graph it shows (GraphPresentationState), not by the
// configuration: a graph always starts on Auto ticks.
public sealed record GraphAxisTickOptions(GraphAxisTickOption X, GraphAxisTickOption Y)
{
    // Both axes on Auto ticks: the graph as it always was.
    public static GraphAxisTickOptions Default { get; } = new(GraphAxisTickOption.Auto, GraphAxisTickOption.Auto);

    public bool IsAuto => X.IsAuto && Y.IsAuto;

    public GraphAxisTickOption For(GraphAxisField axis) => axis == GraphAxisField.X ? X : Y;

    public GraphAxisTickOptions With(GraphAxisField axis, GraphAxisTickOption option) =>
        axis == GraphAxisField.X ? this with { X = option } : this with { Y = option };
}

// Why the ticks of an axis cannot be used.
public enum GraphAxisTickProblemKind
{
    // The interval is not a finite number above zero.
    IntervalNotPositive,

    // No values were given.
    NoValues,

    // More values than an axis can be given (GraphAxisTickRules.MaximumValues).
    TooManyValues,

    // A value is not a finite number.
    ValueNotFinite,

    // A value lies outside what the axis can show (below 0 on a histogram's axis, outside 0..100 on a percent axis,
    // outside the probability axis's percentages).
    ValueOutsideAxis
}

// One problem of an axis's ticks; Value is the value at fault, where one is.
public sealed record GraphAxisTickProblem(GraphAxisField Axis, GraphAxisTickProblemKind Kind, double? Value = null);

// The rules ticks obey before any range is known. How many ticks an interval makes depends on the range the axis is
// shown over, so that is checked where the graph is presented.
public static class GraphAxisTickRules
{
    // The most values one axis can be given, and the most ticks an interval may draw on it.
    public const int MaximumValues = 100;

    public const int MaximumIntervalTicks = 100;

    // What is wrong with the ticks of one axis of this kind, or nothing. A count axis (a histogram's frequency) is
    // marked like any other non-negative axis: its Auto ticks are whole counts, but chosen ticks may lie between them.
    public static IReadOnlyList<GraphAxisTickProblem> Check(
        GraphAxisField axis,
        GraphAxisKind kind,
        GraphAxisTickOption option)
    {
        ArgumentNullException.ThrowIfNull(option);

        var problems = new List<GraphAxisTickProblem>();
        switch (option)
        {
            case GraphAxisTickOption.FixedInterval { Interval: var interval }:
                if (!double.IsFinite(interval) || !(interval > 0))
                {
                    problems.Add(new GraphAxisTickProblem(axis, GraphAxisTickProblemKind.IntervalNotPositive, interval));
                }

                break;

            case GraphAxisTickOption.CustomValues { Values: var values }:
                if (values.Count == 0)
                {
                    problems.Add(new GraphAxisTickProblem(axis, GraphAxisTickProblemKind.NoValues));
                }
                else if (values.Count > MaximumValues)
                {
                    problems.Add(new GraphAxisTickProblem(axis, GraphAxisTickProblemKind.TooManyValues));
                }
                else
                {
                    foreach (var value in values)
                    {
                        if (!double.IsFinite(value))
                        {
                            problems.Add(new GraphAxisTickProblem(axis, GraphAxisTickProblemKind.ValueNotFinite, value));
                            break;
                        }

                        if (!GraphAxisRangeRules.Allows(kind, value))
                        {
                            problems.Add(new GraphAxisTickProblem(axis, GraphAxisTickProblemKind.ValueOutsideAxis, value));
                            break;
                        }
                    }
                }

                break;
        }

        return problems;
    }
}
