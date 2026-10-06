using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Torture;

// Task #063.2 (defect D5 of #063): a box plot whose category labels are very long - a long group value, a long
// variable name, CJK, emoji - is drawn: its plot, axes, boxes and title keep their room, and only a label too long for
// the graph is ellipsized. Before, the band reserved for the last label reached across the whole canvas, the layout had
// no plot left, and the graph - on screen, copied or exported - was blank.
public sealed class LongLabelBoxPlotTests
{
    private static readonly string Cjk120 = new('長', 120);
    private static readonly string Label40 = new('W', 40);
    private const string Mixed = "😀 émoji 晶圓 ★ Ω 🧪🧪🧪 ünïcödé 測試 " + "😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀😀";
    private static readonly string LongVariable = "Vth 閾值電壓 😀 (mV) " + new string('W', 180) + " 終";

    // A box plot of four rows, one of them in a group of its own named label (the #063 minimal reproduction).
    private static TortureDataset Grouped(string label) =>
        new("long-group", [new TortureColumn("X", ["1", "2", "3", "4"]), new TortureColumn("G", [label, "A", "A", "B"])]);

    private static async Task<TortureGraph> DrawAsync(TortureDataset dataset, TortureRequest request)
    {
        using var session = await TortureSession.StartAsync(dataset);
        var outcome = await session.DrawAsync(request);
        Assert.NotNull(outcome);
        Assert.Empty(outcome.Traces);
        Assert.Empty(outcome.Errors);
        TortureInvariants.Verify($"#063.2 {request}\n  {dataset.Describe()}", outcome, TortureSizes.All);
        return Assert.Single(outcome.Graphs);
    }

    public static TheoryData<string> GroupLabels => new() { Label40, Cjk120, Mixed };

    [Theory]
    [MemberData(nameof(GroupLabels))]
    public async Task ALongGroupValueKeepsThePlotAndIsEllipsizedOnly(string label)
    {
        var graph = await DrawAsync(Grouped(label), new TortureRequest(GraphType.BoxPlot, ["X"]) { Group = "G" });

        AssertDrawn(graph);
    }

    [Fact]
    public async Task ALongVariableNameKeepsThePlot()
    {
        var dataset = new TortureDataset("long-variable", [new TortureColumn(LongVariable, ["1", "2", "3", "4"]), new TortureColumn("Short", ["4", "3", "2", "1"])]);

        AssertDrawn(await DrawAsync(dataset, new TortureRequest(GraphType.BoxPlot, [LongVariable, "Short"])));
        AssertDrawn(await DrawAsync(dataset, new TortureRequest(GraphType.BoxPlot, [LongVariable])));
    }

    // Narrow, normal and large: the plot keeps at least half the content's width wherever a plot fits at all, and the
    // title is drawn.
    private static void AssertDrawn(TortureGraph graph)
    {
        foreach (var size in new SKSize[] { new(360, 260), new(640, 480), new(1600, 1000) })
        {
            var bounds = new SKRect(0, 0, size.Width, size.Height);
            var layout = SkiaGraphRenderer.Layout(graph.State.Frame, bounds, GraphThemes.Light);
            Assert.True(layout.HasPlotArea, $"no plot at {size}");
            Assert.False(layout.TitleArea.IsEmpty, $"no title at {size}");
            var content = size.Width - 24;
            Assert.True(layout.PlotArea.Width + layout.LegendArea.Width + layout.StatisticsPanelArea.Width >= content * 0.5f, $"plot {layout.PlotArea} at {size}");
        }

        // An export is drawn at twice the scale (GraphExportService.ExportScale): 720x520 is the narrow 360x260 graph.
        TortureInvariants.Png("narrow export", TortureInvariants.Export(graph, GraphThemes.Light, 720, 520), 720, 520);
        TortureInvariants.Png("normal export", TortureInvariants.Export(graph, GraphThemes.Light), 1600, 1000);
    }

    // The label is ellipsized to the width a category may take, never measured or drawn longer than it.
    [Fact]
    public async Task ACategoryLabelNeverReachesPastItsShareOfTheWidth()
    {
        var graph = await DrawAsync(Grouped(Cjk120), new TortureRequest(GraphType.BoxPlot, ["X"]) { Group = "G" });

        foreach (var width in new[] { 360f, 640f, 1600f })
        {
            var layout = SkiaGraphRenderer.Layout(graph.State.Frame, new SKRect(0, 0, width, 480), GraphThemes.Light);
            var share = (width - 24) * SkiaGraphRenderer.MaximumCategoryLabelShare;
            Assert.True(layout.Canvas.Right - layout.PlotArea.Right <= (share / 2) + layout.LegendArea.Width + 24 + 12, $"right band at {width}");
        }
    }

    // A short category label is drawn as it always was: nothing to ellipsize, the same layout as any box plot.
    [Fact]
    public async Task AShortCategoryLabelIsNotEllipsized()
    {
        var graph = await DrawAsync(Grouped("LOT-A"), new TortureRequest(GraphType.BoxPlot, ["X"]) { Group = "G" });

        Assert.Equal("LOT-A", SkiaGraphRenderer.Ellipsize("LOT-A", new SKFont { Size = GraphThemes.Light.TickLabelFontSize }, (640 - 24) * SkiaGraphRenderer.MaximumCategoryLabelShare));
        Assert.Contains(graph.State.Frame.XAxis.Ticks, tick => tick.Label.EndsWith("LOT-A", StringComparison.Ordinal));
    }

    // The same graph exported twice, and at the narrow size twice, is the same image: the ellipsis is decided the same way
    // every time it is drawn.
    [Theory]
    [MemberData(nameof(GroupLabels))]
    public async Task ExportingALongLabelGraphAgainGivesTheSameImage(string label)
    {
        var graph = await DrawAsync(Grouped(label), new TortureRequest(GraphType.BoxPlot, ["X"]) { Group = "G" });

        Assert.Equal(TortureInvariants.Export(graph, GraphThemes.Light), TortureInvariants.Export(graph, GraphThemes.Light));
        Assert.Equal(TortureInvariants.Export(graph, GraphThemes.Dark, 720, 520), TortureInvariants.Export(graph, GraphThemes.Dark, 720, 520));
    }
}
