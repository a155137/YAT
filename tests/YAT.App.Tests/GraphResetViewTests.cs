using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.Views;
using static YAT.App.Tests.GraphLabelEditingTests;

namespace YAT.App.Tests;

// Reset View from the right-click menu and the Home key (Task #056), beside the double-click in the plot (#055): one
// command on the view controller, available only while the graph is zoomed or panned, running the very Reset the
// double-click runs - back to the configured ranges, Auto or chosen, with the ticks and everything else as they were.
[Collection(GraphMenuItemCollection.Name)]
public class GraphResetViewTests
{
    private static readonly SKRect Plot = new(100, 50, 600, 350);

    private static GraphAxisRangeOptions XRange(double minimum, double maximum) =>
        GraphAxisRangeOptions.Default with { X = new GraphAxisRangeOption(minimum, maximum) };

    private static byte[] Png(GraphRenderModel frame, IGraphPlotRenderer plot) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, GraphThemes.Light));

    private static GraphViewController Zoomed(GraphPresentationState graph)
    {
        var controller = new GraphViewController(graph);
        controller.ZoomAt(null, Plot, new SKPoint(260, 140), 3);
        controller.BeginPan(Plot, new SKPoint(300, 200));
        controller.PanTo(new SKPoint(340, 230));
        controller.EndPan();
        return controller;
    }

    // ---- The command ----

    [Fact]
    public void ResetViewIsAvailableOnlyWhileTheGraphIsZoomedOrPanned()
    {
        var controller = new GraphViewController(Present(GraphType.ScatterPlot).Graph);
        var changes = 0;
        controller.ResetCommand.CanExecuteChanged += (_, _) => changes++;
        Assert.False(controller.CanReset);
        Assert.False(controller.ResetCommand.CanExecute(null));

        controller.ZoomAt(null, Plot, new SKPoint(300, 200), 1);
        Assert.True(controller.ResetCommand.CanExecute(null));
        Assert.True(changes > 0);

        controller.ResetCommand.Execute(null);
        Assert.False(controller.ResetCommand.CanExecute(null));
        Assert.True(controller.Graph.ViewOptions.IsDefault);
    }

    [Fact]
    public void TheCommandFollowsAViewEndedOrBroughtByAnotherEdit()
    {
        var zoomed = Zoomed(Present(GraphType.ScatterPlot).Graph).Graph;
        var controller = new GraphViewController(Present(GraphType.ScatterPlot).Graph);
        Assert.False(controller.ResetCommand.CanExecute(null));

        controller.Show(zoomed);
        Assert.True(controller.ResetCommand.CanExecute(null));

        // Edit Axes committing new ranges for both axes ends both views.
        controller.Show(zoomed.WithView(GraphViewOptions.Default));
        Assert.False(controller.ResetCommand.CanExecute(null));
    }

    // ---- One Reset, three ways in ----

    [Fact]
    public void TheMenuTheHomeKeyAndTheDoubleClickResetAlike()
    {
        var presented = Present(GraphType.ScatterPlot);
        var start = presented.Graph.WithAxisScale(XRange(14, 16), XTicks(new GraphAxisTickOption.FixedInterval(0.5)));

        // Double-click in the plot: the window calls Reset.
        var doubleClick = Zoomed(start);
        Assert.True(doubleClick.Reset());

        // Right-click > Reset View: the menu item runs the command it was given.
        var menu = Zoomed(start);
        var item = GraphWindow.ResetViewItem(menu.ResetCommand);
        item.Command!.Execute(item.CommandParameter);

        // Home: the window's key binding runs the same command.
        var home = Zoomed(start);
        var binding = new KeyBinding { Gesture = GraphWindow.ResetViewGesture, Command = home.ResetCommand };
        binding.Command.Execute(binding.CommandParameter);

        foreach (var (how, graph) in new[] { ("menu", menu.Graph), ("Home", home.Graph) })
        {
            Assert.True(graph.ViewOptions == doubleClick.Graph.ViewOptions, $"{how}: the view");
            Assert.True(graph.AxisRangeOptions == doubleClick.Graph.AxisRangeOptions, $"{how}: the ranges");
            Assert.True(graph.AxisTickOptions == doubleClick.Graph.AxisTickOptions, $"{how}: the ticks");
            Assert.True(Png(graph.Frame, presented.Plot).AsSpan().SequenceEqual(Png(doubleClick.Graph.Frame, presented.Plot)), $"{how}: the drawing");
        }

        Assert.Equal(Png(start.Frame, presented.Plot), Png(doubleClick.Graph.Frame, presented.Plot));
    }

    private static GraphAxisTickOptions XTicks(GraphAxisTickOption option) => GraphAxisTickOptions.Default with { X = option };

    [Fact]
    public void ResetViewReturnsToAChosenRangeNotToAuto()
    {
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisRanges(XRange(14, 16));
        var controller = new GraphViewController(start);
        controller.ResetCommand.Execute(null);
        Assert.Same(start, controller.Graph);

        // Viewed at 14.8..15.2, the axis comes back to its chosen 14..16.
        Assert.True(controller.ZoomAt(GraphAxisField.X, Plot, new SKPoint(Plot.MidX, 400), Math.Log(5) / Math.Log(1.2)));
        Assert.Equal(14.8, controller.Graph.Frame.XAxis.Range.Minimum, 9);
        Assert.Equal(15.2, controller.Graph.Frame.XAxis.Range.Maximum, 9);

        controller.ResetCommand.Execute(null);

        Assert.Equal(new GraphAxisRange(14, 16), controller.Graph.Frame.XAxis.Range);
        Assert.Equal(XRange(14, 16), controller.Graph.AxisRangeOptions);
    }

    [Fact]
    public void ResetViewReturnsAnAutoRangeToAuto()
    {
        var presented = Present(GraphType.Histogram);
        var controller = Zoomed(presented.Graph);

        controller.ResetCommand.Execute(null);

        Assert.Equal(GraphAxisRangeOptions.Default, controller.Graph.AxisRangeOptions);
        Assert.Equal(presented.Graph.Frame.XAxis.Range, controller.Graph.Frame.XAxis.Range);
        Assert.Equal(presented.Graph.Frame.YAxis.Range, controller.Graph.Frame.YAxis.Range);
        Assert.Equal(Png(presented.Graph.Frame, presented.Plot), Png(controller.Graph.Frame, presented.Plot));
    }

    [Fact]
    public void ResetViewKeepsTheTicksTitlesLegendStatisticsAndAppearance()
    {
        var start = Present(GraphType.Histogram).Graph
            .WithAxisScale(GraphAxisRangeOptions.Default, new GraphAxisTickOptions(new GraphAxisTickOption.CustomValues([14.9, 15.0]), new GraphAxisTickOption.FixedInterval(5)))
            .WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("T"), GraphLabelOption.Custom("X"), GraphLabelOption.Hidden))
            .WithLegend(GraphLegendOptions.Default with { Position = GraphLegendPosition.Bottom })
            .WithStatistics(GraphStatisticsOptions.Default with { Mode = GraphStatisticsMode.Hide })
            .WithAppearance(GraphAppearanceOptions.Default with { GridMode = GraphGridMode.Hide });
        var controller = Zoomed(start);

        controller.ResetCommand.Execute(null);

        var reset = controller.Graph;
        Assert.Equal(
            (start.AxisTickOptions, start.LabelOptions, start.LegendOptions, start.StatisticsOptions, start.AppearanceOptions),
            (reset.AxisTickOptions, reset.LabelOptions, reset.LegendOptions, reset.StatisticsOptions, reset.AppearanceOptions));
        Assert.Same(start.BaseFrame, reset.BaseFrame);
        Assert.Equal(start.Frame.XAxis.Ticks.Select(tick => tick.Label), reset.Frame.XAxis.Ticks.Select(tick => tick.Label));
        Assert.Equal(start.Frame.Title, reset.Frame.Title);
    }

    // ---- The menu item ----

    [Fact]
    public void ResetViewIsLabelledAndBoundToHome()
    {
        var command = new RelayCommand(() => { });

        var item = GraphWindow.ResetViewItem(command);

        Assert.Equal("_Reset View", item.Header);
        Assert.Same(command, item.Command);
        Assert.Null(item.CommandParameter);
        Assert.Equal(new KeyGesture(Key.Home), item.InputGesture);
        Assert.Equal(KeyModifiers.None, GraphWindow.ResetViewGesture.KeyModifiers);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot, new[] { "_Copy Image", "_Reset View", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit A_ppearance..." })]
    [InlineData(GraphType.BoxPlot, new[] { "_Copy Image", "_Reset View", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Box Plot...", "Edit A_ppearance..." })]
    [InlineData(GraphType.Histogram, new[] { "_Copy Image", "_Reset View", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Statistics...", "Edit A_ppearance..." })]
    [InlineData(GraphType.ProbabilityPlot, new[] { "_Copy Image", "_Reset View", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Statistics...", "Edit A_ppearance..." })]
    [InlineData(GraphType.EmpiricalCdf, new[] { "_Copy Image", "_Reset View", "-", "_Edit Labels...", "Edit _Axes...", "Edit Le_gend...", "Edit _Statistics...", "Edit A_ppearance..." })]
    public void TheRightClickMenuOffersResetViewBesideCopyImage(GraphType type, string[] expected)
    {
        var commands = Enumerable.Range(0, 8).Select(_ => new RelayCommand(() => { })).ToArray();

        var items = GraphWindow.ContextMenuItems(
            GraphTypeDefinitions.For(type), commands[0], commands[1], commands[2], commands[3], commands[4], commands[5], commands[6], commands[7]);

        Assert.Equal(expected, items.Select(item => item is MenuItem menuItem ? (string)menuItem.Header! : "-"));
        Assert.Same(commands[7], ((MenuItem)items[1]).Command);
        Assert.Same(commands[0], ((MenuItem)items[0]).Command);
        Assert.Same(commands[6], ((MenuItem)items[^1]).Command);
        var keys = items.OfType<MenuItem>().Select(item => char.ToUpperInvariant(((string)item.Header!)[((string)item.Header!).IndexOf('_') + 1])).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }
}
