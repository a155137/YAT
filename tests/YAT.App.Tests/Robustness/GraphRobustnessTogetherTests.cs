using System.Globalization;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// #041: several variables drawn together, over the whole named corpus. Each case becomes three variables - the case, its
// negation with the groups in reverse order, and its first half - combined into one graph of series and drawn by the
// histogram, the probability plot and the empirical CDF through the harness's own checks (every invariant a
// single-variable graph holds, in both themes). On top of those: the series are the variables' groups, labelled
// "Variable / Group", in variable order and global first-seen group order, each holding exactly its variable's
// observations of that group.
public sealed class GraphRobustnessTogetherTests
{
    public static TheoryData<string> Cases => GraphRobustnessNamedTests.UnivariateCases;

    private static readonly RobustnessGraph[] Graphs = [RobustnessGraph.Histogram, RobustnessGraph.ProbabilityPlot, RobustnessGraph.EmpiricalCdf];

    [Theory]
    [MemberData(nameof(Cases))]
    public void VariablesDrawnTogetherHoldEveryInvariant(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        foreach (var graph in Graphs)
        {
            var reg1 = (UnivariateGraphData)RobustnessGraphs.DataFor(graph, robustnessCase);
            var variables = new[]
            {
                reg1,
                Variant(reg1, "Negated", [.. reg1.Values.ToArray().Select(value => -value)], reverseGroups: true),
                Variant(reg1, "FirstHalf", [.. reg1.Values.ToArray().Take(reg1.Count / 2)], reverseGroups: false)
            };
            var context = robustnessCase.Describe(RobustnessGraphs.Name(graph) + " together");

            var combined = GraphVariablesTogether.Combine(
                new MultiVariableGraphData(reg1.GraphType, reg1.WorksheetId, variables), TestContext.Current.CancellationToken);

            // Every observation of every variable, in series order, each in its variable's own group.
            var expected = Expected(variables);
            GraphRobustnessInvariants.That(combined.Count == expected.Count, context, $"{combined.Count} observations, not {expected.Count}");
            var labels = ((StringGroupData)combined.Group!).Values.ToArray();
            GraphRobustnessInvariants.That(
                labels.SequenceEqual(expected.Select(item => item.Label)) && combined.Values.ToArray().SequenceEqual(expected.Select(item => item.Value)),
                context,
                "the combined observations are not the variables' groups in variable and first-seen order");

            var built = GraphRobustnessInvariants.Exercise(context, graph, combined, GraphThemes.Dark);
            if (built.Frame is not { } frame)
            {
                GraphRobustnessInvariants.That(expected.Count == 0, context, "a graph with observations drew nothing");
                continue;
            }

            var series = expected.Select(item => item.Label).Distinct().ToArray();
            GraphRobustnessInvariants.That(
                frame.Legend is { } legend && legend.Entries.Select(entry => entry.Label).SequenceEqual(series)
                    && legend.Entries.Select(entry => entry.SeriesIndex).SequenceEqual(Enumerable.Range(0, series.Length)),
                context,
                $"legend {string.Join(",", frame.Legend?.Entries.Select(entry => entry.Label) ?? [])} is not the series {string.Join(",", series)}");
            GraphRobustnessInvariants.That(frame.Legend!.Title == GraphVariablesTogether.Header(reg1.Group?.Column.Name), context, "legend title");
        }
    }

    private static UnivariateGraphData Variant(UnivariateGraphData source, string name, double[] values, bool reverseGroups)
    {
        GraphGroupData? group = source.Group switch
        {
            StringGroupData text => new StringGroupData(text.Column, Arrange(text.Values.ToArray(), values.Length, reverseGroups)),
            NumericGroupData numeric => new NumericGroupData(numeric.Column, Arrange(numeric.Values.ToArray(), values.Length, reverseGroups)),
            _ => null
        };

        return new UnivariateGraphData(source.GraphType, source.WorksheetId, new GraphColumnInfo(Guid.NewGuid(), name, source.Variable.DataType), values, group);
    }

    private static T[] Arrange<T>(T[] groups, int count, bool reverse) =>
        reverse ? [.. groups.Reverse().Take(count)] : [.. groups.Take(count)];

    // The observations drawn together, worked out on their own: groups ranked by first sight across the variables in
    // order, then each variable's observations group by group in that order, each group in its own row order.
    private static List<(string Label, double Value)> Expected(IReadOnlyList<UnivariateGraphData> variables)
    {
        var order = new List<string?>();
        foreach (var variable in variables)
        {
            for (var row = 0; row < variable.Count; row++)
            {
                var group = GroupLabel(variable.Group, row);
                if (!order.Contains(group))
                {
                    order.Add(group);
                }
            }
        }

        var expected = new List<(string, double)>();
        foreach (var variable in variables)
        {
            foreach (var group in order)
            {
                for (var row = 0; row < variable.Count; row++)
                {
                    if (GroupLabel(variable.Group, row) == group)
                    {
                        expected.Add((GraphVariablesTogether.SeriesLabel(variable.Variable.Name, group), variable.Values.Span[row]));
                    }
                }
            }
        }

        return expected;
    }

    private static string? GroupLabel(GraphGroupData? group, int row) => group switch
    {
        null => null,
        _ when group.IsMissing(row) => "(Missing)",
        StringGroupData text => text.Values.Span[row],
        NumericGroupData numeric => numeric.Values.Span[row]!.Value.ToString("0.####", CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown group data.")
    };
}
