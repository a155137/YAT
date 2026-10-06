using YAT.App.Tests.Robustness;
using YAT.Application.Graphs;

namespace YAT.App.Tests.Torture;

// Task #063, 2: the largest graph the setup can ask for - many variables, a group column, nine panels and all eight
// statistics, together and separately - laid out and drawn from a window shrunk to almost nothing to a 4K one, and
// exported. The normal suite uses twelve variables; the explicit torture uses fifty, the most the setup takes.
public sealed class MaximumCombinationTests
{
    public const ulong Seed = 0x0630_0002;

    // variables numeric columns, a six-value group column (one value on a single row) and a nine-value panel column.
    internal static TortureDataset Dataset(int variables, int rows = 900)
    {
        var random = new RobustnessRandom(Seed);
        var columns = new List<TortureColumn>();
        for (var index = 0; index < variables; index++)
        {
            var offset = index % 3 == 0 ? 1.5e4 : index % 3 == 1 ? 0.8 : -40;
            var spread = Math.Abs(offset) * Math.Pow(10, -1 - (index % 4));
            columns.Add(TortureDataset.Numeric($"Reg{index + 1}", Enumerable.Range(0, rows).Select(row =>
                row % 17 == 0 ? null : (double?)(offset + (spread * random.NextGaussian())))));
        }

        columns.Add(TortureDataset.Text("Lot", Enumerable.Range(0, rows).Select(row => row == 5 ? "LOT-single" : $"LOT-{row % 5}")));
        columns.Add(TortureDataset.Text("Site", Enumerable.Range(0, rows).Select(row => $"Site {(row % 9) + 1}")));
        return new TortureDataset($"maximum-{variables}", columns);
    }

    public static TheoryData<GraphType, GraphVariableLayout> Cases => new()
    {
        { GraphType.Histogram, GraphVariableLayout.Together },
        { GraphType.Histogram, GraphVariableLayout.Separate },
        { GraphType.ProbabilityPlot, GraphVariableLayout.Together },
        { GraphType.ProbabilityPlot, GraphVariableLayout.Separate },
        { GraphType.EmpiricalCdf, GraphVariableLayout.Together },
        { GraphType.EmpiricalCdf, GraphVariableLayout.Separate },
        { GraphType.BoxPlot, GraphVariableLayout.Together },
        { GraphType.BoxPlot, GraphVariableLayout.Separate }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public Task TwelveVariablesGroupedInNinePanelsWithEveryStatisticDraw(GraphType type, GraphVariableLayout layout) =>
        RunAsync(12, type, layout, windowsToExport: 2);

    [Fact]
    public async Task AScatterPlotGroupedInNinePanelsDraws()
    {
        var dataset = Dataset(2);
        using var session = await TortureSession.StartAsync(dataset);
        var request = new TortureRequest(GraphType.ScatterPlot, ["Reg1"]) { Y = "Reg2", Group = "Lot", Panel = "Site" };
        var outcome = await session.DrawAsync(request);

        Assert.Single(outcome!.Graphs);
        Assert.Equal(9, outcome.Graphs[0].Panels!.Count);
        TortureInvariants.Verify($"seed=0x{Seed:X} named case\n  request: {request}\n  {dataset.Describe()}", outcome, TortureSizes.All);
    }

    [Theory(Explicit = true)]
    [MemberData(nameof(Cases))]
    public Task FiftyVariablesGroupedInNinePanelsWithEveryStatisticDraw(GraphType type, GraphVariableLayout layout) =>
        RunAsync(50, type, layout, windowsToExport: 50);

    private static async Task RunAsync(int variables, GraphType type, GraphVariableLayout layout, int windowsToExport)
    {
        var dataset = Dataset(variables);
        using var session = await TortureSession.StartAsync(dataset);
        var request = new TortureRequest(type, [.. Enumerable.Range(1, variables).Select(index => $"Reg{index}")])
        {
            Group = "Lot",
            Panel = type == GraphType.BoxPlot ? null : "Site",
            Layout = layout,
            Statistics = TortureStatistics.All
        };
        var outcome = await session.DrawAsync(request);
        var context = $"seed=0x{Seed:X} named case\n  request: {request}\n  {dataset.Describe()}";

        // Together: one window (the box plot: one category per variable and lot); separately: one window per variable.
        Assert.NotNull(outcome);
        Assert.Empty(outcome.Errors);
        Assert.Equal(layout == GraphVariableLayout.Together ? 1 : variables, outcome.Graphs.Count);
        if (type != GraphType.BoxPlot)
        {
            Assert.All(outcome.Graphs, graph => Assert.Equal(9, graph.Panels!.Count));
            Assert.All(outcome.Graphs, graph => Assert.Equal(GraphStatisticsOptions.AllItems.Count, graph.State.Frame.StatisticsPanel!.Items.Count));
        }

        TortureInvariants.Verify(context, outcome with { Graphs = outcome.Graphs.Take(windowsToExport).ToList() }, TortureSizes.All);
        TortureInvariants.Verify(context, outcome with { Graphs = outcome.Graphs.Skip(windowsToExport).ToList() }, TortureSizes.All, export: false);
    }
}
