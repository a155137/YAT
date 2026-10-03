using YAT.Application.Graphs;
using YAT.App.Tests.TestDoubles;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using YAT.app.Views;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Editing the legend (Task #044): in the setup and on a drawn graph, through the one editor. Confirmed options go onto
// the frame the graph had before its legend, so Auto on the right brings the graph type's legend back exactly; a graph
// without a legend has nothing to edit.
public class GraphLegendEditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 600;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)))];

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    // A grouped (or, with no groups, ungrouped) histogram, presented as the graph preparation presents it.
    private static (GraphPresentationState Graph, HistogramRenderModel Model) Histogram(int groups = 5, GraphLegendOptions? legend = null)
    {
        var group = groups == 0
            ? null
            : new StringGroupData(Column("Lot", WorksheetDataType.String), (string?[])[.. Enumerable.Range(0, Count).Select(i => $"Lot {i % groups}")]);
        var data = new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), Reg1, group);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", group?.Column.Name), Token)!;
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.Empty, [])
        {
            LegendOptions = legend ?? GraphLegendOptions.Default
        };

        return (GraphPresentation.Present(model.Frame, data, configuration, Token), model);
    }

    private sealed class FakeLegendDialog(Func<GraphLegendOptions, GraphLegendOptions?> answer) : IGraphLegendDialog
    {
        public List<GraphLegendOptions> Calls { get; } = [];

        public Task<GraphLegendOptions?> EditAsync(GraphLegendOptions current)
        {
            Calls.Add(current);
            return Task.FromResult(answer(current));
        }
    }

    private static readonly GraphLegendOptions Bottom = new(GraphLegendMode.Auto, GraphLegendPosition.Bottom);

    // ---- The controller ----

    [Fact]
    public async Task ConfirmedOptionsAreShownOverTheSameGraph()
    {
        var (graph, model) = Histogram();
        var dialog = new FakeLegendDialog(_ => Bottom);
        var controller = new GraphLegendEditController(graph, dialog);
        var changed = 0;
        controller.GraphChanged += (_, _) => changed++;

        Assert.True(controller.CanEdit);
        Assert.True(await controller.EditAsync());

        Assert.Equal([GraphLegendOptions.Default], dialog.Calls);
        Assert.Equal(1, changed);
        Assert.Same(graph.BaseFrame, controller.Graph.BaseFrame);
        Assert.Equal(GraphLegendPosition.Bottom, controller.Graph.Frame.LegendPosition);
        Assert.Same(graph.Frame.Legend, controller.Graph.Frame.Legend);
        Assert.Same(graph.Frame.StatisticsPanel, controller.Graph.Frame.StatisticsPanel);
        Assert.Same(graph.Frame.XAxis, controller.Graph.Frame.XAxis);

        // The plot - bins, counts, series and their order - is the builder's; the legend edit never sees it.
        Assert.Equal(Enumerable.Range(0, 5), model.Series.Select(series => series.SeriesIndex));
    }

    [Fact]
    public async Task AutoOnTheRightBringsBackTheGraphTypesLegendItself()
    {
        var (graph, model) = Histogram(legend: new GraphLegendOptions(GraphLegendMode.Hide, GraphLegendPosition.Left));
        Assert.Null(graph.Frame.Legend);
        var controller = new GraphLegendEditController(graph, new FakeLegendDialog(_ => GraphLegendOptions.Default));

        Assert.True(await controller.EditAsync());
        Assert.Same(controller.Graph.BaseFrame, controller.Graph.Frame);

        var (auto, _) = Histogram();
        var service = new GraphExportService();
        Assert.Equal(
            service.RenderPng(new GraphExportSnapshot(auto.Frame, new HistogramRenderer(model), GraphThemes.Light)),
            service.RenderPng(new GraphExportSnapshot(controller.Graph.Frame, new HistogramRenderer(model), GraphThemes.Light)));
    }

    [Fact]
    public async Task ACancelledEditChangesNothing()
    {
        var (graph, _) = Histogram(legend: Bottom);
        var controller = new GraphLegendEditController(graph, new FakeLegendDialog(_ => null));

        Assert.False(await controller.EditAsync());
        Assert.Same(graph, controller.Graph);
    }

    [Fact]
    public async Task OptionsThatAreNotChoicesAreNeverShown()
    {
        var (graph, _) = Histogram();
        var controller = new GraphLegendEditController(graph, new FakeLegendDialog(_ => new GraphLegendOptions((GraphLegendMode)7)));

        Assert.False(await controller.EditAsync());
        Assert.Same(graph, controller.Graph);
    }

    // A graph of one unnamed series has no legend: nothing to edit, and no dialog.
    [Fact]
    public async Task AGraphWithoutALegendHasNothingToEdit()
    {
        var (graph, _) = Histogram(groups: 0);
        var dialog = new FakeLegendDialog(_ => Bottom);
        var controller = new GraphLegendEditController(graph, dialog);

        Assert.False(controller.CanEdit);
        Assert.False(await controller.EditAsync());
        Assert.Empty(dialog.Calls);
    }

    // Labels, ranges and the legend are edited on the one graph: each keeps the others.
    [Fact]
    public async Task TheLegendKeepsTheLabelsAndRangesAndTheyKeepIt()
    {
        var (graph, _) = Histogram();
        var labelled = graph
            .WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Auto, GraphLabelOption.Auto))
            .WithAxisRanges(new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, 15.1), GraphAxisRangeOption.Auto));
        var legend = new GraphLegendEditController(labelled, new FakeLegendDialog(_ => new GraphLegendOptions(GraphLegendMode.Hide)));

        Assert.True(await legend.EditAsync());
        Assert.Equal("Wafer", legend.Graph.Frame.Title);
        Assert.Equal(new GraphAxisRange(14.9, 15.1), legend.Graph.Frame.XAxis.Range);
        Assert.Null(legend.Graph.Frame.Legend);

        var axes = new GraphAxesEditController(graph, new NoAxesDialog());
        axes.Show(legend.Graph);
        Assert.Same(legend.Graph, axes.Graph);
        Assert.Null(axes.Graph.WithAxisRanges(GraphAxisRangeOptions.Default).Frame.Legend);
    }

    private sealed class NoAxesDialog : IGraphAxesDialog
    {
        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame, GraphAxisTickOptions ticks) =>
            Task.FromResult<GraphAxisRangeOptions?>(null);
    }

    [Fact]
    public async Task OneEditAtATime()
    {
        var (graph, _) = Histogram();
        var release = new TaskCompletionSource<GraphLegendOptions?>();
        var dialog = new BlockingDialog(release.Task);
        var controller = new GraphLegendEditController(graph, dialog);

        var first = controller.EditAsync();
        Assert.False(await controller.EditAsync());
        release.SetResult(Bottom);
        Assert.True(await first);
        Assert.Equal(1, dialog.Opened);
    }

    private sealed class BlockingDialog(Task<GraphLegendOptions?> answer) : IGraphLegendDialog
    {
        public int Opened { get; private set; }

        public Task<GraphLegendOptions?> EditAsync(GraphLegendOptions current)
        {
            Opened++;
            return answer;
        }
    }

    [Fact]
    public void EditLegendIsOfferedOnTheRightClickMenu()
    {
        var command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }, () => false);
        var item = GraphWindow.EditLegendItem(command);

        Assert.Equal("Edit Le_gend...", item.Header);
        Assert.Same(command, item.Command);
    }

    // ---- The editor ----

    [Fact]
    public void TheEditorOffersEveryModeAndSideAndKeepsTheSideWhileHidden()
    {
        var legend = new GraphLegendEditorViewModel();

        Assert.Equal(["Auto", "Show", "Hide"], legend.ModeChoices.Select(choice => choice.Name));
        Assert.Equal(["Right", "Left", "Top", "Bottom"], legend.PositionChoices.Select(choice => choice.Name));
        Assert.Equal(GraphLegendOptions.Default, legend.Options);
        Assert.True(legend.IsPositionEnabled);

        legend.SelectedPosition = legend.PositionChoices[3];
        legend.SelectedMode = legend.ModeChoices[2];
        Assert.False(legend.IsPositionEnabled);
        Assert.Equal(new GraphLegendOptions(GraphLegendMode.Hide, GraphLegendPosition.Bottom), legend.Options);

        legend.SelectedMode = legend.ModeChoices[1];
        Assert.True(legend.IsPositionEnabled);
        Assert.Equal(new GraphLegendOptions(GraphLegendMode.Show, GraphLegendPosition.Bottom), legend.Options);
    }

    [Fact]
    public void TheEditorStartsFromTheOptionsItIsGiven()
    {
        var legend = new GraphLegendEditorViewModel(new GraphLegendOptions(GraphLegendMode.Hide, GraphLegendPosition.Top));

        Assert.Equal(GraphLegendMode.Hide, legend.SelectedMode.Value);
        Assert.Equal(GraphLegendPosition.Top, legend.SelectedPosition.Value);
    }

    // ---- The setup ----

    private static readonly Worksheet Sheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static readonly WorksheetColumn Reg1Column = new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = Sheet.Id,
        Name = "Reg1",
        Index = 0,
        DataType = WorksheetDataType.Numeric
    };

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public void TheSetupCarriesTheLegendOptionsEvenWithoutGroups(GraphType type)
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(type), Sheet, [Reg1Column]);
        var variable = setup.Roles.Single(role => role.Role == GraphVariableRole.Variable);
        variable.Choose(variable.Options.Single(option => option.Name == "Reg1"));

        Assert.True(setup.SupportsLegend);
        Assert.Equal(GraphLegendOptions.Default, setup.Confirm()!.LegendOptions);

        // No group is chosen, and the options are still the user's to set: whether there is a legend is the graph's.
        setup.Legend.SelectedMode = setup.Legend.ModeChoices[2];
        setup.Legend.SelectedPosition = setup.Legend.PositionChoices[2];
        Assert.Equal(new GraphLegendOptions(GraphLegendMode.Hide, GraphLegendPosition.Top), setup.Confirm()!.LegendOptions);
    }

    [Fact]
    public void AnInvalidLegendIsExplained()
    {
        var error = new GraphValidationError(GraphValidationReason.LegendOptionsInvalid);

        Assert.Equal("Please choose how the legend is shown and where.",
            GraphValidationMessages.For(error, GraphTypeDefinitions.For(GraphType.Histogram)));
    }
}
