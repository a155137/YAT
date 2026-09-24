using YAT.Application.Graphs;

namespace YAT.App.Tests;

// The legend from the Graph menu (Task #044), over a real project: chosen in the setup, it is the legend the graph window
// opens with - every window of several variables drawn separately alike - and the series behind it are the same
// whether it is shown, hidden or moved.
public partial class GraphCommandTests
{
    [Fact]
    public async Task TheSetupsLegendIsTheLegendTheWindowOpensWith()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var moved = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot", setup =>
            setup.Legend.SelectedPosition = setup.Legend.PositionChoices.Single(choice => choice.Value == GraphLegendPosition.Top)));
        Assert.Equal(GraphLegendPosition.Top, moved.LegendPosition);
        Assert.NotNull(moved.Legend);
        Assert.Equal(new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Top), runtime.GraphWindows.Graphs[^1].LegendOptions);

        var hidden = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot", setup =>
            setup.Legend.SelectedMode = setup.Legend.ModeChoices.Single(choice => choice.Value == GraphLegendMode.Hide)));
        Assert.Null(hidden.Legend);

        // The same series, in the same order, with the same statistics, whatever the legend.
        Assert.Equal(
            moved.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex, row.Count)),
            hidden.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex, row.Count)));
        Assert.NotNull(runtime.GraphWindows.Graphs[^1].BaseFrame.Legend);
    }

    [Fact]
    public async Task SeparatelyEveryWindowOpensWithTheSameLegend()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.EmpiricalCdf, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot", setup =>
            setup.Legend.SelectedPosition = setup.Legend.PositionChoices.Single(choice => choice.Value == GraphLegendPosition.Left));

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(GraphLegendPosition.Left, frame.LegendPosition));
        Assert.All(runtime.GraphWindows.Graphs, graph => Assert.Equal(GraphLegendPosition.Left, graph.LegendOptions.Position));
    }
}
