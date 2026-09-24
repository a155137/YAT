using YAT.Application.Graphs;

namespace YAT.App.Tests;

// The appearance from the Graph menu (Task #046), over a real project: chosen in the setup, it is the appearance the
// graph window opens with - every window of several variables drawn separately alike - and the graph itself, its
// series, legend and statistics, is exactly the graph drawn without it.
public partial class GraphCommandTests
{
    private static readonly GraphAppearanceOptions Styled = new(
        new GraphPalette([new GraphColor(0xD0, 0, 0), new GraphColor(0, 0x80, 0)]),
        GraphGridMode.Hide,
        null,
        new GraphColor(0xF0, 0xE0, 0xD0),
        new GraphColor(0x12, 0x34, 0x56));

    [Fact]
    public async Task TheSetupsAppearanceIsTheAppearanceTheWindowOpensWith()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var plain = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot", _ => { }));
        Assert.Same(GraphAppearanceOptions.Default, runtime.GraphWindows.Graphs[^1].AppearanceOptions);

        var styled = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot", setup =>
            setup.Appearance = Styled));
        Assert.Equal(Styled, runtime.GraphWindows.Graphs[^1].AppearanceOptions);

        // The same graph: its titles, axes, series, legend and statistics.
        Assert.Equal(plain.Title, styled.Title);
        Assert.Equal(plain.XAxis.Range, styled.XAxis.Range);
        Assert.Equal(plain.YAxis.Range, styled.YAxis.Range);
        Assert.Equal(plain.Legend!.Entries, styled.Legend!.Entries);
        Assert.Equal(
            plain.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex, row.CountText, row.MeanText, row.StandardDeviationText)),
            styled.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex, row.CountText, row.MeanText, row.StandardDeviationText)));
    }

    [Fact]
    public async Task SeparatelyEveryWindowOpensWithTheSameAppearance()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.BoxPlot, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot", setup =>
            setup.Appearance = Styled);

        Assert.Equal(2, frames.Count);
        Assert.All(runtime.GraphWindows.Graphs, graph => Assert.Equal(Styled, graph.AppearanceOptions));
    }
}
