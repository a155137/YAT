using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Edit Box Plot... on a drawn box plot (Task #047): the dialog is shown the options as they are, and a confirmed edit
// is the same model - the very boxes and frame, nothing read, sorted or worked out again - drawn by a new plot renderer.
// The presented graph is not touched, so the box plot options and the labels, axes, legend and appearance keep each
// other, and every copy and export draws what the window shows.
public class GraphBoxPlotEditingTests
{
    private static readonly GraphTypeDefinition BoxPlotDefinition = GraphTypeDefinitions.For(GraphType.BoxPlot);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static readonly double[] Values = [.. Enumerable.Range(0, 60).Select(i => i * i / 60.0), 150, -90];

    private static (MultiVariableGraphData Data, BoxPlotRenderModel Model) Build(BoxPlotOptions? options = null)
    {
        var lot = new StringGroupData(Column("Lot", WorksheetDataType.String), (string?[])[.. Values.Select((_, i) => $"L{i % 2}")]);
        var data = new MultiVariableGraphData(GraphType.BoxPlot, Guid.NewGuid(),
        [
            new UnivariateGraphData(GraphType.BoxPlot, Guid.NewGuid(), Column("Reg1"), Values, lot),
            new UnivariateGraphData(GraphType.BoxPlot, Guid.NewGuid(), Column("Reg2"), Values.Select(value => value + 10).ToArray(), lot)
        ]);
        var model = new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels(["Reg1", "Reg2"], "Lot"), options ?? BoxPlotOptions.Default, Token)!;
        return (data, model);
    }

    private static GraphPresentationState Present(MultiVariableGraphData data, BoxPlotRenderModel model, GraphConfiguration? configuration = null) =>
        GraphPresentation.Present(model.Frame, data, configuration ?? new GraphConfiguration(GraphType.BoxPlot, Guid.Empty, []), Token);

    private sealed class FakeBoxPlotDialog : IGraphBoxPlotDialog
    {
        // What the user confirms each time the dialog is shown, in order; null cancels.
        public List<BoxPlotOptions?> Answers { get; } = [];

        public List<BoxPlotOptions> Shown { get; } = [];

        public TaskCompletionSource<BoxPlotOptions?>? Pending { get; set; }

        public Task<BoxPlotOptions?> EditAsync(BoxPlotOptions current)
        {
            Shown.Add(current);
            return Pending?.Task ?? Task.FromResult(Answers[Shown.Count - 1]);
        }
    }

    private static byte[] Png(GraphRenderModel frame, IGraphPlotRenderer? plot, GraphTheme? theme = null) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, theme ?? GraphThemes.Light));

    // ---- Which graphs can be edited ----

    [Fact]
    public void OnlyABoxPlotDrawnByTheBoxPlotRendererCanBeEdited()
    {
        var (_, model) = Build();
        var dialog = new FakeBoxPlotDialog();

        Assert.True(new GraphBoxPlotEditController(BoxPlotDefinition, new BoxPlotRenderer(model), dialog).CanEdit);
        Assert.False(new GraphBoxPlotEditController(BoxPlotDefinition, null, dialog).CanEdit);
        Assert.False(new GraphBoxPlotEditController(GraphTypeDefinitions.For(GraphType.Histogram), new BoxPlotRenderer(model), dialog).CanEdit);
    }

    [Fact]
    public async Task AGraphWithoutBoxPlotOptionsShowsNoDialog()
    {
        var dialog = new FakeBoxPlotDialog();
        var controller = new GraphBoxPlotEditController(GraphTypeDefinitions.For(GraphType.ScatterPlot), null, dialog);

        Assert.False(await controller.EditAsync());
        Assert.Empty(dialog.Shown);
    }

    // ---- OK and Cancel ----

    [Fact]
    public async Task CancelLeavesTheGraphExactlyAsItWas()
    {
        var (_, model) = Build();
        var plot = new BoxPlotRenderer(model);
        var dialog = new FakeBoxPlotDialog { Answers = { null } };
        var controller = new GraphBoxPlotEditController(BoxPlotDefinition, plot, dialog);
        var changes = 0;
        controller.PlotChanged += (_, _) => changes++;

        Assert.False(await controller.EditAsync());

        Assert.Equal([BoxPlotOptions.Default], dialog.Shown);
        Assert.Same(plot, controller.Plot);
        Assert.Equal(0, changes);
    }

    // Confirmed: the same model with the new options - its very boxes, categories and frame - under a new renderer.
    [Fact]
    public async Task OkDrawsTheSameBoxesWithTheNewOptions()
    {
        var (_, model) = Build();
        var plot = new BoxPlotRenderer(model);
        var edited = new BoxPlotOptions(30, ShowMean: false, ShowOutliers: false);
        var dialog = new FakeBoxPlotDialog { Answers = { edited } };
        var controller = new GraphBoxPlotEditController(BoxPlotDefinition, plot, dialog);
        var changes = 0;
        controller.PlotChanged += (_, _) => changes++;

        Assert.True(await controller.EditAsync());

        var now = Assert.IsType<BoxPlotRenderer>(controller.Plot);
        Assert.NotSame(plot, now);
        Assert.Equal(edited, now.Model.Options);
        Assert.Same(model.Boxes, now.Model.Boxes);
        Assert.Same(model.Categories, now.Model.Categories);
        Assert.Same(model.Frame, now.Model.Frame);
        Assert.Equal(1, changes);

        // The old renderer is untouched: it still draws the default.
        Assert.Equal(BoxPlotOptions.Default, plot.Model.Options);
    }

    [Fact]
    public async Task OptionsTheRuleRefusesAreNotTaken()
    {
        var (_, model) = Build();
        var plot = new BoxPlotRenderer(model);
        var controller = new GraphBoxPlotEditController(BoxPlotDefinition, plot, new FakeBoxPlotDialog { Answers = { new BoxPlotOptions(95) } });

        Assert.False(await controller.EditAsync());
        Assert.Same(plot, controller.Plot);
    }

    // Each edit starts from the last one; back at the default, the graph is the graph as it was drawn, to the pixel.
    [Fact]
    public async Task RepeatedEditsStartFromTheLastAndComeBackToTheOriginal()
    {
        var (data, model) = Build();
        var state = Present(data, model);
        var plot = new BoxPlotRenderer(model);
        var original = Png(state.Frame, plot);
        var answers = new[] { new BoxPlotOptions(20), new BoxPlotOptions(90, ShowMean: false), new BoxPlotOptions(45, true, false), BoxPlotOptions.Default };
        var dialog = new FakeBoxPlotDialog();
        foreach (var answer in answers)
        {
            dialog.Answers.Add(answer);
        }

        var controller = new GraphBoxPlotEditController(BoxPlotDefinition, plot, dialog);
        var drawn = new List<byte[]>();
        foreach (var _ in answers)
        {
            Assert.True(await controller.EditAsync());
            Assert.Same(model.Boxes, ((BoxPlotRenderer)controller.Plot!).Model.Boxes);
            drawn.Add(Png(state.Frame, controller.Plot));
        }

        Assert.Equal([BoxPlotOptions.Default, .. answers[..^1]], dialog.Shown);
        Assert.Equal(drawn.Count, drawn.Select(Convert.ToHexString).Distinct().Count());
        Assert.Equal(original, drawn[^1]);
    }

    // One edit at a time: while the dialog is open, a second request is not a second dialog.
    [Fact]
    public async Task ASecondEditWhileTheDialogIsOpenDoesNothing()
    {
        var (_, model) = Build();
        var dialog = new FakeBoxPlotDialog { Pending = new TaskCompletionSource<BoxPlotOptions?>() };
        var controller = new GraphBoxPlotEditController(BoxPlotDefinition, new BoxPlotRenderer(model), dialog);

        var first = controller.EditAsync();
        Assert.False(await controller.EditAsync());
        Assert.Single(dialog.Shown);

        dialog.Pending.SetResult(new BoxPlotOptions(40));
        Assert.True(await first);
        Assert.Equal(40, ((BoxPlotRenderer)controller.Plot!).Model.Options.BoxWidthPercent);
    }

    // ---- With the other editors ----

    private sealed class LabelsDialog(GraphLabelOptions options) : IGraphLabelsDialog
    {
        public Task<GraphLabelOptions?> EditAsync(GraphTypeDefinition definition, GraphLabelOptions current, GraphLabelField? focus, string? shownText) =>
            Task.FromResult<GraphLabelOptions?>(options);
    }

    private sealed class AxesDialog(GraphAxisRangeOptions options) : IGraphAxesDialog
    {
        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame, GraphAxisTickOptions ticks) =>
            Task.FromResult<GraphAxisRangeOptions?>(options);
    }

    private sealed class LegendDialog(GraphLegendOptions options) : IGraphLegendDialog
    {
        public Task<GraphLegendOptions?> EditAsync(GraphLegendOptions current) => Task.FromResult<GraphLegendOptions?>(options);
    }

    private sealed class AppearanceDialog(GraphAppearanceOptions options) : IGraphAppearanceDialog
    {
        public Task<GraphAppearanceOptions?> EditAsync(GraphTypeDefinition definition, GraphAppearanceOptions current) =>
            Task.FromResult<GraphAppearanceOptions?>(options);
    }

    private static readonly GraphLabelOptions Labels =
        new(GraphLabelOption.Custom("晶圓 Wafer"), GraphLabelOption.Hidden, GraphLabelOption.Custom("厚度"));

    private static readonly GraphAxisRangeOptions Axes = new(GraphAxisRangeOption.Auto, new GraphAxisRangeOption(-120, 200));

    private static readonly GraphLegendOptions Legend = new(GraphLegendMode.Auto, GraphLegendPosition.Bottom);

    private static readonly GraphAppearanceOptions Appearance =
        new(new GraphPalette([new GraphColor(0xD0, 0, 0), new GraphColor(0, 0x90, 0)]), GraphGridMode.Hide, PlotBackground: new GraphColor(0xF0, 0xE0, 0xD0));

    private static readonly BoxPlotOptions Edited = new(35, ShowMean: false, ShowOutliers: false);

    // The graph window's editors, as the window wires them: each presentation editor shows the graph the others last
    // made, and the box plot editor keeps the plot. Edited in either order, the graph drawn is the graph a setup with
    // all of those options would have drawn - to the pixel.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheBoxPlotOptionsAndTheOtherEditorsKeepEachOther(bool boxPlotFirst)
    {
        var (data, model) = Build();
        var state = Present(data, model);
        var labels = new GraphLabelEditController(state, new LabelsDialog(Labels));
        var axes = new GraphAxesEditController(state, new AxesDialog(Axes));
        var legend = new GraphLegendEditController(state, new LegendDialog(Legend));
        var appearance = new GraphAppearanceEditController(state, new AppearanceDialog(Appearance));
        var boxPlot = new GraphBoxPlotEditController(BoxPlotDefinition, new BoxPlotRenderer(model), new FakeBoxPlotDialog { Answers = { Edited } });
        var graph = state;
        void Show(GraphPresentationState changed)
        {
            graph = changed;
            labels.Show(changed);
            axes.Show(changed);
            legend.Show(changed);
            appearance.Show(changed);
        }

        labels.GraphChanged += (_, _) => Show(labels.Graph);
        axes.GraphChanged += (_, _) => Show(axes.Graph);
        legend.GraphChanged += (_, _) => Show(legend.Graph);
        appearance.GraphChanged += (_, _) => Show(appearance.Graph);

        if (boxPlotFirst)
        {
            Assert.True(await boxPlot.EditAsync());
        }

        var beforeOthers = graph;
        Assert.True(await labels.EditAsync(null));
        Assert.True(await axes.EditAsync());
        Assert.True(await legend.EditAsync());
        Assert.True(await appearance.EditAsync());
        Assert.NotSame(beforeOthers, graph);

        if (!boxPlotFirst)
        {
            var presented = graph;
            Assert.True(await boxPlot.EditAsync());

            // The box plot edit does not touch the presented graph.
            Assert.Same(presented, graph);
        }

        Assert.Equal(Edited, ((BoxPlotRenderer)boxPlot.Plot!).Model.Options);
        Assert.Equal(Labels, graph.LabelOptions);
        Assert.Equal(Axes, graph.AxisRangeOptions);
        Assert.Equal(Legend, graph.LegendOptions);
        Assert.Equal(Appearance, graph.AppearanceOptions);

        // What a setup with every one of those options draws.
        var (freshData, fresh) = Build(Edited);
        var configured = Present(freshData, fresh, new GraphConfiguration(GraphType.BoxPlot, Guid.Empty, [])
        {
            LabelOptions = Labels,
            AxisRangeOptions = Axes,
            LegendOptions = Legend,
            AppearanceOptions = Appearance
        });
        var theme = GraphAppearance.Resolve(GraphThemes.Light, Appearance);
        Assert.Equal(Png(configured.Frame, new BoxPlotRenderer(fresh), theme), Png(graph.Frame, boxPlot.Plot, theme));
    }

    // ---- Copy and export ----

    // Copy Image, Export PNG and Export PowerPoint of the edited graph all draw the graph with its new options.
    [Fact]
    public async Task CopiesAndExportsDrawTheEditedPlot()
    {
        var (data, model) = Build();
        var state = Present(data, model);
        var boxPlot = new GraphBoxPlotEditController(BoxPlotDefinition, new BoxPlotRenderer(model), new FakeBoxPlotDialog { Answers = { Edited } });
        var before = Png(state.Frame, boxPlot.Plot);
        Assert.True(await boxPlot.EditAsync());

        // What the window's Snapshot() gives after the edit: its frame and its current plot.
        var snapshot = new GraphExportSnapshot(state.Frame, boxPlot.Plot, GraphThemes.Dark);
        using var directory = new TemporaryDirectory();
        var dialogs = new FakeGraphExportDialogs { PowerPointPath = directory.File("graph.pptx"), PngPath = directory.File("graph.png") };
        var powerPoint = new FakePowerPointGraphExporter();
        var clipboard = new FakeGraphImageClipboard();
        var export = new GraphExportController(dialogs, new GraphExportService(), powerPoint, clipboard);

        await export.ExportPngAsync(snapshot, Token);
        await export.ExportPowerPointAsync(snapshot, Token);
        await export.CopyImageAsync(snapshot, Token);

        var png = File.ReadAllBytes(dialogs.PngPath!);
        Assert.Equal(Png(state.Frame, boxPlot.Plot, GraphThemes.Dark), png);
        Assert.Equal(png, powerPoint.Last.Image.Png);
        Assert.Equal(png, clipboard.Copied.Single());
        Assert.NotEqual(Png(state.Frame, new BoxPlotRenderer(model), GraphThemes.Dark), png);
        Assert.NotEqual(before, Png(state.Frame, boxPlot.Plot));

        using var image = SKBitmap.Decode(png);
        Assert.Equal(GraphExportService.ExportWidth, image.Width);
    }
}
