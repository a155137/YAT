using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Torture;

// Task #063, Phase 2: determinism. The same input gives the same image, and every way of leaving a graph's original
// state and coming back - a resize and back, a zoom and pan undone by Reset View, options changed and changed back -
// gives back the very image it started from. The image is the fingerprint: an export at a fixed size draws the frame,
// every panel and the plot, so anything that drifts shows in its bytes.
//
// The graphs are representative of every shape a graph takes: panels with groups, groups, fifty variables together and
// separately, a box plot and a scatter plot. Fixed data (MaximumCombinationTests' seeded dataset), fixed sizes, no time
// and no randomness.
public sealed class DeterminismTests
{
    // The plot area the view controller navigates over, as a window would report it.
    private static readonly SKRect Plot = new(80, 40, 560, 400);
    private static readonly SKRect OtherPlot = new(40, 20, 1500, 900);

    public enum Shape
    {
        HistogramGroupedInPanels,
        ProbabilityPlotInPanels,
        EmpiricalCdfOfFiftyVariables,
        HistogramOfFiftyVariablesSeparately,
        GroupedBoxPlot,
        GroupedScatterPlot
    }

    public static TheoryData<Shape> Shapes => [.. Enum.GetValues<Shape>()];

    private static readonly TortureDataset Data = MaximumCombinationTests.Dataset(50, rows: 180);

    private static TortureRequest Request(Shape shape) => shape switch
    {
        Shape.HistogramGroupedInPanels => new(GraphType.Histogram, ["Reg1"]) { Group = "Lot", Panel = "Site" },
        Shape.ProbabilityPlotInPanels => new(GraphType.ProbabilityPlot, ["Reg2", "Reg5"]) { Panel = "Site" },
        Shape.EmpiricalCdfOfFiftyVariables => new(GraphType.EmpiricalCdf, [.. Enumerable.Range(1, 50).Select(index => $"Reg{index}")]),
        Shape.HistogramOfFiftyVariablesSeparately =>
            new(GraphType.Histogram, [.. Enumerable.Range(1, 50).Select(index => $"Reg{index}")]) { Layout = GraphVariableLayout.Separate },
        Shape.GroupedBoxPlot => new(GraphType.BoxPlot, ["Reg1", "Reg2", "Reg3"]) { Group = "Lot" },
        _ => new(GraphType.ScatterPlot, ["Reg1"]) { Y = "Reg4", Group = "Lot" }
    };

    private static async Task<IReadOnlyList<TortureGraph>> DrawAsync(Shape shape)
    {
        using var session = await TortureSession.StartAsync(Data);
        var outcome = await session.DrawAsync(Request(shape));
        Assert.NotNull(outcome);
        Assert.Empty(outcome.Traces);
        Assert.Empty(outcome.Errors);
        Assert.NotEmpty(outcome.Graphs);
        return outcome.Graphs;
    }

    private static byte[] Png(TortureGraph graph, GraphPresentationState state, GraphTheme? theme = null) =>
        TortureInvariants.Export(graph with { State = state }, theme ?? GraphThemes.Light, 800, 500);

    private static void Same(byte[] expected, byte[] actual, Shape shape, string what) =>
        Assert.True(expected.AsSpan().SequenceEqual(actual), $"{shape}: {what} did not give back the same image");

    // ---- The same input ----

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task TheSameGraphExportedTwiceIsTheSameImage(Shape shape)
    {
        var graph = (await DrawAsync(shape))[0];

        Same(Png(graph, graph.State), Png(graph, graph.State), shape, "exporting again");
        Same(Png(graph, graph.State, GraphThemes.Dark), Png(graph, graph.State, GraphThemes.Dark), shape, "exporting again in the dark theme");
    }

    // The same data pasted into another project and drawn again: the whole pipeline - paste, storage, read, preparation -
    // gives the same image, window by window.
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task TheSameDataDrawnAgainIsTheSameImage(Shape shape)
    {
        var first = await DrawAsync(shape);
        var second = await DrawAsync(shape);

        Assert.Equal(first.Count, second.Count);
        foreach (var (a, b) in first.Zip(second).Take(5))
        {
            Same(Png(a, a.State), Png(b, b.State), shape, "drawing the same data again");
        }
    }

    // ---- Leaving the original state and coming back ----

    // A window resized and resized back: drawn at its original size it is the original image. A zoomed graph's ticks
    // depend on the pixels its plot spans, so the view controller is resized too.
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task AResizeAndBackGivesTheSameImage(Shape shape)
    {
        var graph = (await DrawAsync(shape))[0];
        var original = TortureInvariants.Export(graph, GraphThemes.Light, 800, 500);
        foreach (var size in new[] { (240, 160), (1920, 1080), (520, 900) })
        {
            TortureInvariants.Export(graph, GraphThemes.Light, size.Item1, size.Item2);
        }

        Same(original, TortureInvariants.Export(graph, GraphThemes.Light, 800, 500), shape, "a resize and back");

        var controller = new GraphViewController(graph.State);
        controller.ZoomAt(null, Plot, new SKPoint(300, 220), 2);
        var zoomed = Png(graph, controller.Graph);
        controller.Resize(OtherPlot);
        controller.Resize(new SKRect(10, 10, 200, 120));
        controller.Resize(Plot);
        Same(zoomed, Png(graph, controller.Graph), shape, "a zoomed graph resized and back");
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task ZoomAndPanUndoneByResetViewGivesTheSameImage(Shape shape)
    {
        var graph = (await DrawAsync(shape))[0];
        var original = Png(graph, graph.State);
        var controller = new GraphViewController(graph.State);

        controller.ZoomAt(null, Plot, new SKPoint(260, 140), 3);
        controller.ZoomAt(GraphAxisField.X, Plot, new SKPoint(400, 380), -1);
        controller.BeginPan(Plot, new SKPoint(300, 200));
        controller.PanTo(new SKPoint(340, 230));
        controller.PanTo(new SKPoint(120, 90));
        controller.EndPan();
        controller.ZoomAt(GraphAxisField.Y, Plot, new SKPoint(90, 200), 5);
        controller.Reset();

        Assert.True(controller.Graph.ViewOptions.IsDefault, $"{shape}: Reset View left a view");
        Assert.False(controller.CanReset);
        Same(original, Png(graph, controller.Graph), shape, "zoom and pan, then Reset View");
    }

    // Every option a drawn graph's dialogs edit, changed and changed back to what the graph opened with.
    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task OptionsChangedAndChangedBackGiveTheSameImage(Shape shape)
    {
        var graph = (await DrawAsync(shape))[0];
        var start = graph.State;
        var original = Png(graph, start);

        var edited = start
            .WithLabels(new GraphLabelOptions(
                new GraphLabelOption(GraphLabelMode.Custom, "晶圓 😀 custom title"),
                new GraphLabelOption(GraphLabelMode.Hidden),
                new GraphLabelOption(GraphLabelMode.Custom, "Y")))
            .WithLegend(new GraphLegendOptions(GraphLegendMode.Show, GraphLegendPosition.Bottom))
            .WithAxisRanges(Narrowed(start));
        if (start.Definition.Supports(GraphCapability.StatisticsPanel))
        {
            edited = edited.WithStatistics(new GraphStatisticsOptions(GraphStatisticsMode.Show, false, true, false, true, true, true, true, true));
        }

        Assert.False(Png(graph, edited).AsSpan().SequenceEqual(original), $"{shape}: the edits changed nothing, so the test proves nothing");

        var restored = edited
            .WithLabels(start.LabelOptions)
            .WithLegend(start.LegendOptions)
            .WithAxisRanges(start.AxisRangeOptions)
            .WithStatistics(start.StatisticsOptions);
        Same(original, Png(graph, restored), shape, "options changed and changed back");

        // And back to the defaults: the graph opened with them.
        var defaults = edited
            .WithLabels(GraphLabelOptions.Default)
            .WithLegend(GraphLegendOptions.Default)
            .WithAxisRanges(GraphAxisRangeOptions.Default)
            .WithStatistics(GraphStatisticsOptions.Default);
        Same(original, Png(graph, defaults), shape, "options restored to their defaults");
    }

    // A narrower range on the value axis: the X axis of every graph type but the box plot (whose X axis is its
    // categories), whose value axis is Y. Inside the automatic range, so no rule refuses it.
    private static GraphAxisRangeOptions Narrowed(GraphPresentationState state)
    {
        var axis = state.Definition.GraphType == GraphType.BoxPlot ? state.Frame.YAxis : state.Frame.XAxis;
        var span = axis.Range.Maximum - axis.Range.Minimum;
        var narrowed = new GraphAxisRangeOption(axis.Range.Minimum + (span * 0.2), axis.Range.Maximum - (span * 0.2));
        return state.Definition.GraphType == GraphType.BoxPlot
            ? GraphAxisRangeOptions.Default with { Y = narrowed }
            : GraphAxisRangeOptions.Default with { X = narrowed };
    }
}
