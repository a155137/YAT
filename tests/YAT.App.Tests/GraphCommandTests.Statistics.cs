using YAT.Application.Graphs;

namespace YAT.App.Tests;

// The statistics panel from the Graph menu (Task #045), over a real project: chosen in the setup, it is the panel the
// graph window opens with - every window of several variables drawn separately alike - and a panel hidden there was
// still worked out, with the same statistics a shown one has, ready to be shown without the data.
public partial class GraphCommandTests
{
    [Fact]
    public async Task TheSetupsStatisticsAreThePanelTheWindowOpensWith()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var counted = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot", setup =>
        {
            setup.Statistics.ShowMean = false;
            setup.Statistics.ShowStandardDeviation = false;
        }));
        Assert.Equal([GraphStatisticsItem.Count], counted.StatisticsPanel!.Items);
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Auto, false, false, true), runtime.GraphWindows.Graphs[^1].StatisticsOptions);

        var hidden = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot", setup =>
            setup.Statistics.SelectedMode = setup.Statistics.ModeChoices.Single(choice => choice.Value == GraphStatisticsMode.Hide)));
        Assert.Null(hidden.StatisticsPanel);
        var worked = runtime.GraphWindows.Graphs[^1].BaseFrame.StatisticsPanel;
        Assert.NotNull(worked);

        // The same series, in the same order, with the same statistics, whatever the panel shows.
        Assert.Equal(
            counted.StatisticsPanel.Rows.Select(row => (row.Label, row.SeriesIndex, row.Count, row.MeanText, row.StandardDeviationText)),
            worked.Rows.Select(row => (row.Label, row.SeriesIndex, row.Count, row.MeanText, row.StandardDeviationText)));
        Assert.Equal(counted.Legend!.Entries, hidden.Legend!.Entries);
    }

    [Fact]
    public async Task SeparatelyEveryWindowOpensWithTheSameStatistics()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.EmpiricalCdf, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot", setup =>
            setup.Statistics.ShowCount = false);

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal([GraphStatisticsItem.Mean, GraphStatisticsItem.StandardDeviation], frame.StatisticsPanel!.Items));
        Assert.All(runtime.GraphWindows.Graphs, graph => Assert.False(graph.StatisticsOptions.ShowCount));
    }
}
