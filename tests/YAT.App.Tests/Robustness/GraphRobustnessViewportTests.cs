using System.Globalization;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// #043: axis ranges over the whole named corpus, on every graph type, with ranges of every sort - one end chosen, the
// other end, both, a range inside the data, one wider than it, one that leaves every observation outside, and a very
// narrow one. Each graph is presented as the application presents it and the ranges put on it; then:
//
//   * a range is refused (GraphAxisViewportBuilder.Conflicts, and a GraphPreparationException when it is put on) exactly
//     when the range it makes with the automatic ends is not one an axis can be drawn over;
//   * otherwise the axis covers exactly the chosen ends and the automatic ones left, keeps its scale and title, and has
//     finite, increasing ticks inside the range; the other axis, the legend, the panel, the lines and the title are the
//     very ones the graph had;
//   * the plot model is the one the builder made, and the graph is drawn - light and dark in turn - without failing;
//   * Auto again gives back the automatic frame itself.
public sealed class GraphRobustnessViewportTests
{
    public static TheoryData<string> Cases => GraphRobustnessNamedTests.UnivariateCases;

    public static TheoryData<string> PairedCases => GraphRobustnessNamedTests.PairedCases;

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryDistributionGraphTakesEveryRange(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        foreach (var graph in RobustnessGraphs.UnivariateGraphs)
        {
            Check(robustnessCase.Describe(RobustnessGraphs.Name(graph) + " viewport"), graph, RobustnessGraphs.DataFor(graph, robustnessCase));
        }
    }

    [Theory]
    [MemberData(nameof(PairedCases))]
    public void AScatterPlotTakesEveryRange(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        Check(robustnessCase.Describe("Scatter viewport"), RobustnessGraph.Scatter, RobustnessGraphs.DataFor(RobustnessGraph.Scatter, robustnessCase));
    }

    private static void Check(string context, RobustnessGraph graph, GraphData data)
    {
        var built = RobustnessGraphs.Build(graph, data, cancellationToken: TestContext.Current.CancellationToken);
        if (built.Frame is not { } frame)
        {
            return;
        }

        var definition = GraphTypeDefinitions.For(RobustnessGraphs.TypeOf(graph));
        var model = built.Model;
        var themes = 0;
        foreach (var ranges in Variants(definition, frame))
        {
            var where = $"{context} ranges X {Describe(ranges.X)} Y {Describe(ranges.Y)}";
            if (GraphAxisRangeRules.Check(ranges, definition).Count > 0)
            {
                continue;
            }

            var expectedX = Expected(frame.XAxis, definition.SupportsAxisRange(GraphAxisField.X) ? ranges.X : GraphAxisRangeOption.Auto);
            var expectedY = Expected(frame.YAxis, definition.SupportsAxisRange(GraphAxisField.Y) ? ranges.Y : GraphAxisRangeOption.Auto);
            var usable = GraphAxisRangeRules.IsUsable(expectedX.Minimum, expectedX.Maximum) && GraphAxisRangeRules.IsUsable(expectedY.Minimum, expectedY.Maximum);
            var conflicts = GraphAxisViewportBuilder.Conflicts(frame, definition, ranges);

            GraphRobustnessInvariants.That(usable == (conflicts.Count == 0), where,
                $"the ranges were {(usable ? "refused" : "accepted")}: {string.Join(" / ", conflicts)}");
            if (!usable)
            {
                GraphRobustnessInvariants.That(Throws(() => new GraphPresentationState(frame, definition, GraphLabelOptions.Default, ranges)), where,
                    "a range that does not fit was put on the graph");
                continue;
            }

            var state = new GraphPresentationState(frame, definition, GraphLabelOptions.Default, ranges);
            var shown = state.Frame;
            Axis(where + " X", shown.XAxis, frame.XAxis, expectedX, ranges.X.IsAuto || !definition.SupportsAxisRange(GraphAxisField.X));
            Axis(where + " Y", shown.YAxis, frame.YAxis, expectedY, ranges.Y.IsAuto || !definition.SupportsAxisRange(GraphAxisField.Y));
            GraphRobustnessInvariants.That(
                ReferenceEquals(shown.Legend, frame.Legend) && ReferenceEquals(shown.StatisticsPanel, frame.StatisticsPanel)
                && ReferenceEquals(shown.ReferenceLines, frame.ReferenceLines) && shown.Title == frame.Title,
                where, "the ranges changed more than the axes");
            GraphRobustnessInvariants.That(ReferenceEquals(model, built.Model), where, "the plot model was replaced");

            try
            {
                RobustnessGraphs.Render(built with { Frame = shown }, themes++ % 2 == 0 ? GraphThemes.Light : GraphThemes.Dark);
            }
            catch (Exception exception)
            {
                Assert.Fail($"{where}\n  rendering threw: {exception}");
            }

            var back = state.WithAxisRanges(GraphAxisRangeOptions.Default);
            GraphRobustnessInvariants.That(ReferenceEquals(back.Frame, frame), where, "Auto again did not give back the automatic frame");
        }
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (GraphPreparationException)
        {
            return true;
        }
    }

    private static void Axis(string where, GraphAxisModel shown, GraphAxisModel auto, (double Minimum, double Maximum) expected, bool untouched)
    {
        if (untouched)
        {
            GraphRobustnessInvariants.That(ReferenceEquals(shown, auto), where, "an Auto axis was replaced");
            return;
        }

        GraphRobustnessInvariants.That(shown.Range.Minimum == expected.Minimum && shown.Range.Maximum == expected.Maximum, where,
            $"the axis covers {shown.Range.Minimum:R}..{shown.Range.Maximum:R}, not {expected.Minimum:R}..{expected.Maximum:R}");
        GraphRobustnessInvariants.That(shown.Scale == auto.Scale && shown.Title == auto.Title, where, "the axis lost its scale or title");

        // A tick is a multiple of its step, which can land an ulp or two past an end on a range this narrow for its
        // magnitude; the renderer draws within the same tolerance.
        var magnitude = Math.Max(Math.Abs(shown.Range.Minimum), Math.Abs(shown.Range.Maximum));
        var tolerance = Math.Max(shown.Range.Span * 1e-9, 4 * (Math.BitIncrement(magnitude) - magnitude));
        GraphRobustnessInvariants.That(
            shown.Ticks.All(tick => double.IsFinite(tick.Value)
                && tick.Value >= shown.Range.Minimum - tolerance && tick.Value <= shown.Range.Maximum + tolerance),
            where, $"a tick lies outside the axis: {string.Join(",", shown.Ticks.Select(tick => tick.Label))}");
        GraphRobustnessInvariants.That(shown.Ticks.Zip(shown.Ticks.Skip(1)).All(pair => pair.Second.Value > pair.First.Value), where,
            "the ticks are not in increasing order");
        if (shown.Scale == GraphAxisScale.Count)
        {
            GraphRobustnessInvariants.That(shown.Ticks.All(tick => tick.Value == Math.Round(tick.Value)), where, "a count axis has a fractional tick");
        }
    }

    // The ends the axis should cover: each chosen end on the axis's own values (a probability axis's percent at its
    // score), the automatic end where none was chosen.
    private static (double Minimum, double Maximum) Expected(GraphAxisModel axis, GraphAxisRangeOption option)
    {
        double Place(double value) => axis.Scale == GraphAxisScale.Probability ? ProbabilityAxis.Score(value) : value;
        return (option.Minimum is { } minimum ? Place(minimum) : axis.Range.Minimum, option.Maximum is { } maximum ? Place(maximum) : axis.Range.Maximum);
    }

    // Ranges of every sort for both axes, each on its own axis and one on both, in the units each axis is typed in.
    private static IEnumerable<GraphAxisRangeOptions> Variants(GraphTypeDefinition definition, GraphRenderModel frame)
    {
        var xs = OptionsFor(definition.XAxisKind, frame.XAxis).ToList();
        var ys = OptionsFor(definition.YAxisKind, frame.YAxis).ToList();
        foreach (var x in xs)
        {
            yield return new GraphAxisRangeOptions(x, GraphAxisRangeOption.Auto);
        }

        foreach (var y in ys)
        {
            yield return new GraphAxisRangeOptions(GraphAxisRangeOption.Auto, y);
        }

        for (var index = 0; index < Math.Min(xs.Count, ys.Count); index++)
        {
            yield return new GraphAxisRangeOptions(xs[index], ys[ys.Count - 1 - index]);
        }
    }

    private static IEnumerable<GraphAxisRangeOption> OptionsFor(GraphAxisKind kind, GraphAxisModel axis)
    {
        switch (kind)
        {
            case GraphAxisKind.None:
                yield break;

            case GraphAxisKind.ProbabilityPercent:
                yield return new(1, null);
                yield return new(null, 99);
                yield return new(40, 60);
                yield return new(49.9, 50.1);
                yield return new(0.0001, 99.9999);
                yield return new(99.99, null);
                yield break;

            case GraphAxisKind.Percent:
                yield return new(0, 100);
                yield return new(90, null);
                yield return new(null, 10);
                yield return new(45, 55);
                yield return new(100, null);
                yield break;
        }

        var (low, high) = (axis.Range.Minimum, axis.Range.Maximum);
        var span = axis.Range.Span;
        yield return new(low + (span / 4), null);
        yield return new(null, high - (span / 4));
        yield return new(low + (span / 10), low + (span * 0.6));
        yield return new(low - (span * 10), high + (span * 10));
        yield return new(high + span, high + (2 * span));
        yield return new((low + high) / 2, ((low + high) / 2) + (span * 1e-6));
        yield return new(high, null);
        if (kind == GraphAxisKind.NonNegative)
        {
            yield return new(0, high * 3);
        }
    }

    private static string Describe(GraphAxisRangeOption option) =>
        $"{(option.Minimum is { } minimum ? minimum.ToString("R", CultureInfo.InvariantCulture) : "auto")}.." +
        $"{(option.Maximum is { } maximum ? maximum.ToString("R", CultureInfo.InvariantCulture) : "auto")}";
}
