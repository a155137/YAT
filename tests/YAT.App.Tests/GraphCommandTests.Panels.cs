using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// A graph drawn in panels from the Graph menu (Task #058), over a real project: the Panel role in the setup, the rows
// read once and filtered first, one window per graph showing every panel under the whole graph's title, axes and legend
// and no statistics panel - several variables drawn together in each panel, or each in a window of its own in panels -
// and a Panel column with too many values refused before anything opens. Without a Panel column nothing changes.
public partial class GraphCommandTests
{
    private const string PanelData =
        "Reg1\tReg2\tLot\tSite\tId\n" +
        "15.02\t30.1\tA\tS2\t1\n14.97\t30.0\tB\tS1\t2\n15.10\t30.4\tA\tS2\t3\n15.00\t29.6\tB\t\t4\n15.05\t30.2\tC\tS3\t5\n" +
        "14.95\t29.9\tA\tS1\t6\n15.08\t30.3\tB\tS3\t7\n15.12\t30.5\tC\tS2\t8\n14.89\t29.5\tA\tS1\t9\n15.01\t30.0\tB\tS3\t10\n";

    private static void Choose(GraphSetupViewModel setup, GraphVariableRole role, string column)
    {
        var target = setup.Roles.Single(candidate => candidate.Role == role);
        target.SelectedOption = target.Options.Single(option => option.Name == column);
    }

    private static async Task<(GraphPresentationState Graph, IReadOnlyList<GraphPanel>? Panels)> DrawInPanelsAsync(
        Runtime runtime,
        GraphType type,
        string panel,
        string[]? variables = null,
        string? group = null,
        Action<GraphSetupViewModel>? options = null,
        GraphVariableLayout layout = GraphVariableLayout.Together)
    {
        runtime.GraphDialogs.Layout = layout;
        runtime.GraphDialogs.Answer = setup =>
        {
            Choose(setup, GraphVariableRole.Panel, panel);
            options?.Invoke(setup);
            if (type == GraphType.ScatterPlot)
            {
                Choose(setup, GraphVariableRole.X, "Reg1");
                Choose(setup, GraphVariableRole.Y, "Reg2");
                if (group is not null)
                {
                    Choose(setup, GraphVariableRole.Group, group);
                }

                return setup.Confirm();
            }

            return ConfirmWithVariables(setup, variables ?? ["Reg1"], group);
        };
        await Command(runtime, type).ExecuteAsync(null);
        return (runtime.GraphWindows.Graphs[^1], runtime.GraphWindows.Panels[^1]);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task APanelColumnOpensOneWindowWithAPanelPerValueInTheOrderFirstSeen(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);

        var (graph, panels) = await DrawInPanelsAsync(runtime, type, "Site", group: "Lot");

        Assert.Single(runtime.GraphWindows.Graphs);
        Assert.Equal(["Site = S2", "Site = S1", "Site = (Missing)", "Site = S3"], panels!.Select(panel => panel.Title));
        Assert.Equal(["A", "B", "C"], graph.Frame.Legend!.Entries.Select(entry => entry.Label));
        Assert.Empty(runtime.GraphDialogs.Errors);

        // Each panel's own statistics (Task #062), a row per panel and group, in panel order then the legend's; a scatter
        // plot has none.
        if (type == GraphType.ScatterPlot)
        {
            Assert.Null(graph.Frame.StatisticsPanel);
            return;
        }

        var statistics = graph.Frame.StatisticsPanel!;
        Assert.Equal("Site / Lot", statistics.GroupHeader);
        Assert.Equal(
            [("Site S2 / A", 0, 2), ("Site S2 / C", 2, 1), ("Site S1 / A", 0, 2), ("Site S1 / B", 1, 1), ("Site (Missing) / B", 1, 1), ("Site S3 / B", 1, 2), ("Site S3 / C", 2, 1)],
            statistics.Rows.Select(row => (row.Label, row.SeriesIndex!.Value, row.Count)));
        Assert.True(graph.Definition.Supports(GraphCapability.StatisticsPanel));
    }

    [Fact]
    public async Task WithoutAPanelColumnTheGraphIsDrawnAsAlways()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, ["Reg1"], "Lot");

        await Command(runtime, GraphType.Histogram).ExecuteAsync(null);

        Assert.Null(runtime.GraphWindows.Panels[^1]);
        Assert.NotNull(runtime.GraphWindows.Graphs[^1].Frame.StatisticsPanel);
        Assert.True(runtime.GraphWindows.Graphs[^1].Definition.Supports(GraphCapability.StatisticsPanel));
    }

    [Fact]
    public async Task TheFilterDecidesFirstAndOneValueLeftIsStillAPanel()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);

        var (_, panels) = await DrawInPanelsAsync(runtime, GraphType.EmpiricalCdf, "Site", options: setup =>
        {
            var site = setup.AvailableColumns.Single(option => option.Name == "Site").WorksheetColumnId!.Value;
            setup.Filter = new RowFilter(new TextValueSetCondition(site, ["S3"]));
        });

        Assert.Equal("Site = S3", Assert.Single(panels!).Title);
    }

    [Fact]
    public async Task MoreThanNinePanelValuesAreRefusedBeforeAnyWindowOpens()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);
        runtime.GraphDialogs.Answer = setup =>
        {
            Choose(setup, GraphVariableRole.Panel, "Id");
            return ConfirmWithVariables(setup, ["Reg1"], null);
        };

        await Command(runtime, GraphType.Histogram).ExecuteAsync(null);

        Assert.Empty(runtime.GraphWindows.Graphs);
        Assert.Equal(GraphPanelSplit.TooManyPanelsMessage("Id"), Assert.Single(runtime.GraphDialogs.Errors));
    }

    [Fact]
    public async Task SeveralVariablesTogetherAreDrawnTogetherInEveryPanel()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);

        var (graph, panels) = await DrawInPanelsAsync(runtime, GraphType.ProbabilityPlot, "Site", ["Reg1", "Reg2"]);

        Assert.Single(runtime.GraphWindows.Graphs);
        Assert.Equal(4, panels!.Count);
        Assert.Equal(["Reg1", "Reg2"], graph.Frame.Legend!.Entries.Select(entry => entry.Label));
        Assert.Equal(GraphVariablesTogether.AxisTitle, graph.Frame.XAxis.Title);
    }

    [Fact]
    public async Task SeveralVariablesSeparatelyOpenAWindowInPanelsForEach()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);

        await DrawInPanelsAsync(runtime, GraphType.Histogram, "Site", ["Reg1", "Reg2"], layout: GraphVariableLayout.Separate);

        Assert.Equal(2, runtime.GraphWindows.Graphs.Count);
        Assert.Equal(["Histogram of Reg1", "Histogram of Reg2"], runtime.GraphWindows.Graphs.Select(graph => graph.Frame.Title));
        Assert.All(runtime.GraphWindows.Panels, panels => Assert.Equal(4, panels!.Count));
        Assert.Equal([0, 1], runtime.GraphWindows.Cascades);
    }

    // Task #062: a graph in panels has statistics again - each panel's own, chosen in the setup as for any graph.
    [Fact]
    public async Task TheSetupsStatisticsApplyToAGraphInPanels()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);

        var (graph, _) = await DrawInPanelsAsync(runtime, GraphType.Histogram, "Site", options: setup =>
        {
            setup.Statistics.ShowStandardDeviation = false;
            setup.Statistics.ShowMedian = true;
        });

        var statistics = graph.Frame.StatisticsPanel!;
        Assert.Equal([GraphStatisticsItem.Mean, GraphStatisticsItem.Count, GraphStatisticsItem.Median], statistics.Items);
        Assert.Equal("Site", statistics.GroupHeader);
        Assert.Equal(["Site S2", "Site S1", "Site (Missing)", "Site S3"], statistics.Rows.Select(row => row.Label));
        Assert.All(statistics.Rows, row => Assert.Null(row.SeriesIndex));

        // Site S2 is rows 1, 3 and 8: 15.02, 15.10, 15.12 - its own numbers, not every panel's.
        var s2 = statistics.Rows[0];
        Assert.Equal(3, s2.Count);
        Assert.Equal("15.1", s2.FiveNumbers!.MedianText);
        Assert.Equal(15.02, s2.FiveNumbers.Minimum);
        Assert.Equal(15.12, s2.FiveNumbers.Maximum);
    }

    [Fact]
    public async Task ThePanelColumnCannotBeTheGroupColumn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(PanelData);
        string? message = null;
        var canConfirm = true;
        runtime.GraphDialogs.Answer = setup =>
        {
            Choose(setup, GraphVariableRole.Panel, "Lot");
            var configuration = ConfirmWithVariables(setup, ["Reg1"], "Lot");
            (message, canConfirm) = (setup.ValidationMessage, setup.CanConfirm);
            return configuration;
        };

        await Command(runtime, GraphType.Histogram).ExecuteAsync(null);

        Assert.False(canConfirm);
        Assert.Equal("Choose a different column for panels than for grouping: each panel is grouped by the group column.", message);
        Assert.Empty(runtime.GraphWindows.Graphs);
    }
}
