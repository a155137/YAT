using Avalonia.Controls;
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

// Editing the statistics panel (Task #045): in the setup and on a drawn graph, through the one editor. Confirmed options
// go onto the frame the graph had before its statistics, whose panel was worked out whole with the graph - so a panel
// hidden from the start can be shown without the data, and the default brings the panel back exactly. A graph without
// a panel has nothing to edit.
[Collection(GraphMenuItemCollection.Name)]
public class GraphStatisticsEditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 600;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)))];

    private static readonly GraphTypeDefinition HistogramDefinition = GraphTypeDefinitions.For(GraphType.Histogram);

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    // A grouped (or, with no groups, ungrouped) histogram, presented as the graph preparation presents it.
    private static (GraphPresentationState Graph, HistogramRenderModel Model) Histogram(int groups = 5, GraphStatisticsOptions? statistics = null)
    {
        var group = groups == 0
            ? null
            : new StringGroupData(Column("Lot", WorksheetDataType.String), (string?[])[.. Enumerable.Range(0, Count).Select(i => $"Lot {i % groups}")]);
        var data = new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), Reg1, group);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", group?.Column.Name), Token)!;
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.Empty, [])
        {
            StatisticsOptions = statistics ?? GraphStatisticsOptions.Default
        };

        return (GraphPresentation.Present(model.Frame, data, configuration, Token), model);
    }

    private static byte[] Png(GraphRenderModel frame, HistogramRenderModel model) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, new HistogramRenderer(model), GraphThemes.Light));

    private sealed class FakeStatisticsDialog(Func<GraphStatisticsOptions, GraphStatisticsOptions?> answer) : IGraphStatisticsDialog
    {
        public List<(GraphTypeDefinition Definition, GraphStatisticsOptions Current)> Calls { get; } = [];

        public Task<GraphStatisticsOptions?> EditAsync(GraphTypeDefinition definition, GraphStatisticsOptions current)
        {
            Calls.Add((definition, current));
            return Task.FromResult(answer(current));
        }
    }

    private static readonly GraphStatisticsOptions Hide = new(GraphStatisticsMode.Hide);

    private static readonly GraphStatisticsOptions CountOnly = new(GraphStatisticsMode.Auto, false, false, true);

    // ---- The controller ----

    [Fact]
    public async Task ConfirmedOptionsAreShownOverTheSameGraph()
    {
        var (graph, model) = Histogram();
        var dialog = new FakeStatisticsDialog(_ => CountOnly);
        var controller = new GraphStatisticsEditController(graph, dialog);
        var changed = 0;
        controller.GraphChanged += (_, _) => changed++;

        Assert.True(controller.CanEdit);
        Assert.True(await controller.EditAsync());

        var call = Assert.Single(dialog.Calls);
        Assert.Same(HistogramDefinition, call.Definition);
        Assert.Equal(GraphStatisticsOptions.Default, call.Current);
        Assert.Equal(1, changed);
        Assert.Same(graph.BaseFrame, controller.Graph.BaseFrame);
        Assert.Equal(CountOnly, controller.Graph.StatisticsOptions);
        Assert.Equal([GraphStatisticsItem.Count], controller.Graph.Frame.StatisticsPanel!.Items);
        Assert.Equal(graph.Frame.StatisticsPanel!.Rows, controller.Graph.Frame.StatisticsPanel.Rows);
        Assert.Same(graph.Frame.Legend, controller.Graph.Frame.Legend);
        Assert.Same(graph.Frame.XAxis, controller.Graph.Frame.XAxis);

        // The plot - bins, counts, series and their order - is the builder's; the statistics edit never sees it.
        Assert.Equal(Enumerable.Range(0, 5), model.Series.Select(series => series.SeriesIndex));
    }

    // A panel hidden in the setup is shown afterwards from the panel worked out with the graph: no data is needed.
    [Fact]
    public async Task APanelHiddenFromTheStartIsShownWithoutTheData()
    {
        var (graph, model) = Histogram(statistics: Hide);
        Assert.Null(graph.Frame.StatisticsPanel);
        Assert.NotNull(graph.BaseFrame.StatisticsPanel);
        var controller = new GraphStatisticsEditController(graph, new FakeStatisticsDialog(_ => GraphStatisticsOptions.Default));

        Assert.True(controller.CanEdit);
        Assert.True(await controller.EditAsync());
        Assert.Same(controller.Graph.BaseFrame, controller.Graph.Frame);

        var (shown, _) = Histogram();
        Assert.Equal(Png(shown.Frame, model), Png(controller.Graph.Frame, model));
    }

    [Fact]
    public async Task HideTakesThePanelAwayAndTheDefaultBringsItBack()
    {
        var (graph, model) = Histogram();
        var first = Png(graph.Frame, model);
        var answers = new Queue<GraphStatisticsOptions>([Hide, CountOnly, Hide, GraphStatisticsOptions.Default]);
        var controller = new GraphStatisticsEditController(graph, new FakeStatisticsDialog(_ => answers.Dequeue()));

        Assert.True(await controller.EditAsync());
        Assert.Null(controller.Graph.Frame.StatisticsPanel);
        Assert.True(await controller.EditAsync());
        Assert.Equal([GraphStatisticsItem.Count], controller.Graph.Frame.StatisticsPanel!.Items);
        Assert.True(await controller.EditAsync());
        Assert.True(await controller.EditAsync());

        Assert.Same(graph.BaseFrame, controller.Graph.Frame);
        Assert.Equal(first, Png(controller.Graph.Frame, model));
    }

    [Fact]
    public async Task ACancelledEditChangesNothing()
    {
        var (graph, _) = Histogram(statistics: CountOnly);
        var controller = new GraphStatisticsEditController(graph, new FakeStatisticsDialog(_ => null));

        Assert.False(await controller.EditAsync());
        Assert.Same(graph, controller.Graph);
    }

    [Fact]
    public async Task OptionsThatAreNotValidAreNeverShown()
    {
        var (graph, _) = Histogram();

        foreach (var invalid in new[]
        {
            new GraphStatisticsOptions((GraphStatisticsMode)7),
            new GraphStatisticsOptions(GraphStatisticsMode.Auto, false, false, false),
            new GraphStatisticsOptions(GraphStatisticsMode.Show, false, false, false)
        })
        {
            var controller = new GraphStatisticsEditController(graph, new FakeStatisticsDialog(_ => invalid));
            Assert.False(await controller.EditAsync());
            Assert.Same(graph, controller.Graph);
        }
    }

    // A graph type without a panel - or a graph none was worked out for - has nothing to edit, and no dialog opens.
    [Fact]
    public async Task AGraphWithoutAPanelHasNothingToEdit()
    {
        var (histogram, _) = Histogram();
        var withoutPanel = new GraphPresentationState(histogram.BaseFrame.WithStatisticsPanel(null), HistogramDefinition, GraphLabelOptions.Default);
        var scatter = new GraphPresentationState(histogram.BaseFrame, GraphTypeDefinitions.For(GraphType.ScatterPlot), GraphLabelOptions.Default);

        foreach (var graph in new[] { withoutPanel, scatter })
        {
            var dialog = new FakeStatisticsDialog(_ => Hide);
            var controller = new GraphStatisticsEditController(graph, dialog);

            Assert.False(controller.CanEdit);
            Assert.False(await controller.EditAsync());
            Assert.Empty(dialog.Calls);
        }
    }

    // Labels, ranges, the legend and the statistics are edited on the one graph: each keeps the others.
    [Fact]
    public async Task TheStatisticsKeepTheLabelsRangesAndLegendAndTheyKeepThem()
    {
        var (graph, _) = Histogram();
        var edited = graph
            .WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Auto, GraphLabelOption.Auto))
            .WithAxisRanges(new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, 15.1), GraphAxisRangeOption.Auto))
            .WithLegend(new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Left));
        var statistics = new GraphStatisticsEditController(edited, new FakeStatisticsDialog(_ => Hide));

        Assert.True(await statistics.EditAsync());
        Assert.Equal("Wafer", statistics.Graph.Frame.Title);
        Assert.Equal(new GraphAxisRange(14.9, 15.1), statistics.Graph.Frame.XAxis.Range);
        Assert.Equal(GraphLegendPosition.Left, statistics.Graph.Frame.LegendPosition);
        Assert.Null(statistics.Graph.Frame.StatisticsPanel);

        var legend = new GraphLegendEditController(graph, new NoLegendDialog());
        legend.Show(statistics.Graph);
        Assert.Same(statistics.Graph, legend.Graph);
        Assert.Null(legend.Graph.WithLegend(GraphLegendOptions.Default).Frame.StatisticsPanel);
    }

    private sealed class NoLegendDialog : IGraphLegendDialog
    {
        public Task<GraphLegendOptions?> EditAsync(GraphLegendOptions current) => Task.FromResult<GraphLegendOptions?>(null);
    }

    [Fact]
    public async Task OneEditAtATime()
    {
        var (graph, _) = Histogram();
        var release = new TaskCompletionSource<GraphStatisticsOptions?>();
        var dialog = new BlockingDialog(release.Task);
        var controller = new GraphStatisticsEditController(graph, dialog);

        var first = controller.EditAsync();
        Assert.False(await controller.EditAsync());
        release.SetResult(Hide);
        Assert.True(await first);
        Assert.Equal(1, dialog.Opened);
    }

    private sealed class BlockingDialog(Task<GraphStatisticsOptions?> answer) : IGraphStatisticsDialog
    {
        public int Opened { get; private set; }

        public Task<GraphStatisticsOptions?> EditAsync(GraphTypeDefinition definition, GraphStatisticsOptions current)
        {
            Opened++;
            return answer;
        }
    }

    // ---- The right-click menu ----

    [Fact]
    public void EditStatisticsIsOfferedOnTheRightClickMenu()
    {
        var command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }, () => false);
        var item = GraphWindow.EditStatisticsItem(command);

        Assert.Equal("Edit _Statistics...", item.Header);
        Assert.Same(command, item.Command);
    }

    // Copy Image, then the editors each graph type offers, in one order - Edit Statistics only where the graph type has
    // a panel, Edit Box Plot (Task #047) only for a box plot, and Edit Appearance (Task #046) last, everywhere. Every access
    // key is used once.
    [Theory]
    [InlineData(GraphType.ScatterPlot, new[] { "_Copy Image", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit A_ppearance..." })]
    [InlineData(GraphType.BoxPlot, new[] { "_Copy Image", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Box Plot...", "Edit A_ppearance..." })]
    [InlineData(GraphType.Histogram, new[] { "_Copy Image", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Statistics...", "Edit A_ppearance..." })]
    [InlineData(GraphType.ProbabilityPlot, new[] { "_Copy Image", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Statistics...", "Edit A_ppearance..." })]
    [InlineData(GraphType.EmpiricalCdf, new[] { "_Copy Image", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Statistics...", "Edit A_ppearance..." })]
    public void TheRightClickMenuOffersTheEditorsInOrder(GraphType type, string[] expected)
    {
        var commands = Enumerable.Range(0, 7).Select(_ => new CommunityToolkit.Mvvm.Input.RelayCommand(() => { })).ToArray();

        var items = GraphWindow.ContextMenuItems(GraphTypeDefinitions.For(type), commands[0], commands[1], commands[2], commands[3], commands[4], commands[5], commands[6]);

        Assert.Equal(expected, items.Select(item => item is MenuItem menuItem ? (string)menuItem.Header! : "-"));
        var keys = items.OfType<MenuItem>().Select(item => char.ToUpperInvariant(((string)item.Header!)[((string)item.Header!).IndexOf('_') + 1])).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
        if (type is GraphType.Histogram or GraphType.ProbabilityPlot or GraphType.EmpiricalCdf)
        {
            Assert.Same(commands[4], ((MenuItem)items[^2]).Command);
        }

        if (type == GraphType.BoxPlot)
        {
            Assert.Same(commands[5], ((MenuItem)items[^2]).Command);
        }

        Assert.Same(commands[6], ((MenuItem)items[^1]).Command);
    }

    // ---- The editor ----

    [Fact]
    public void TheEditorOffersEveryModeAndKeepsTheStatisticsWhileHidden()
    {
        var statistics = new GraphStatisticsEditorViewModel(HistogramDefinition);

        Assert.Equal(["Auto", "Show", "Hide"], statistics.ModeChoices.Select(choice => choice.Name));
        Assert.Equal(GraphStatisticsOptions.Default, statistics.Options);
        Assert.True(statistics.AreItemsEnabled);
        Assert.True(statistics.IsValid);

        statistics.ShowMean = false;
        statistics.SelectedMode = statistics.ModeChoices[2];
        Assert.False(statistics.AreItemsEnabled);
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Hide, false, true, true), statistics.Options);

        // Hidden, the statistics are only not editable: what was chosen is still chosen.
        Assert.False(statistics.ShowMean);
        Assert.True(statistics.ShowStandardDeviation);
        Assert.True(statistics.ShowCount);

        statistics.SelectedMode = statistics.ModeChoices[1];
        Assert.True(statistics.AreItemsEnabled);
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Show, false, true, true), statistics.Options);
    }

    [Fact]
    public void TheEditorStartsFromTheOptionsItIsGiven()
    {
        var statistics = new GraphStatisticsEditorViewModel(HistogramDefinition, new GraphStatisticsOptions(GraphStatisticsMode.Hide, true, false, false));

        Assert.Equal(GraphStatisticsMode.Hide, statistics.SelectedMode.Value);
        Assert.True(statistics.ShowMean);
        Assert.False(statistics.ShowStandardDeviation);
        Assert.False(statistics.ShowCount);
        Assert.False(statistics.AreItemsEnabled);
    }

    // A shown panel needs a statistic; a hidden one does not, and keeps none if none was chosen.
    [Fact]
    public void AShownPanelWithoutAStatisticIsExplained()
    {
        var statistics = new GraphStatisticsEditorViewModel(HistogramDefinition);
        var changed = new List<string?>();
        statistics.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        statistics.ShowMean = false;
        statistics.ShowStandardDeviation = false;
        Assert.True(statistics.IsValid);
        statistics.ShowCount = false;

        Assert.False(statistics.IsValid);
        Assert.Equal("Choose at least one statistic, or hide the statistics.", statistics.ValidationMessage);
        Assert.Contains(nameof(GraphStatisticsEditorViewModel.ValidationMessage), changed);

        statistics.SelectedMode = statistics.ModeChoices[2];
        Assert.True(statistics.IsValid);
        Assert.Null(statistics.ValidationMessage);

        statistics.SelectedMode = statistics.ModeChoices[1];
        Assert.False(statistics.IsValid);
    }

    [Fact]
    public void InvalidStatisticsAreExplained()
    {
        Assert.Equal("Choose at least one statistic, or hide the statistics.",
            GraphValidationMessages.For(new GraphValidationError(GraphValidationReason.StatisticsItemsMissing), HistogramDefinition));
        Assert.Equal("Please choose how the statistics are shown.",
            GraphValidationMessages.For(new GraphValidationError(GraphValidationReason.StatisticsOptionsInvalid), HistogramDefinition));
    }

    // One row - the mode, then Mean, StDev and N - each control named after what it edits. (That the three statistics
    // are not editable while the panel is hidden is the view model's AreItemsEnabled, bound here and checked in the
    // running application.)
    [Fact]
    public void TheEditorIsOneRowOfNamedControls()
    {
        var statistics = new GraphStatisticsEditorViewModel(HistogramDefinition);
        var row = Assert.IsType<StackPanel>(GraphStatisticsEditor.Create(statistics));

        Assert.Equal(Avalonia.Layout.Orientation.Horizontal, row.Orientation);
        Assert.Equal(
            ["Statistics", GraphStatisticsEditor.ModeName, "ShowMean", "ShowStandardDeviation", "ShowCount"],
            row.Children.Select(child => child is TextBlock text ? text.Text : child.Name));
        Assert.Equal(["Mean", "StDev", "N"], row.Children.OfType<CheckBox>().Select(item => (string)item.Content!));
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

    private static GraphSetupViewModel Setup(GraphType type)
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(type), Sheet, [Reg1Column]);
        var variable = setup.Roles.Single(role => role.Role == GraphVariableRole.Variable || role.Role == GraphVariableRole.X);
        variable.Choose(variable.Options.Single(option => option.Name == "Reg1"));
        return setup;
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void TheSetupCarriesTheStatisticsOptions(GraphType type)
    {
        var setup = Setup(type);

        Assert.True(setup.SupportsStatisticsPanel);
        Assert.Equal(GraphStatisticsOptions.Default, setup.Confirm()!.StatisticsOptions);

        setup.Statistics.ShowStandardDeviation = false;
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Auto, true, false, true), setup.Confirm()!.StatisticsOptions);

        setup.Statistics.SelectedMode = setup.Statistics.ModeChoices[2];
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Hide, true, false, true), setup.Confirm()!.StatisticsOptions);

        // Back to Auto: the statistics chosen before come back with it.
        setup.Statistics.SelectedMode = setup.Statistics.ModeChoices[0];
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Auto, true, false, true), setup.Confirm()!.StatisticsOptions);
    }

    [Fact]
    public void TheSetupRefusesAShownPanelWithoutAStatistic()
    {
        var setup = Setup(GraphType.Histogram);
        Assert.True(setup.CanConfirm);

        setup.Statistics.ShowMean = false;
        setup.Statistics.ShowStandardDeviation = false;
        setup.Statistics.ShowCount = false;

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal("Choose at least one statistic, or hide the statistics.", setup.ValidationMessage);

        // Hidden, it may keep no statistic at all.
        setup.Statistics.SelectedMode = setup.Statistics.ModeChoices[2];
        Assert.True(setup.CanConfirm);
        Assert.Equal(new GraphStatisticsOptions(GraphStatisticsMode.Hide, false, false, false), setup.Confirm()!.StatisticsOptions);

        // And not converted into anything: Show refuses it again.
        setup.Statistics.SelectedMode = setup.Statistics.ModeChoices[1];
        Assert.False(setup.CanConfirm);
    }

    // A graph type without a panel carries the default, whatever the (unshown) editor holds, and is never refused for it.
    [Fact]
    public void AGraphTypeWithoutAPanelCarriesTheDefault()
    {
        var setup = Setup(GraphType.BoxPlot);
        setup.Statistics.ShowMean = false;
        setup.Statistics.ShowStandardDeviation = false;
        setup.Statistics.ShowCount = false;

        Assert.False(setup.SupportsStatisticsPanel);
        Assert.True(setup.CanConfirm);
        Assert.Same(GraphStatisticsOptions.Default, setup.Confirm()!.StatisticsOptions);
    }
}
