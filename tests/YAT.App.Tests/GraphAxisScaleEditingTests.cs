using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using YAT.app.Views;
using static YAT.App.Tests.GraphLabelEditingTests;

namespace YAT.App.Tests;

// Editing one axis's scale by double-clicking it (Task #052): which axis a point is on, from the very layout the graph
// is drawn with; what a double-click edits (a title first, then an axis the graph type gives a range, else nothing);
// the Edit X / Y Scale dialog over the #043 ranges; and the graph it gives - only that axis changed, presentation only,
// kept through every other edit, copied and exported as shown, and Auto/Auto exactly the graph as it was.
public class GraphAxisScaleEditingTests
{
    private static readonly SKRect Canvas = new(0, 0, 760, 488);

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    private static SKPoint Centre(SKRect rect) => new(rect.MidX, rect.MidY);

    private static float Reach => (GraphThemes.Light.AxisThickness / 2f) + GraphAxisHitTest.LineTolerance;

    private static (GraphRenderModel Frame, GraphLayout Layout, GraphAxesGeometry Axes, IReadOnlyList<GraphLabelGeometry> Labels) Drawn(GraphType type, SKRect? canvas = null, GraphTheme? theme = null)
    {
        var frame = Present(type).Graph.Frame;
        var bounds = canvas ?? Canvas;
        var used = theme ?? GraphThemes.Light;
        return (frame, SkiaGraphRenderer.Layout(frame, bounds, used), SkiaGraphRenderer.AxisGeometry(frame, bounds, used), SkiaGraphRenderer.LabelGeometry(frame, bounds, used));
    }

    // What a double-click at a point edits, as the graph window decides it.
    private static (GraphLabelField? Label, GraphAxisField? Axis) Target(GraphType type, SKPoint point)
    {
        var (_, _, axes, labels) = Drawn(type);
        var label = GraphLabelHitTest.Find(labels, point);
        return GraphWindow.DoubleClickTarget(label, label is null ? GraphAxisHitTest.Find(axes, point) : null, GraphTypeDefinitions.For(type));
    }

    // ---- Which axis a point is on ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheTicksAndTickLabelsOfAnAxisPickIt(GraphType type)
    {
        var (_, layout, axes, _) = Drawn(type);

        Assert.Equal(GraphAxisField.X, GraphAxisHitTest.Find(axes, new SKPoint(layout.PlotArea.Left + 20, layout.PlotArea.Bottom + 3)));
        Assert.Equal(GraphAxisField.X, GraphAxisHitTest.Find(axes, new SKPoint(layout.PlotArea.MidX + 40, layout.PlotArea.Bottom + 12)));
        Assert.Equal(GraphAxisField.Y, GraphAxisHitTest.Find(axes, new SKPoint(layout.PlotArea.Left - 3, layout.PlotArea.MidY + 30)));
        Assert.Equal(GraphAxisField.Y, GraphAxisHitTest.Find(axes, new SKPoint(layout.PlotArea.Left - 12, layout.PlotArea.Top + 20)));
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AnAxisIsPickedWithinThreePixelsOfItsLineInsideThePlotButNotBeyond(GraphType type)
    {
        var (_, layout, axes, _) = Drawn(type);
        var plot = layout.PlotArea;

        Assert.Equal(3f, GraphAxisHitTest.LineTolerance);
        Assert.Equal(GraphAxisField.X, GraphAxisHitTest.Find(axes, new SKPoint(plot.MidX, plot.Bottom - Reach + 0.25f)));
        Assert.Null(GraphAxisHitTest.Find(axes, new SKPoint(plot.MidX, plot.Bottom - Reach - 0.75f)));
        Assert.Equal(GraphAxisField.Y, GraphAxisHitTest.Find(axes, new SKPoint(plot.Left + Reach - 0.25f, plot.MidY)));
        Assert.Null(GraphAxisHitTest.Find(axes, new SKPoint(plot.Left + Reach + 0.75f, plot.MidY)));
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheLastXTickLabelPicksXWhereItOverhangsThePlot(GraphType type)
    {
        var (_, layout, axes, _) = Drawn(type);
        var x = Assert.Single(axes.Axes, axis => axis.Axis == GraphAxisField.X);

        Assert.True(x.Area.Right > layout.XAxisArea.Right, "X reaches on past the plot by the last label's overhang");
        Assert.Equal(GraphAxisField.X, GraphAxisHitTest.Find(axes, new SKPoint((layout.XAxisArea.Right + x.Area.Right) / 2f, layout.PlotArea.Bottom + 12)));
        foreach (var other in new[] { layout.LegendArea, layout.StatisticsPanelArea }.Where(area => !area.IsEmpty))
        {
            Assert.False(SKRect.Intersect(x.Area, other) is { IsEmpty: false }, $"{type}: the overhang reaches into {other}");
        }
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void ThePlotTheLegendTheStatisticsAndTheReferenceLabelsPickNoAxis(GraphType type)
    {
        var (_, layout, axes, _) = Drawn(type);
        var nowhere = new List<(string What, SKPoint Point)>
        {
            ("plot centre", Centre(layout.PlotArea)),
            ("plot top right", new SKPoint(layout.PlotArea.Right - 6, layout.PlotArea.Top + 6)),
            ("corner where the axes meet", new SKPoint(layout.PlotArea.Left - 1, layout.PlotArea.Bottom + 1)),
            ("canvas corner", new SKPoint(2, 2)),
            ("title", Centre(layout.TitleArea))
        };
        foreach (var (what, area) in new[] { ("legend", layout.LegendArea), ("statistics", layout.StatisticsPanelArea), ("reference labels", layout.ReferenceLabelArea) })
        {
            if (!area.IsEmpty)
            {
                nowhere.Add((what, Centre(area)));
            }
        }

        Assert.Contains(nowhere, entry => entry.What == "legend");
        foreach (var (what, point) in nowhere)
        {
            Assert.True(GraphAxisHitTest.Find(axes, point) is null, $"{type}: the {what} at {point} picked an axis");
        }
    }

    [Fact]
    public void SomeGraphsHaveStatisticsAndReferenceLabelsToStayClearOf()
    {
        var (_, layout, _, _) = Drawn(GraphType.Histogram);

        Assert.False(layout.StatisticsPanelArea.IsEmpty);
        Assert.False(layout.ReferenceLabelArea.IsEmpty);
    }

    [Fact]
    public void ACanvasTooSmallForAPlotHasNoAxes()
    {
        var frame = Present(GraphType.ScatterPlot).Graph.Frame;

        Assert.Empty(SkiaGraphRenderer.AxisGeometry(frame, new SKRect(0, 0, 60, 40), GraphThemes.Light).Axes);
        Assert.Empty(SkiaGraphRenderer.AxisGeometry(frame, SKRect.Empty, GraphThemes.Light).Axes);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheAxesArePickedWhereverTheyAreDrawnAtAnySizeAndTheme(GraphType type)
    {
        foreach (var (canvas, theme) in new[] { (new SKRect(0, 0, 500, 360), GraphThemes.Dark), (new SKRect(0, 0, 1200, 800), GraphThemes.Light) })
        {
            var (_, layout, axes, _) = Drawn(type, canvas, theme);
            Assert.Equal(GraphAxisField.X, GraphAxisHitTest.Find(axes, new SKPoint(layout.PlotArea.MidX + 40, layout.PlotArea.Bottom + 12)));
            Assert.Equal(GraphAxisField.Y, GraphAxisHitTest.Find(axes, new SKPoint(layout.PlotArea.Left - 12, layout.PlotArea.MidY + 30)));
            Assert.Null(GraphAxisHitTest.Find(axes, Centre(layout.PlotArea)));
        }
    }

    // ---- What a double-click edits ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AnAxisTitleStillEditsTheLabelsAndTheRestOfTheAxisEditsItsScale(GraphType type)
    {
        var (_, layout, _, labels) = Drawn(type);
        var xTitle = Assert.Single(labels, label => label.Field == GraphLabelField.XAxisTitle);
        var yTitle = labels.SingleOrDefault(label => label.Field == GraphLabelField.YAxisTitle);

        Assert.Equal((GraphLabelField.XAxisTitle, (GraphAxisField?)null), Target(type, Centre(xTitle.Text)));
        if (yTitle is not null)
        {
            Assert.Equal((GraphLabelField.YAxisTitle, (GraphAxisField?)null), Target(type, Centre(yTitle.Text)));
        }

        var xTicks = Target(type, new SKPoint(layout.PlotArea.MidX + 40, layout.PlotArea.Bottom + 12));
        Assert.Equal(type == GraphType.BoxPlot ? ((GraphLabelField?)null, (GraphAxisField?)null) : (null, GraphAxisField.X), xTicks);
        Assert.Equal(((GraphLabelField?)null, GraphAxisField.Y), Target(type, new SKPoint(layout.PlotArea.Left - 12, layout.PlotArea.MidY + 30)));
        Assert.Equal(((GraphLabelField?)null, (GraphAxisField?)null), Target(type, Centre(layout.PlotArea)));
    }

    [Fact]
    public void EveryNumericAxisHasAScaleAndABoxPlotsCategoriesHaveNone()
    {
        foreach (var type in Enum.GetValues<GraphType>())
        {
            var definition = GraphTypeDefinitions.For(type);
            Assert.Equal(type != GraphType.BoxPlot, definition.SupportsAxisRange(GraphAxisField.X));
            Assert.True(definition.SupportsAxisRange(GraphAxisField.Y));
        }
    }

    // ---- The controller ----

    private sealed class ScaleDialog(Func<GraphAxisField, GraphAxisRangeOptions, GraphAxisRangeOption?> answer) : IGraphAxisScaleDialog
    {
        public List<(GraphAxisField Axis, GraphAxisRangeOptions Current, GraphRenderModel AutoFrame)> Calls { get; } = [];

        public Task<GraphAxisRangeOption?> EditAsync(GraphTypeDefinition definition, GraphAxisField axis, GraphAxisRangeOptions current, GraphRenderModel autoFrame)
        {
            Calls.Add((axis, current, autoFrame));
            return Task.FromResult(answer(axis, current));
        }
    }

    private sealed class NoAxesDialog : IGraphAxesDialog
    {
        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame) =>
            Task.FromResult<GraphAxisRangeOptions?>(null);
    }

    private static readonly GraphAxisRangeOption YChosen = new(14.7, 15.4);

    private static (GraphAxesEditController Controller, ScaleDialog Dialog, Presented Presented) Editing(GraphType type, GraphAxisRangeOption? answer, GraphAxisRangeOptions? start = null)
    {
        var presented = Present(type);
        var graph = start is null ? presented.Graph : presented.Graph.WithAxisRanges(start);
        var dialog = new ScaleDialog((_, _) => answer);
        return (new GraphAxesEditController(graph, new NoAxesDialog(), dialog), dialog, presented);
    }

    [Theory]
    [InlineData(14.95, null)]
    [InlineData(null, 15.05)]
    [InlineData(14.95, 15.05)]
    public async Task AChosenXRangeReplacesOnlyXAndKeepsY(double? minimum, double? maximum)
    {
        var start = new GraphAxisRangeOptions(GraphAxisRangeOption.Auto, YChosen);
        var (controller, dialog, presented) = Editing(GraphType.ScatterPlot, new GraphAxisRangeOption(minimum, maximum), start);
        var changed = 0;
        controller.GraphChanged += (_, _) => changed++;

        Assert.True(await controller.EditScaleAsync(GraphAxisField.X));

        var call = Assert.Single(dialog.Calls);
        Assert.Equal((GraphAxisField.X, start), (call.Axis, call.Current));
        Assert.Same(presented.Graph.BaseFrame, call.AutoFrame);
        Assert.Equal(1, changed);
        Assert.Equal(new GraphAxisRangeOptions(new GraphAxisRangeOption(minimum, maximum), YChosen), controller.Graph.AxisRangeOptions);
        var (autoMinimum, autoMaximum) = GraphAxisViewportBuilder.AutoRange(presented.Graph.Frame.XAxis);
        Assert.Equal(new GraphAxisRange(minimum ?? autoMinimum, maximum ?? autoMaximum), controller.Graph.Frame.XAxis.Range);
        Assert.Equal(new GraphAxisRange(14.7, 15.4), controller.Graph.Frame.YAxis.Range);
        Assert.Same(presented.Graph.BaseFrame, controller.Graph.BaseFrame);
    }

    [Theory]
    [InlineData(GraphType.BoxPlot)]
    [InlineData(GraphType.Histogram)]
    public async Task AChosenYRangeReplacesOnlyY(GraphType type)
    {
        var (controller, _, presented) = Editing(type, new GraphAxisRangeOption(null, 60));

        Assert.True(await controller.EditScaleAsync(GraphAxisField.Y));

        Assert.Equal(new GraphAxisRangeOptions(GraphAxisRangeOption.Auto, new GraphAxisRangeOption(null, 60)), controller.Graph.AxisRangeOptions);
        Assert.Equal(60, controller.Graph.Frame.YAxis.Range.Maximum);
        Assert.Equal(presented.Graph.Frame.XAxis, controller.Graph.Frame.XAxis);
    }

    [Fact]
    public async Task AProbabilityAxisIsChosenInPercent()
    {
        var (controller, _, _) = Editing(GraphType.ProbabilityPlot, new GraphAxisRangeOption(1, 99));

        Assert.True(await controller.EditScaleAsync(GraphAxisField.Y));

        var (minimum, maximum) = GraphAxisViewportBuilder.AutoRange(controller.Graph.Frame.YAxis);
        Assert.Equal(1, minimum, 9);
        Assert.Equal(99, maximum, 9);
        Assert.Equal(GraphAxisScale.Probability, controller.Graph.Frame.YAxis.Scale);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot, GraphAxisField.X, 15.1, 15.0)]
    [InlineData(GraphType.ScatterPlot, GraphAxisField.X, 15.0, 15.0)]
    [InlineData(GraphType.ScatterPlot, GraphAxisField.X, double.NaN, null)]
    [InlineData(GraphType.ScatterPlot, GraphAxisField.X, null, double.PositiveInfinity)]
    [InlineData(GraphType.ScatterPlot, GraphAxisField.X, 1000.0, null)]
    [InlineData(GraphType.Histogram, GraphAxisField.Y, -1.0, null)]
    [InlineData(GraphType.EmpiricalCdf, GraphAxisField.Y, null, 101.0)]
    [InlineData(GraphType.ProbabilityPlot, GraphAxisField.Y, 0.0, 50.0)]
    [InlineData(GraphType.ProbabilityPlot, GraphAxisField.Y, 50.0, 100.0)]
    public async Task ARangeTheRulesRefuseOrThatDoesNotFitTheGraphIsNeverShown(GraphType type, GraphAxisField axis, double? minimum, double? maximum)
    {
        var (controller, _, presented) = Editing(type, new GraphAxisRangeOption(minimum, maximum));
        var changed = 0;
        controller.GraphChanged += (_, _) => changed++;

        Assert.False(await controller.EditScaleAsync(axis));

        Assert.Same(presented.Graph, controller.Graph);
        Assert.Equal(0, changed);
    }

    [Fact]
    public async Task ACancelledEditChangesNothing()
    {
        var (controller, dialog, presented) = Editing(GraphType.ScatterPlot, answer: null);

        Assert.False(await controller.EditScaleAsync(GraphAxisField.Y));

        Assert.Single(dialog.Calls);
        Assert.Same(presented.Graph, controller.Graph);
    }

    [Fact]
    public async Task ABoxPlotsCategoriesAreNeverEdited()
    {
        var (controller, dialog, presented) = Editing(GraphType.BoxPlot, new GraphAxisRangeOption(1, 2));

        Assert.False(await controller.EditScaleAsync(GraphAxisField.X));

        Assert.Empty(dialog.Calls);
        Assert.Same(presented.Graph, controller.Graph);
    }

    [Fact]
    public async Task ResetToAutoBringsBackTheAutomaticGraphByteForByte()
    {
        var (controller, _, presented) = Editing(GraphType.Histogram, GraphAxisRangeOption.Auto, new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, 15.2), GraphAxisRangeOption.Auto));
        Assert.NotEqual(presented.Graph.Frame.XAxis.Range, controller.Graph.Frame.XAxis.Range);

        Assert.True(await controller.EditScaleAsync(GraphAxisField.X));

        Assert.Equal(GraphAxisRangeOption.Auto, controller.Graph.AxisRangeOptions.X);
        Assert.True(controller.Graph.AxisRangeOptions.Y.IsAuto);
        Assert.Same(controller.Graph.BaseFrame, controller.Graph.UnlabelledFrame);
        var service = new GraphExportService();
        Assert.Equal(
            service.RenderPng(new GraphExportSnapshot(presented.Graph.Frame, presented.Plot, GraphThemes.Light)),
            service.RenderPng(new GraphExportSnapshot(controller.Graph.Frame, presented.Plot, GraphThemes.Light)));
    }

    [Fact]
    public async Task OneEditAtATimeWhicheverDialogItIs()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;
        var release = new TaskCompletionSource<GraphAxisRangeOption?>();
        var dialog = new BlockingScaleDialog(release.Task);
        var axes = new CountingAxesDialog();
        var controller = new GraphAxesEditController(graph, axes, dialog);

        var first = controller.EditScaleAsync(GraphAxisField.X);
        Assert.False(await controller.EditScaleAsync(GraphAxisField.Y));
        Assert.False(await controller.EditAsync());
        release.SetResult(new GraphAxisRangeOption(14.95, null));

        Assert.True(await first);
        Assert.Equal((1, 0), (dialog.Opened, axes.Opened));
    }

    private sealed class BlockingScaleDialog(Task<GraphAxisRangeOption?> answer) : IGraphAxisScaleDialog
    {
        public int Opened { get; private set; }

        public Task<GraphAxisRangeOption?> EditAsync(GraphTypeDefinition definition, GraphAxisField axis, GraphAxisRangeOptions current, GraphRenderModel autoFrame)
        {
            Opened++;
            return answer;
        }
    }

    private sealed class CountingAxesDialog : IGraphAxesDialog
    {
        public int Opened { get; private set; }

        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame)
        {
            Opened++;
            return Task.FromResult<GraphAxisRangeOptions?>(null);
        }
    }

    // ---- Kept, copied and exported ----

    [Fact]
    public async Task TheRangeIsKeptThroughTheGraphsOtherEdits()
    {
        var (controller, _, _) = Editing(GraphType.Histogram, new GraphAxisRangeOption(14.9, 15.2));
        Assert.True(await controller.EditScaleAsync(GraphAxisField.X));
        var edited = controller.Graph;
        var range = edited.Frame.XAxis.Range;

        foreach (var (what, graph) in new[]
                 {
                     ("labels", edited.WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("T"), GraphLabelOption.Custom("X"), GraphLabelOption.Hidden))),
                     ("legend", edited.WithLegend(edited.LegendOptions with { Position = GraphLegendPosition.Bottom })),
                     ("statistics", edited.WithStatistics(edited.StatisticsOptions with { Mode = GraphStatisticsMode.Hide })),
                     ("appearance", edited.WithAppearance(edited.AppearanceOptions with { GridMode = GraphGridMode.Hide }))
                 })
        {
            Assert.True(graph.AxisRangeOptions == edited.AxisRangeOptions, $"{what} kept the ranges");
            Assert.True(graph.Frame.XAxis.Range == range, $"{what} kept the X range");
        }
    }

    [Fact]
    public async Task CopiesAndExportsDrawTheRangeAsShownAtAnySize()
    {
        var (controller, _, presented) = Editing(GraphType.ScatterPlot, new GraphAxisRangeOption(14.95, 15.05));
        Assert.True(await controller.EditScaleAsync(GraphAxisField.X));
        var service = new GraphExportService();

        var shown = service.RenderPng(new GraphExportSnapshot(controller.Graph.Frame, presented.Plot, GraphThemes.Light));
        Assert.NotEqual(service.RenderPng(new GraphExportSnapshot(presented.Graph.Frame, presented.Plot, GraphThemes.Light)), shown);
        Assert.Equal(shown, service.RenderPng(new GraphExportSnapshot(presented.Graph.WithAxisRanges(controller.Graph.AxisRangeOptions).Frame, presented.Plot, GraphThemes.Light)));

        // A resize lays the same frame out again: its range is the frame's, not the size's.
        foreach (var canvas in new[] { new SKRect(0, 0, 400, 300), new SKRect(0, 0, 1400, 900) })
        {
            var layout = SkiaGraphRenderer.Layout(controller.Graph.Frame, canvas, GraphThemes.Dark);
            var transform = new GraphCoordinateTransform(controller.Graph.Frame.XAxis.Range, controller.Graph.Frame.YAxis.Range, layout.PlotArea);
            Assert.Equal(layout.PlotArea.Left, (float)transform.ToScreenX(14.95), 3);
            Assert.Equal(layout.PlotArea.Right, (float)transform.ToScreenX(15.05), 3);
        }
    }

    // ---- The dialog ----

    private static GraphAxisScaleEditorViewModel Dialog(GraphType type, GraphAxisField axis, GraphAxisRangeOptions? current = null)
    {
        var graph = Present(type).Graph;
        return new GraphAxisScaleEditorViewModel(graph.Definition, axis, current ?? GraphAxisRangeOptions.Default, graph.BaseFrame);
    }

    [Fact]
    public void ItOpensOnTheAxisAsItIsAutoShowingTheAutomaticValues()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;
        var scale = Dialog(GraphType.ScatterPlot, GraphAxisField.X);
        var (minimum, maximum) = GraphAxisViewportBuilder.AutoRange(graph.BaseFrame.XAxis);

        Assert.Equal(("Edit X Scale", "X axis", (string?)null), (scale.Title, scale.AxisLabel, scale.Unit));
        Assert.True(scale.MinimumIsAuto && scale.MaximumIsAuto);
        Assert.Equal((YAT.app.Analyses.AnalysisNumberFormat.Statistic(minimum), YAT.app.Analyses.AnalysisNumberFormat.Statistic(maximum)), (scale.MinimumText, scale.MaximumText));
        Assert.Equal(GraphAxisRangeOption.Auto, scale.Options.X);
        Assert.True(scale.Option.IsAuto);
        Assert.True(scale.IsValid);
        Assert.Equal("Edit Y Scale", Dialog(GraphType.ScatterPlot, GraphAxisField.Y).Title);
    }

    [Fact]
    public void ItOpensOnTheRangeAsChosen()
    {
        var scale = Dialog(GraphType.ScatterPlot, GraphAxisField.X, new GraphAxisRangeOptions(new GraphAxisRangeOption(14.95, null), YChosen));

        Assert.False(scale.MinimumIsAuto);
        Assert.True(scale.MaximumIsAuto);
        Assert.Equal("14.95", scale.MinimumText);
        Assert.Equal(new GraphAxisRangeOption(14.95, null), scale.Option);
        Assert.Equal(YChosen, scale.Options.Y);
    }

    [Fact]
    public void UncheckingAutoStartsFromTheAutomaticValue()
    {
        var scale = Dialog(GraphType.ScatterPlot, GraphAxisField.X);

        scale.MinimumIsAuto = false;

        Assert.Equal(scale.AutoMinimumText, scale.MinimumText);
        Assert.Equal(double.Parse(scale.AutoMinimumText, System.Globalization.CultureInfo.InvariantCulture), scale.Option.Minimum);
        Assert.Null(scale.Option.Maximum);
        Assert.True(scale.IsValid);

        scale.MinimumText = "14.9";
        scale.MinimumIsAuto = true;
        Assert.Equal(scale.AutoMinimumText, scale.MinimumText);
        Assert.True(scale.Option.IsAuto);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-∞")]
    [InlineData("")]
    public void AValueThatIsNotAFiniteNumberIsRefused(string text)
    {
        var scale = Dialog(GraphType.ScatterPlot, GraphAxisField.X);
        scale.MaximumIsAuto = false;
        scale.MaximumText = text;

        Assert.False(scale.IsValid);
        Assert.Equal("X-axis maximum must be a number.", scale.ValidationMessage);
    }

    [Fact]
    public void AMinimumNotBelowTheMaximumIsRefused()
    {
        var scale = Dialog(GraphType.ScatterPlot, GraphAxisField.Y);
        scale.MinimumIsAuto = false;
        scale.MaximumIsAuto = false;
        scale.MinimumText = "15.2";
        scale.MaximumText = "15.2";

        Assert.Equal("Y-axis minimum must be below the Y-axis maximum.", scale.ValidationMessage);

        scale.MaximumText = "15.3";
        Assert.True(scale.IsValid);
    }

    [Fact]
    public void ARangeThatDoesNotFitTheAutomaticOtherEndIsRefused()
    {
        var scale = Dialog(GraphType.ScatterPlot, GraphAxisField.X);
        scale.MinimumIsAuto = false;
        scale.MinimumText = "1000";

        Assert.False(scale.IsValid);
        Assert.NotNull(scale.ValidationMessage);
    }

    [Theory]
    [InlineData(GraphType.Histogram, "-1")]
    [InlineData(GraphType.EmpiricalCdf, "101")]
    [InlineData(GraphType.ProbabilityPlot, "0")]
    [InlineData(GraphType.ProbabilityPlot, "100")]
    public void EachAxisKeepsItsOwnLimits(GraphType type, string text)
    {
        var scale = Dialog(type, GraphAxisField.Y);
        if (text.StartsWith('-') || text == "0")
        {
            scale.MinimumIsAuto = false;
            scale.MinimumText = text;
        }
        else
        {
            scale.MaximumIsAuto = false;
            scale.MaximumText = text;
        }

        Assert.False(scale.IsValid);
        Assert.Equal(
            GraphValidationMessages.For(Assert.Single(scale.Errors), GraphTypeDefinitions.For(type)),
            scale.ValidationMessage);
    }

    [Fact]
    public void AProbabilityAxisIsTypedInPercent()
    {
        var scale = Dialog(GraphType.ProbabilityPlot, GraphAxisField.Y);

        Assert.Equal(("Y axis (%)", "%"), (scale.AxisLabel, scale.Unit));
        Assert.Equal("%", Dialog(GraphType.EmpiricalCdf, GraphAxisField.Y).Unit);
        Assert.Null(Dialog(GraphType.ProbabilityPlot, GraphAxisField.X).Unit);

        scale.MinimumIsAuto = false;
        scale.MaximumIsAuto = false;
        scale.MinimumText = "1";
        scale.MaximumText = "99";
        Assert.True(scale.IsValid);
        Assert.Equal(new GraphAxisRangeOption(1, 99), scale.Option);
    }

    [Fact]
    public void ResetToAutoChangesTheDialogOnly()
    {
        var scale = Dialog(GraphType.ScatterPlot, GraphAxisField.X, new GraphAxisRangeOptions(new GraphAxisRangeOption(14.95, 15.05), YChosen));

        scale.ResetToAuto();

        Assert.True(scale.MinimumIsAuto && scale.MaximumIsAuto);
        Assert.True(scale.Option.IsAuto);
        Assert.Equal((scale.AutoMinimumText, scale.AutoMaximumText), (scale.MinimumText, scale.MaximumText));
        Assert.Equal(YChosen, scale.Options.Y);
    }

    [Fact]
    public async Task ResetToAutoAndOkShowsTheAutomaticRangeAndResetAndCancelKeepsTheChosenOne()
    {
        var start = new GraphAxisRangeOptions(new GraphAxisRangeOption(14.95, 15.05), YChosen);
        GraphAxisRangeOption? Answer(bool ok, GraphAxisField axis, GraphAxisRangeOptions current)
        {
            var graph = Present(GraphType.ScatterPlot).Graph;
            var scale = new GraphAxisScaleEditorViewModel(graph.Definition, axis, current, graph.BaseFrame);
            scale.ResetToAuto();
            return ok ? scale.Option : null;
        }

        var presented = Present(GraphType.ScatterPlot);
        var ok = new GraphAxesEditController(presented.Graph.WithAxisRanges(start), new NoAxesDialog(), new ScaleDialog((axis, current) => Answer(true, axis, current)));
        Assert.True(await ok.EditScaleAsync(GraphAxisField.X));
        Assert.Equal(new GraphAxisRangeOptions(GraphAxisRangeOption.Auto, YChosen), ok.Graph.AxisRangeOptions);
        Assert.Equal(presented.Graph.Frame.XAxis.Range, ok.Graph.Frame.XAxis.Range);

        var cancel = new GraphAxesEditController(presented.Graph.WithAxisRanges(start), new NoAxesDialog(), new ScaleDialog((axis, current) => Answer(false, axis, current)));
        Assert.False(await cancel.EditScaleAsync(GraphAxisField.X));
        Assert.Equal(start, cancel.Graph.AxisRangeOptions);
    }

    [Fact]
    public void ABoxPlotsCategoriesHaveNoScaleDialog()
    {
        Assert.Throws<ArgumentException>(() => Dialog(GraphType.BoxPlot, GraphAxisField.X));
        Assert.Equal("Edit Y Scale", Dialog(GraphType.BoxPlot, GraphAxisField.Y).Title);
    }
}
