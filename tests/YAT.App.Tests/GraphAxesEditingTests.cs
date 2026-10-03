using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.App.Tests.TestDoubles;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Editing the axis ranges (Task #043): in the setup and on a drawn graph, through the one editor. A blank field is Auto;
// text is read the way numbers are read everywhere in YAT; a range is refused for the same reasons in the same words in
// both places, and a drawn graph also refuses a range that does not fit its automatic ends. Confirmed ranges go onto the
// frame the graph had before any range, so Auto brings back the automatic ranges exactly.
public class GraphAxesEditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 400;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)))];

    private static GraphColumnInfo Column(string name) => new(Guid.Empty, name, WorksheetDataType.Numeric);

    // A histogram with a specification, presented as the graph preparation presents it.
    private static (GraphPresentationState Graph, IGraphPlotRenderer Plot) Histogram(GraphAxisRangeOptions? ranges = null, GraphLabelOptions? labels = null)
    {
        var data = new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), Reg1, null);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1"), Token)!;
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.Empty, [])
        {
            Specification = new Specification(14.8, 15, 15.3),
            AxisRangeOptions = ranges ?? GraphAxisRangeOptions.Default,
            LabelOptions = labels ?? GraphLabelOptions.Default
        };

        return (GraphPresentation.Present(model.Frame, data, configuration, Token), new HistogramRenderer(model));
    }

    private sealed class FakeAxesDialog(Func<GraphAxisRangeOptions, GraphAxisRangeOptions?> answer) : IGraphAxesDialog
    {
        public List<(GraphTypeDefinition Definition, GraphAxisRangeOptions Current, GraphRenderModel AutoFrame)> Calls { get; } = [];

        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame, GraphAxisTickOptions ticks)
        {
            Calls.Add((definition, current, autoFrame));
            return Task.FromResult(answer(current));
        }
    }

    private static readonly GraphAxisRangeOptions Chosen =
        new(new GraphAxisRangeOption(14.9, 15.2), new GraphAxisRangeOption(null, 60));

    // ---- The controller ----

    [Fact]
    public async Task ConfirmedRangesAreShownOverTheAutomaticFrameWithoutTheData()
    {
        var (graph, _) = Histogram();
        var dialog = new FakeAxesDialog(_ => Chosen);
        var controller = new GraphAxesEditController(graph, dialog);
        var changed = 0;
        controller.GraphChanged += (_, _) => changed++;

        Assert.True(await controller.EditAsync());

        var call = Assert.Single(dialog.Calls);
        Assert.Same(graph.BaseFrame, call.AutoFrame);
        Assert.Same(GraphAxisRangeOptions.Default, call.Current);
        Assert.Equal(1, changed);
        Assert.Same(graph.BaseFrame, controller.Graph.BaseFrame);
        Assert.Equal(Chosen, controller.Graph.AxisRangeOptions);
        Assert.Equal(new GraphAxisRange(14.9, 15.2), controller.Graph.Frame.XAxis.Range);
        Assert.Equal(60, controller.Graph.Frame.YAxis.Range.Maximum);
        Assert.Equal(0, controller.Graph.Frame.YAxis.Range.Minimum);
    }

    [Fact]
    public async Task AutoAgainBringsBackTheAutomaticFrameItself()
    {
        var (graph, plot) = Histogram(Chosen);
        var controller = new GraphAxesEditController(graph, new FakeAxesDialog(_ => GraphAxisRangeOptions.Default));

        Assert.True(await controller.EditAsync());

        var (auto, _) = Histogram();
        Assert.Same(controller.Graph.BaseFrame, controller.Graph.Frame);
        var service = new GraphExportService();
        Assert.Equal(
            service.RenderPng(new GraphExportSnapshot(auto.Frame, plot, GraphThemes.Light)),
            service.RenderPng(new GraphExportSnapshot(controller.Graph.Frame, plot, GraphThemes.Light)));
    }

    [Fact]
    public async Task ACancelledEditChangesNothing()
    {
        var (graph, _) = Histogram(Chosen);
        var controller = new GraphAxesEditController(graph, new FakeAxesDialog(_ => null));
        var changed = false;
        controller.GraphChanged += (_, _) => changed = true;

        Assert.False(await controller.EditAsync());
        Assert.Same(graph, controller.Graph);
        Assert.False(changed);
    }

    // A dialog is expected to refuse these; the controller refuses them too.
    [Fact]
    public async Task RangesThatBreakTheRulesOrDoNotFitTheGraphAreNeverShown()
    {
        var (graph, _) = Histogram();
        foreach (var refused in new[]
                 {
                     new GraphAxisRangeOptions(new GraphAxisRangeOption(20, 10), GraphAxisRangeOption.Auto),
                     new GraphAxisRangeOptions(GraphAxisRangeOption.Auto, new GraphAxisRangeOption(-1, null)),
                     new GraphAxisRangeOptions(new GraphAxisRangeOption(1000, null), GraphAxisRangeOption.Auto)
                 })
        {
            var controller = new GraphAxesEditController(graph, new FakeAxesDialog(_ => refused));
            Assert.False(await controller.EditAsync());
            Assert.Same(graph, controller.Graph);
        }
    }

    // Labels and ranges are edited on the one graph: each keeps the other.
    [Fact]
    public async Task RangesKeepTheLabelsAndLabelsKeepTheRanges()
    {
        var labels = new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Hidden, GraphLabelOption.Auto);
        var (graph, _) = Histogram(labels: labels);
        var axes = new GraphAxesEditController(graph, new FakeAxesDialog(_ => Chosen));

        Assert.True(await axes.EditAsync());
        Assert.Equal("Wafer", axes.Graph.Frame.Title);
        Assert.Null(axes.Graph.Frame.XAxis.Title);

        var relabelled = axes.Graph.WithLabels(GraphLabelOptions.Default);
        Assert.Equal(new GraphAxisRange(14.9, 15.2), relabelled.Frame.XAxis.Range);
        Assert.Equal("Reg1", relabelled.Frame.XAxis.Title);

        // The window hands each editor the graph the other made.
        var labelsController = new GraphLabelEditController(graph, new NoLabelsDialog());
        labelsController.Show(axes.Graph);
        Assert.Same(axes.Graph, labelsController.Graph);
        axes.Show(relabelled);
        Assert.Same(relabelled, axes.Graph);
    }

    private sealed class NoLabelsDialog : IGraphLabelsDialog
    {
        public Task<GraphLabelOptions?> EditAsync(GraphTypeDefinition definition, GraphLabelOptions current, GraphLabelField? focus, string? shownText) =>
            Task.FromResult<GraphLabelOptions?>(null);
    }

    [Fact]
    public async Task OneEditAtATime()
    {
        var (graph, _) = Histogram();
        var release = new TaskCompletionSource<GraphAxisRangeOptions?>();
        var dialog = new BlockingDialog(release.Task);
        var controller = new GraphAxesEditController(graph, dialog);

        var first = controller.EditAsync();
        Assert.False(await controller.EditAsync());
        release.SetResult(Chosen);
        Assert.True(await first);
        Assert.Equal(1, dialog.Opened);
    }

    private sealed class BlockingDialog(Task<GraphAxisRangeOptions?> answer) : IGraphAxesDialog
    {
        public int Opened { get; private set; }

        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame, GraphAxisTickOptions ticks)
        {
            Opened++;
            return answer;
        }
    }

    // ---- The editor ----

    [Fact]
    public void BlankFieldsAreAutoAndTypedOnesAreRead()
    {
        var axes = new GraphAxesEditorViewModel(GraphTypeDefinitions.For(GraphType.ScatterPlot));

        Assert.Equal(GraphAxisRangeOptions.Default, axes.Options);
        Assert.True(axes.IsValid);
        axes.XMinimumText = " 1e1 ";
        axes.YMaximumText = "-2.5";
        Assert.Equal(new GraphAxisRangeOptions(new GraphAxisRangeOption(10, null), new GraphAxisRangeOption(null, -2.5)), axes.Options);
        Assert.True(axes.IsValid);
    }

    [Fact]
    public void TextThatIsNotANumberIsReportedOnceForItsField()
    {
        var axes = new GraphAxesEditorViewModel(GraphTypeDefinitions.For(GraphType.ScatterPlot)) { YMinimumText = "abc" };

        Assert.Equal(
            new GraphValidationError(GraphValidationReason.AxisRangeValueNotNumeric, Axis: GraphAxisField.Y, Bound: GraphAxisBound.Minimum),
            Assert.Single(axes.Errors));
        Assert.Equal("Y-axis minimum must be a number.", axes.ValidationMessage);
        Assert.True(axes.Options.Y.IsAuto);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot, "20", "10", "X-axis minimum must be below the X-axis maximum.")]
    [InlineData(GraphType.Histogram, "-1", "", "Y-axis minimum cannot be below 0.")]
    [InlineData(GraphType.EmpiricalCdf, "", "101", "Y-axis maximum must be from 0 to 100.")]
    [InlineData(GraphType.ProbabilityPlot, "0", "", "Y-axis minimum must be from 0.0001 to 99.9999 (%).")]
    [InlineData(GraphType.ScatterPlot, "1e15", "1000000000000000.25", "The X-axis range is too narrow to be shown.")]
    public void RefusedRangesAreExplainedInTheSetupsWords(GraphType type, string minimum, string maximum, string message)
    {
        var axes = new GraphAxesEditorViewModel(GraphTypeDefinitions.For(type));
        if (message.StartsWith("X", StringComparison.Ordinal) || message.Contains("X-axis", StringComparison.Ordinal))
        {
            axes.XMinimumText = minimum;
            axes.XMaximumText = maximum;
        }
        else
        {
            axes.YMinimumText = minimum;
            axes.YMaximumText = maximum;
        }

        Assert.False(axes.IsValid);
        Assert.Equal(message, axes.ValidationMessage);
    }

    [Fact]
    public void EachGraphTypeOffersItsOwnAxes()
    {
        var box = new GraphAxesEditorViewModel(GraphTypeDefinitions.For(GraphType.BoxPlot)) { XMinimumText = "5", XMaximumText = "1" };
        Assert.False(box.SupportsXAxisRange);
        Assert.True(box.SupportsYAxisRange);
        Assert.True(box.IsValid);
        Assert.True(box.Options.X.IsAuto);

        var probability = new GraphAxesEditorViewModel(GraphTypeDefinitions.For(GraphType.ProbabilityPlot));
        Assert.Equal("X axis", probability.XAxisLabel);
        Assert.Equal("Y axis (%)", probability.YAxisLabel);
        Assert.Equal("Y axis", new GraphAxesEditorViewModel(GraphTypeDefinitions.For(GraphType.EmpiricalCdf)).YAxisLabel);
        Assert.Equal("Auto", probability.YMinimumHint);
    }

    // On a drawn graph: the automatic values beside the blank fields, and a range that does not fit them refused at once.
    [Fact]
    public void OnADrawnGraphTheAutomaticValuesAreShownAndARangeThatDoesNotFitThemIsRefused()
    {
        var (graph, _) = Histogram();
        var axes = new GraphAxesEditorViewModel(graph.Definition, GraphAxisRangeOptions.Default, graph.BaseFrame);
        var x = graph.BaseFrame.XAxis.Range;

        Assert.Equal($"Auto ({x.Minimum.ToString("G8", System.Globalization.CultureInfo.InvariantCulture)})", axes.XMinimumHint);
        Assert.Equal($"Auto ({x.Maximum.ToString("G8", System.Globalization.CultureInfo.InvariantCulture)})", axes.XMaximumHint);
        Assert.Equal("Auto (0)", axes.YMinimumHint);

        axes.XMinimumText = "100";
        Assert.False(axes.IsValid);
        Assert.StartsWith("The X-axis minimum (100) must be below the automatic X-axis maximum (", axes.ValidationMessage);

        axes.XMinimumText = "15";
        Assert.True(axes.IsValid);
    }

    [Fact]
    public void ADrawnGraphsRangesAreShownAsTheyWereTyped()
    {
        var (graph, _) = Histogram(Chosen);
        var axes = new GraphAxesEditorViewModel(graph.Definition, graph.AxisRangeOptions, graph.BaseFrame);

        Assert.Equal(("14.9", "15.2", "", "60"), (axes.XMinimumText, axes.XMaximumText, axes.YMinimumText, axes.YMaximumText));
        Assert.Equal(Chosen, axes.Options);
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
        var variable = setup.Roles.Single(role => role.Role == GraphVariableRole.Variable);
        variable.Choose(variable.Options.Single(option => option.Name == "Reg1"));
        return setup;
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public void AnUntouchedSetupConfiguresBothAxesAuto(GraphType type)
    {
        var setup = Setup(type);

        Assert.True(setup.SupportsAxisRanges);
        Assert.Equal(GraphAxisRangeOptions.Default, setup.Confirm()!.AxisRangeOptions);
    }

    [Fact]
    public void TypedRangesReachTheConfigurationAndSurviveAChangeOfScale()
    {
        var setup = Setup(GraphType.Histogram);
        setup.Axes.XMinimumText = "14.9";
        setup.Axes.YMaximumText = "50";

        Assert.Equal(new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, null), new GraphAxisRangeOption(null, 50)), setup.Confirm()!.AxisRangeOptions);

        // Another Y scale keeps what was typed for the Y axis.
        setup.SelectedYScale = setup.YScaleChoices.Single(choice => choice.Value == HistogramYScale.Density);
        Assert.Equal("50", setup.Axes.YMaximumText);
        Assert.Equal(50, setup.Confirm()!.AxisRangeOptions.Y.Maximum);
    }

    [Fact]
    public void ARefusedRangeStopsTheSetupWithItsReason()
    {
        var setup = Setup(GraphType.EmpiricalCdf);
        setup.Axes.YMinimumText = "x";

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal("Y-axis minimum must be a number.", setup.ValidationMessage);

        setup.Axes.YMinimumText = "120";
        Assert.Null(setup.Confirm());
        Assert.Equal("Y-axis minimum must be from 0 to 100.", setup.ValidationMessage);

        setup.Axes.YMinimumText = "90";
        Assert.NotNull(setup.Confirm());
        Assert.True(setup.CanConfirm);
    }

    // The column problems come first, then the specification, the labels and the axes, as the dialog reads.
    [Fact]
    public void AxisProblemsComeAfterEverythingElse()
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(GraphType.Histogram), Sheet, [Reg1Column]);
        setup.Axes.XMinimumText = "x";
        setup.LowerLimitText = "y";

        Assert.Null(setup.Confirm());
        Assert.Equal("Please select a variable.", setup.ValidationMessage);
    }
}
