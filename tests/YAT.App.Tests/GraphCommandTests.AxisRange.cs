using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// Axis ranges from the Graph menu (Task #043), over a real project: typed in the setup, they are the ranges the graph
// window opens with - every window of several variables drawn separately alike - and a range that does not fit the
// graph's automatic other end is reported the way a bin width that does not suit the data is, with no window.
public partial class GraphCommandTests
{
    [Fact]
    public async Task TheSetupsRangesAreTheRangesTheWindowOpensWith()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frame = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["Reg1"], null, setup =>
        {
            setup.Axes.XMinimumText = "14.9";
            setup.Axes.YMaximumText = "10";
        }));

        Assert.Equal(14.9, frame.XAxis.Range.Minimum);
        Assert.Equal(new GraphAxisRange(0, 10), frame.YAxis.Range);
        var shown = runtime.GraphWindows.Graphs[^1];
        Assert.Equal(new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, null), new GraphAxisRangeOption(null, 10)), shown.AxisRangeOptions);
        Assert.NotEqual(shown.BaseFrame.XAxis.Range, frame.XAxis.Range);
        Assert.Empty(runtime.GraphDialogs.Errors);
    }

    [Fact]
    public async Task SeparatelyEveryWindowOpensWithTheSameRanges()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.EmpiricalCdf, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot", setup =>
        {
            setup.Axes.YMinimumText = "20";
            setup.Axes.YMaximumText = "80";
        });

        Assert.Equal(2, frames.Count);
        Assert.All(frames, frame => Assert.Equal(new GraphAxisRange(20, 80), frame.YAxis.Range));
        Assert.All(runtime.GraphWindows.Graphs, graph => Assert.Equal(20, graph.AxisRangeOptions.Y.Minimum));
    }

    // Reg1 lies near 15: a minimum of 100 with the maximum left Auto has nothing to show.
    [Fact]
    public async Task ARangeThatDoesNotFitTheGraphIsReportedAndOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.ProbabilityPlot, GraphVariableLayout.Together, ["Reg1"], null, setup =>
            setup.Axes.XMinimumText = "100");

        Assert.Empty(frames);
        var message = Assert.Single(runtime.GraphDialogs.Errors);
        Assert.StartsWith("The X-axis minimum (100) must be below the automatic X-axis maximum (", message);
    }

    // Several variables drawn separately: the ones the range fits open, and the one it does not is reported with them.
    [Fact]
    public async Task SeparatelyOnlyTheGraphsARangeDoesNotFitAreReported()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Separate, ["Reg1", "Reg2"], null, setup =>
            setup.Axes.XMaximumText = "20");

        // Reg1 (near 15) opens; Reg2 (near 30) has nothing below 20 to show.
        var frame = Assert.Single(frames);
        Assert.Equal("Histogram of Reg1", frame.Title);
        var message = Assert.Single(runtime.GraphDialogs.Errors);
        Assert.Contains("Reg2: The X-axis maximum (20) must be above the automatic X-axis minimum (", message);
    }
}
