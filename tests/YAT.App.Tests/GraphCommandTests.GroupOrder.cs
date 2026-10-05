using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// The order a graph's groups are drawn in (Task #059), from the Graph menu over a real project: a numeric group column's
// groups from the smallest value up with "(Missing)" last - after the row filter as before it - and a text column's in
// the order they are first seen. The legend, the colours and the statistics panel follow the same order.
public partial class GraphCommandTests
{
    private const string GroupOrderData =
        "Reg1\tSITE\tLOT\n" +
        "15.02\t5\tB\n14.97\t1\tA\n15.10\t7\t\n15.00\t3\tC\n15.05\t\tB\n14.95\t7\tA\n15.08\t3\tC\n15.12\t5\tB\n";

    private static async Task<GraphPresentationState> DrawGroupedAsync(Runtime runtime, GraphType type, string group, Action<GraphSetupViewModel>? options = null)
    {
        runtime.GraphDialogs.Answer = setup =>
        {
            options?.Invoke(setup);
            return ConfirmWithVariables(setup, ["Reg1"], group);
        };
        await Command(runtime, type).ExecuteAsync(null);
        return runtime.GraphWindows.Graphs[^1];
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public async Task ANumericGroupColumnIsDrawnFromItsSmallestValueUpWithMissingLast(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(GroupOrderData);

        var graph = await DrawGroupedAsync(runtime, type, "SITE");

        // Seen as 5, 1, 7, 3, Missing.
        Assert.Equal([("1", 0), ("3", 1), ("5", 2), ("7", 3), ("(Missing)", 4)], graph.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
        if (graph.Frame.StatisticsPanel is { } panel)
        {
            Assert.Equal(["1", "3", "5", "7", "(Missing)"], panel.Rows.Select(row => row.Label));
            Assert.Equal([0, 1, 2, 3, 4], panel.Rows.Select(row => row.SeriesIndex!.Value));
        }
    }

    [Fact]
    public async Task AFilteredNumericGroupColumnKeepsItsOrder()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(GroupOrderData);

        var graph = await DrawGroupedAsync(runtime, GraphType.Histogram, "SITE", setup =>
        {
            var site = setup.AvailableColumns.Single(option => option.Name == "SITE").WorksheetColumnId!.Value;
            setup.Filter = new RowFilter(new NumericValueSetCondition(site, [7, 3]));
        });

        Assert.Equal([("3", 0), ("7", 1)], graph.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
        Assert.Equal(["3", "7"], graph.Frame.StatisticsPanel!.Rows.Select(row => row.Label));
    }

    [Fact]
    public async Task ATextGroupColumnKeepsTheOrderItsValuesAreFirstSeenIn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(GroupOrderData);

        var graph = await DrawGroupedAsync(runtime, GraphType.EmpiricalCdf, "LOT");

        Assert.Equal(["B", "A", "(Missing)", "C"], graph.Frame.Legend!.Entries.Select(entry => entry.Label));
    }
}
