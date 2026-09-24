using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The box plot options from the Graph menu (Task #047), over a real project: chosen in the setup, they are the options
// the graph window's plot is drawn with - every window of several variables drawn separately alike - and the boxes
// themselves are exactly the boxes drawn without them. Once open, each window's options are its own.
public partial class GraphCommandTests
{
    private static readonly BoxPlotOptions NarrowPlain = new(30, ShowMean: false, ShowOutliers: false);

    private static BoxPlotRenderModel BoxPlotModel(IGraphPlotRenderer? plot) => Assert.IsType<BoxPlotRenderer>(plot).Model;

    private static IEnumerable<(double, double, double, double, double, double, int)> Boxes(BoxPlotRenderModel model) =>
        model.Boxes.Select(box => (box.LowerWhisker, box.FirstQuartile, box.Median, box.ThirdQuartile, box.UpperWhisker, box.Mean, box.OutlierCount));

    [Fact]
    public async Task TheSetupsBoxPlotOptionsAreTheOptionsTheWindowDrawsWith()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var plain = Assert.Single(await DrawAsync(runtime, GraphType.BoxPlot, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot"));
        var plainModel = BoxPlotModel(runtime.GraphWindows.Last.Plot);
        Assert.Equal(BoxPlotOptions.Default, plainModel.Options);
        Assert.Equal(BoxPlotOptions.Default, runtime.Graphs.LastConfiguration!.BoxPlotOptions);

        var chosen = Assert.Single(await DrawAsync(runtime, GraphType.BoxPlot, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot", setup =>
            setup.BoxPlot = NarrowPlain));
        var chosenModel = BoxPlotModel(runtime.GraphWindows.Last.Plot);
        Assert.Equal(NarrowPlain, chosenModel.Options);
        Assert.Equal(NarrowPlain, runtime.Graphs.LastConfiguration!.BoxPlotOptions);

        // The same graph: its boxes, categories, axes and legend.
        Assert.Equal(Boxes(plainModel), Boxes(chosenModel));
        Assert.Equal(plainModel.Categories, chosenModel.Categories);
        Assert.Equal(plain.Title, chosen.Title);
        Assert.Equal(plain.XAxis.Range, chosen.XAxis.Range);
        Assert.Equal(plain.YAxis.Range, chosen.YAxis.Range);
        Assert.Equal(plain.YAxis.Ticks, chosen.YAxis.Ticks);
        Assert.Equal(plain.Legend!.Entries, chosen.Legend!.Entries);
    }

    // Separately, every window opens with the setup's options; after that, each one's options are its own.
    [Fact]
    public async Task SeparatelyEveryWindowOpensWithTheOptionsAndEditsItsOwn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.BoxPlot, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot", setup =>
            setup.BoxPlot = NarrowPlain);

        Assert.Equal(2, frames.Count);
        var plots = runtime.GraphWindows.Shown.Select(shown => shown.Plot).ToList();
        Assert.All(plots, plot => Assert.Equal(NarrowPlain, BoxPlotModel(plot).Options));

        // Each window's editor, as each window makes one for its own plot.
        var definition = GraphTypeDefinitions.For(GraphType.BoxPlot);
        var first = new GraphBoxPlotEditController(definition, plots[0], new OneAnswer(new BoxPlotOptions(85)));
        var second = new GraphBoxPlotEditController(definition, plots[1], new OneAnswer(null));

        Assert.True(await first.EditAsync());
        Assert.False(await second.EditAsync());

        Assert.Equal(new BoxPlotOptions(85), BoxPlotModel(first.Plot).Options);
        Assert.Same(plots[1], second.Plot);
        Assert.Equal(NarrowPlain, BoxPlotModel(second.Plot).Options);
    }

    private sealed class OneAnswer(BoxPlotOptions? answer) : IGraphBoxPlotDialog
    {
        public Task<BoxPlotOptions?> EditAsync(BoxPlotOptions current) => Task.FromResult(answer);
    }
}
