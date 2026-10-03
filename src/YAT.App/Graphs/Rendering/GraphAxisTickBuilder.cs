using System.Globalization;
using YAT.Application.Graphs;
using YAT.app.Analyses;

namespace YAT.app.Graphs.Rendering;

// Puts the ticks the user chose on a presented frame (Task #054): the step right after the axis ranges
// (GraphAxisViewportBuilder), so an axis is first given its range and then marked. An axis on Auto ticks keeps the
// ticks its own scale gave it over that range; an axis with an interval or values of its own is marked with exactly
// those of them that lie on the range it shows. Its range never changes here.
//
// Only the ticks that lie on the range go into the frame. The others are kept with the options, not the frame, so they
// cannot change how wide the tick labels are measured and so the layout; they are drawn as soon as a range reaches them.
//
// Labels are made here, one format for the whole axis: as many decimals as the interval - or the most precise value
// shown - needs, scientific notation for large values, percent on a percent or probability axis (where each percentage
// is placed at the normal score it belongs to). How a value was typed is not kept.
//
// Nothing is read, queried, built or computed again. With every axis on Auto ticks it returns the very frame it was
// given.
public static class GraphAxisTickBuilder
{
    // Ticks a rounding error outside a range still belong to it: an end typed as 15.1 is a tick of an interval of 0.02.
    private const double Slack = 1e-9;

    // Past 2^53 a double no longer holds every whole number, so multiples of an interval are not counted out there.
    private const double WholeNumberLimit = 9007199254740992d;

    // More decimals than this, or values this large, read better in scientific notation.
    private const int MaximumDecimals = 10;
    private const double LargeValue = 1e6;
    private const string ScientificFormat = "0.######E+0";

    public static GraphRenderModel Attach(
        GraphRenderModel frame,
        GraphTypeDefinition definition,
        GraphAxisTickOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        if (options.IsAuto || !definition.Supports(GraphCapability.AxisRange))
        {
            return frame;
        }

        if (Problems(frame, definition, options) is [var problem, ..])
        {
            throw new ArgumentException($"The axis ticks cannot be used: {problem}", nameof(options));
        }

        var result = frame;
        if (Marked(frame.XAxis, definition, GraphAxisField.X, options.X) is { } x)
        {
            result = result.WithXAxis(x);
        }

        if (Marked(frame.YAxis, definition, GraphAxisField.Y, options.Y) is { } y)
        {
            result = result.WithYAxis(y);
        }

        return result;
    }

    // What is wrong with these ticks on this frame - whose axes have their final ranges - in the user's words, X first;
    // empty when they can be drawn. An axis the user cannot choose a range for is never checked.
    public static IReadOnlyList<string> Problems(
        GraphRenderModel frame,
        GraphTypeDefinition definition,
        GraphAxisTickOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        var problems = new List<string>();
        if (definition.Supports(GraphCapability.AxisRange))
        {
            problems.AddRange(Problems(frame.XAxis, definition, GraphAxisField.X, options.X));
            problems.AddRange(Problems(frame.YAxis, definition, GraphAxisField.Y, options.Y));
        }

        return problems;
    }

    // What is wrong with these ticks over these ranges: autoFrame is the frame before any range was chosen, and the
    // ranges have to be ones it can be shown over (GraphAxisRangeRules, GraphAxisViewportBuilder.Conflicts).
    public static IReadOnlyList<string> Problems(
        GraphRenderModel autoFrame,
        GraphTypeDefinition definition,
        GraphAxisRangeOptions ranges,
        GraphAxisTickOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.IsAuto ? [] : Problems(GraphAxisViewportBuilder.Attach(autoFrame, definition, ranges), definition, options);
    }

    // What is wrong with the ticks of one axis over the range it shows, or nothing.
    public static IReadOnlyList<string> Problems(
        GraphAxisModel axis,
        GraphTypeDefinition definition,
        GraphAxisField field,
        GraphAxisTickOption option)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(option);

        if (!definition.SupportsAxisRange(field) || option.IsAuto)
        {
            return [];
        }

        var kind = definition.AxisKind(field);
        var rules = GraphAxisTickRules.Check(field, kind, option);
        if (rules.Count > 0)
        {
            return [.. rules.Select(problem => Message(problem, kind))];
        }

        if (option is GraphAxisTickOption.FixedInterval { Interval: var interval }
            && IntervalValues(axis, interval) is not { } values)
        {
            return [$"The {GraphAxisViewportBuilder.AxisName(field)} tick interval {Text(interval)} would draw more " +
                $"than {GraphAxisTickRules.MaximumIntervalTicks} ticks over the range shown. Enter a larger interval."];
        }

        return [];
    }

    // How many of an axis's own values lie off the range it shows: kept, but not drawn.
    public static int OutsideCount(GraphAxisModel axis, GraphAxisTickOption.CustomValues values)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(values);
        return values.Values.Count(value => !IsShown(axis, value));
    }

    // The ticks of an axis under an interval or values of the user's own, over the range it shows: only those on it.
    // The option has to be one Problems accepts.
    public static IReadOnlyList<GraphAxisTick> Ticks(GraphAxisModel axis, GraphAxisTickOption option)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(option);

        return option switch
        {
            GraphAxisTickOption.FixedInterval { Interval: var interval } =>
                Label(axis, IntervalValues(axis, interval)
                    ?? throw new ArgumentException("The interval draws too many ticks over this range.", nameof(option)),
                    DecimalsOf(interval)),
            GraphAxisTickOption.CustomValues { Values: var values } =>
                Label(axis, [.. values.Where(value => IsShown(axis, value))], decimals: null),
            _ => axis.Ticks
        };
    }

    // The axis marked as the option says, or null for an axis left as it is.
    private static GraphAxisModel? Marked(
        GraphAxisModel axis,
        GraphTypeDefinition definition,
        GraphAxisField field,
        GraphAxisTickOption option)
    {
        if (!definition.SupportsAxisRange(field) || option.IsAuto)
        {
            return null;
        }

        return new GraphAxisModel(axis.Range, Ticks(axis, option), axis.Title) { Scale = axis.Scale };
    }

    // The multiples of an interval the axis shows, in the units the axis is typed in (percent on a probability axis),
    // or null when they are more than an axis may draw. Counted, never walked: a loop over the multiples themselves
    // would not end where adding the interval no longer changes a double.
    private static IReadOnlyList<double>? IntervalValues(GraphAxisModel axis, double interval)
    {
        var (lowest, highest) = Shown(axis);
        var first = Math.Ceiling((lowest / interval) - Slack);
        var last = Math.Floor((highest / interval) + Slack);
        var count = last - first + 1;
        // A multiple or two at the very ends may still fall a rounding error off the range, so the count is only an
        // upper bound of what is drawn; past it by more than that, the interval is too small for the range.
        if (!double.IsFinite(count) || count > GraphAxisTickRules.MaximumIntervalTicks + 2
            || Math.Abs(first) >= WholeNumberLimit || Math.Abs(last) >= WholeNumberLimit)
        {
            return null;
        }

        var values = new List<double>();
        for (var index = 0d; index < count; index++)
        {
            var value = (first + index) * interval;
            if (IsShown(axis, value))
            {
                values.Add(value == 0 ? 0 : value);
                if (values.Count > GraphAxisTickRules.MaximumIntervalTicks)
                {
                    return null;
                }
            }
        }

        return values;
    }

    // The range an axis shows in the units it is typed in: its own values, or the percentages a probability axis's
    // scores belong to - never past the percentages such an axis can be given.
    private static (double Lowest, double Highest) Shown(GraphAxisModel axis)
    {
        if (axis.Scale != GraphAxisScale.Probability)
        {
            return (axis.Range.Minimum, axis.Range.Maximum);
        }

        return (
            Math.Max(ProbabilityAxis.Percent(axis.Range.Minimum), GraphAxisRangeRules.MinimumProbabilityPercent),
            Math.Min(ProbabilityAxis.Percent(axis.Range.Maximum), GraphAxisRangeRules.MaximumProbabilityPercent));
    }

    // Where a value typed for the axis is placed on it: the value itself, or its normal score on a probability axis.
    private static double Place(GraphAxisModel axis, double value) =>
        axis.Scale == GraphAxisScale.Probability ? ProbabilityAxis.Score(value) : value;

    // Whether a value typed for the axis lies on the range it shows (its ends included, a rounding error either side).
    private static bool IsShown(GraphAxisModel axis, double value)
    {
        if (axis.Scale == GraphAxisScale.Probability
            && value is < GraphAxisRangeRules.MinimumProbabilityPercent or > GraphAxisRangeRules.MaximumProbabilityPercent)
        {
            return false;
        }

        var place = Place(axis, value);
        var slack = axis.Range.Span * Slack;
        return double.IsFinite(place) && place >= axis.Range.Minimum - slack && place <= axis.Range.Maximum + slack;
    }

    // The ticks at these values (typed units), every label in one format: as many decimals as decimals says - or,
    // without it, as the most precise value needs - or scientific notation when the values are large or need more
    // decimals than are readable. On a count axis too: its Auto ticks are whole counts, but chosen ones need not be.
    private static IReadOnlyList<GraphAxisTick> Label(GraphAxisModel axis, IReadOnlyList<double> values, int? decimals)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var places = decimals ?? values.Max(DecimalsOf);
        var format = values.Max(Math.Abs) >= LargeValue || places > MaximumDecimals
            ? ScientificFormat
            : "F" + places;

        return [.. values.Select(value => new GraphAxisTick(Place(axis, value), value.ToString(format, CultureInfo.InvariantCulture)))];
    }

    // The decimals a value needs to be written exactly as the shortest text that reads back as it: 0.02 needs 2,
    // 1E-05 needs 5, 1.5E-07 needs 8, 12 none.
    internal static int DecimalsOf(double value)
    {
        var text = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
        var exponentAt = text.IndexOfAny(['E', 'e']);
        var mantissa = exponentAt < 0 ? text : text[..exponentAt];
        var exponent = exponentAt < 0 ? 0 : int.Parse(text[(exponentAt + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        var point = mantissa.IndexOf('.');
        var mantissaDecimals = point < 0 ? 0 : mantissa.Length - point - 1;
        return Math.Max(0, mantissaDecimals - exponent);
    }

    // A problem of the rules in the user's words.
    private static string Message(GraphAxisTickProblem problem, GraphAxisKind kind)
    {
        var name = GraphAxisViewportBuilder.AxisName(problem.Axis);
        var value = problem.Value is { } number ? Text(number) : string.Empty;
        return problem.Kind switch
        {
            GraphAxisTickProblemKind.IntervalNotPositive => $"The {name} tick interval must be a number above 0.",
            GraphAxisTickProblemKind.NoValues => $"Enter at least one {name} tick value.",
            GraphAxisTickProblemKind.TooManyValues =>
                $"The {name} can have at most {GraphAxisTickRules.MaximumValues} tick values.",
            GraphAxisTickProblemKind.ValueNotFinite => $"Every {name} tick value must be a number.",
            _ => kind switch
            {
                GraphAxisKind.NonNegative => $"The {name} tick values cannot be below 0 ({value} is).",
                GraphAxisKind.Percent => $"The {name} tick values must be from 0 to 100 ({value} is not).",
                GraphAxisKind.ProbabilityPercent =>
                    $"The {name} tick values must be from {Percent(GraphAxisRangeRules.MinimumProbabilityPercent)} " +
                    $"to {Percent(GraphAxisRangeRules.MaximumProbabilityPercent)} (%) ({value} is not).",
                _ => $"Every {name} tick value must be a number."
            }
        };
    }

    private static string Text(double value) => AnalysisNumberFormat.Statistic(value);

    private static string Percent(double value) => value.ToString(CultureInfo.InvariantCulture);
}
