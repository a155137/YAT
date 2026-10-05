using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The order a graph's groups are drawn in (Task #059), in every graph type that groups: a numeric group column's groups
// from the smallest value up - negative, decimal and repeated values alike - with "(Missing)" last; a text group column's
// in the order they are first seen. One order for the legend, the colours (series index) and the drawing order, and for
// the statistics panel; the same whatever order the rows come in; and, in a graph drawn in panels (Task #058), the same
// in every panel.
public class GraphGroupOrderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static NumericGroupData Numbers(params double?[] values) => new(Column("SITE"), values);

    private static StringGroupData Text(params string?[] values) => new(Column("SITE", WorksheetDataType.String), values);

    private static double[] Values(int count) => [.. Enumerable.Range(0, count).Select(index => 15 + (index * 0.01))];

    // Every grouping graph type: the labels of its series in drawing order, each with its series index (its colour), and
    // its legend - which must be the same.
    public static TheoryData<GraphType> GroupedGraphs =>
        [GraphType.ScatterPlot, GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf, GraphType.BoxPlot];

    private static (IReadOnlyList<(string Label, int Index)> Series, IReadOnlyList<(string Label, int Index)> Legend) Drawn(
        GraphType type, double[] values, GraphGroupData group)
    {
        IReadOnlyList<(string, int)> Legend(GraphRenderModel frame) => [.. frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex))];
        var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), values, group);
        switch (type)
        {
            case GraphType.ScatterPlot:
                var scatter = new ScatterRenderModelBuilder().Build(
                    new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), values, values, group), new ScatterPlotLabels("Reg1", "Reg2", "SITE"), Token)!;
                return ([.. scatter.Series.Select(series => (series.Label, series.SeriesIndex))], Legend(scatter.Frame));
            case GraphType.Histogram:
                var histogram = new HistogramRenderModelBuilder().Build(univariate, new HistogramPlotLabels("Reg1", "SITE"), Token)!;
                return ([.. histogram.Series.Select(series => (series.Label, series.SeriesIndex))], Legend(histogram.Frame));
            case GraphType.ProbabilityPlot:
                var probability = new ProbabilityPlotRenderModelBuilder().Build(univariate, new ProbabilityPlotLabels("Reg1", "SITE"), Token)!;
                return ([.. probability.Series.Select(series => (series.Label, series.SeriesIndex))], Legend(probability.Frame));
            case GraphType.EmpiricalCdf:
                var cdf = new EmpiricalCdfRenderModelBuilder().Build(univariate, new EmpiricalCdfLabels("Reg1", "SITE"), Token)!;
                return ([.. cdf.Series.Select(series => (series.Label, series.SeriesIndex))], Legend(cdf.Frame));
            default:
                var box = new BoxPlotRenderModelBuilder().Build(
                    new MultiVariableGraphData(type, Guid.Empty, [univariate]), new BoxPlotLabels(["Reg1"], "SITE"), Token)!;
                return ([.. box.Boxes.Select(item => (item.Label.Split(" / ")[1], item.SeriesIndex))], Legend(box.Frame));
        }
    }

    private static (string, int)[] InOrder(params string[] labels) => [.. labels.Select((label, index) => (label, index))];

    [Theory]
    [MemberData(nameof(GroupedGraphs))]
    public void NumericGroupsAreDrawnFromTheSmallestValueUp(GraphType type)
    {
        var (series, legend) = Drawn(type, Values(4), Numbers(5, 1, 7, 3));

        Assert.Equal(InOrder("1", "3", "5", "7"), series);
        Assert.Equal(series, legend);
    }

    [Theory]
    [MemberData(nameof(GroupedGraphs))]
    public void NegativeAndDecimalGroupsAreOrderedAsNumbersAndMissingComesLast(GraphType type)
    {
        var (series, legend) = Drawn(type, Values(5), Numbers(null, 2.5, -1, 10, 0));

        Assert.Equal(InOrder("-1", "0", "2.5", "10", "(Missing)"), series);
        Assert.Equal(series, legend);
    }

    [Theory]
    [MemberData(nameof(GroupedGraphs))]
    public void RepeatedGroupValuesMakeOneGroupInItsPlace(GraphType type)
    {
        var (series, legend) = Drawn(type, Values(7), Numbers(3, 1, 3, null, 1, 2, 3));

        Assert.Equal(InOrder("1", "2", "3", "(Missing)"), series);
        Assert.Equal(series, legend);
    }

    [Theory]
    [MemberData(nameof(GroupedGraphs))]
    public void TextGroupsKeepTheOrderTheyAreFirstSeenIn(GraphType type)
    {
        var (series, legend) = Drawn(type, Values(6), Text("SITE 5", "SITE 1", null, "SITE 5", "B", "A"));

        Assert.Equal(InOrder("SITE 5", "SITE 1", "(Missing)", "B", "A"), series);
        Assert.Equal(series, legend);
    }

    [Theory]
    [MemberData(nameof(GroupedGraphs))]
    public void TheOrderDoesNotDependOnTheOrderOfTheRows(GraphType type)
    {
        double?[] sites = [5, 1, null, 7, 3, 1, 7, 5, 3, null];
        var values = Values(sites.Length);
        int[] shuffle = [7, 2, 9, 0, 4, 1, 8, 3, 6, 5];

        var first = Drawn(type, values, Numbers(sites));
        var second = Drawn(type, [.. shuffle.Select(row => values[row])], Numbers([.. shuffle.Select(row => sites[row])]));

        Assert.Equal(InOrder("1", "3", "5", "7", "(Missing)"), first.Series);
        Assert.Equal(first.Series, second.Series);
        Assert.Equal(first.Legend, second.Legend);
    }

    [Fact]
    public void TheStatisticsPanelListsTheGroupsInTheGraphsOrderWithTheirColours()
    {
        var data = new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), new double[] { 1, 2, 3, 4, 5, 6 }, Numbers(7, null, 3, 7, 1, 3));

        var panel = GraphStatisticsPanelBuilder.Build(data, Token)!;
        var histogram = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "SITE"), Token)!;

        Assert.Equal(InOrder("1", "3", "7", "(Missing)"), panel.Rows.Select(row => (row.Label, row.SeriesIndex!.Value)));
        Assert.Equal([1, 2, 2, 1], panel.Rows.Select(row => row.Count));
        Assert.Equal([5d, 4.5, 2.5, 2], panel.Rows.Select(row => row.Mean));
        Assert.Equal(histogram.Series.Select(series => (series.Label, series.SeriesIndex)), panel.Rows.Select(row => (row.Label, row.SeriesIndex!.Value)));
    }

    [Fact]
    public void EachGroupKeepsItsOwnObservationsWhenTheGroupsAreReordered()
    {
        var model = new HistogramRenderModelBuilder().Build(
            new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), new double[] { 1, 2, 3, 4 }, Numbers(7, 3, 7, 3)),
            new HistogramPlotLabels("Reg1", "SITE"), Token)!;

        Assert.Equal(["3", "7"], model.Series.Select(series => series.Label));
        Assert.Equal(2, model.Series[0].Counts.Sum());
        var cdf = new EmpiricalCdfRenderModelBuilder().Build(
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.Empty, Column("Reg1"), new double[] { 1, 2, 3, 4 }, Numbers(7, 3, 7, 3)),
            new EmpiricalCdfLabels("Reg1", "SITE"), Token)!;
        Assert.Equal([2d, 4], cdf.Series[0].Points.ToArray().Select(point => point.Value));
        Assert.Equal([1d, 3], cdf.Series[1].Points.ToArray().Select(point => point.Value));
    }

    [Fact]
    public void ABoxPlotOfSeveralVariablesColoursEveryGroupInOneNumericOrder()
    {
        var data = new MultiVariableGraphData(GraphType.BoxPlot, Guid.Empty,
        [
            new UnivariateGraphData(GraphType.BoxPlot, Guid.Empty, Column("Reg1"), new double[] { 1, 2, 3 }, Numbers(7, 3, 7)),
            new UnivariateGraphData(GraphType.BoxPlot, Guid.Empty, Column("Reg2"), new double[] { 4, 5, 6 }, Numbers(3, null, 1))
        ]);

        var model = new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels(["Reg1", "Reg2"], "SITE"), Token)!;

        Assert.Equal(InOrder("1", "3", "7", "(Missing)"), model.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
        Assert.Equal(["Reg1 / 3", "Reg1 / 7", "Reg2 / 1", "Reg2 / 3", "Reg2 / (Missing)"], model.Categories);
        Assert.Equal([1, 2, 0, 1, 3], model.Boxes.Select(box => box.SeriesIndex));
    }

    [Fact]
    public void VariablesDrawnTogetherOrderTheirNumericGroupsAcrossEveryVariable()
    {
        var data = new MultiVariableGraphData(GraphType.EmpiricalCdf, Guid.Empty,
        [
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.Empty, Column("Reg1"), new double[] { 1, 2, 3 }, Numbers(7, null, 3)),
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.Empty, Column("Reg2"), new double[] { 4, 5 }, Numbers(1, 7))
        ]);

        var combined = GraphVariablesTogether.Combine(data, Token);
        var model = new EmpiricalCdfRenderModelBuilder().Build(combined, new EmpiricalCdfLabels(combined.Variable.Name, combined.Group!.Column.Name), Token)!;

        Assert.Equal(
            InOrder("Reg1 / 3", "Reg1 / 7", "Reg1 / (Missing)", "Reg2 / 1", "Reg2 / 7"),
            model.Series.Select(series => (series.Label, series.SeriesIndex)));
    }

    // A box plot is not drawn in panels (Task #058).
    public static TheoryData<GraphType> PanelGraphs => [GraphType.ScatterPlot, GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf];

    [Theory]
    [MemberData(nameof(PanelGraphs))]
    public void APanelGraphUsesTheNumericGroupOrderAndColoursInEveryPanel(GraphType type)
    {
        // Site A holds groups 7 and 3, site B only 1, site C 3 and Missing.
        double?[] groups = [7, 3, 1, 3, null, 7, 1];
        string?[] sites = ["A", "A", "B", "C", "C", "A", "B"];
        var values = Values(groups.Length);
        var site = new StringGroupData(Column("Site", WorksheetDataType.String), sites);
        GraphData data = type == GraphType.ScatterPlot
            ? new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), values, values, Numbers(groups)) { Panel = site }
            : new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), values, Numbers(groups)) { Panel = site };

        var prepared = GraphPanelPreparation.Prepare(data, new GraphConfiguration(type, Guid.Empty, []), null, Token)!;

        Assert.Equal(InOrder("1", "3", "7", "(Missing)"), prepared.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
        Assert.Equal(["Site = A", "Site = B", "Site = C"], prepared.Panels.Select(panel => panel.Title));
        Assert.Equal([("3", 1), ("7", 2)], PanelSeries(type, prepared.Panels[0].Plot));
        Assert.Equal([("1", 0)], PanelSeries(type, prepared.Panels[1].Plot));
        Assert.Equal([("3", 1), ("(Missing)", 3)], PanelSeries(type, prepared.Panels[2].Plot));
    }

    private static IReadOnlyList<(string Label, int Index)> PanelSeries(GraphType type, IGraphPlotRenderer? plot)
    {
        var model = plot!.GetType().GetField("_model", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(plot)!;
        return model switch
        {
            ScatterRenderModel scatter => [.. scatter.Series.Select(series => (series.Label, series.SeriesIndex))],
            HistogramRenderModel histogram => [.. histogram.Series.Select(series => (series.Label, series.SeriesIndex))],
            ProbabilityPlotRenderModel probability => [.. probability.Series.Select(series => (series.Label, series.SeriesIndex))],
            EmpiricalCdfRenderModel cdf => [.. cdf.Series.Select(series => (series.Label, series.SeriesIndex))],
            _ => throw new InvalidOperationException(type.ToString())
        };
    }

    [Fact]
    public void TheHelperLeavesTextGroupsAsFoundAndPutsNumbersInOrderWithMissingLast()
    {
        List<string> found = ["7", "(Missing)", "-2", "0.5"];
        Assert.Same(found, GraphGroupOrder.Arrange(found, null, null));

        var byNumber = new Dictionary<double, string> { [7] = "7", [-2] = "-2", [0.5] = "0.5" };
        Assert.Equal(["-2", "0.5", "7", "(Missing)"], GraphGroupOrder.Arrange(found, byNumber, "(Missing)"));
        Assert.Equal([2, 3, 0, 1], GraphGroupOrder.Order(4, new Dictionary<double, int> { [7] = 0, [-2] = 2, [0.5] = 3 }, 1));
        Assert.Equal([0, 1, 2], GraphGroupOrder.Order(3, null, 1));
    }
}
