using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Views;

// A graph in its own window: it shows one presented graph and shares no state with the main window's worksheet grid, so
// a graph stays on screen while the user keeps working. Resizing, maximising and restoring only change the area the
// graph is laid out in.
//
// Its File menu copies the graph image to the clipboard (also Ctrl+C, in this window only, and right-click on the graph)
// and exports the graph. The window keeps what the graph is made of - the presentation and its plot renderer - so a
// copy or an export can draw it again off screen instead of copying what happens to be on the screen. How an export is
// put together is not its business: it asks the factory it was given for the workflow, with itself as the owner of the
// dialogs.
//
// Its labels can be changed after the graph is drawn: double-click a title the graph shows, or right-click and choose
// Edit Labels... (the only way back to a title that is hidden). Confirmed labels go onto the frame the graph had before
// its labels, without its data, and this same window then shows, copies and exports the new frame under the new title.
//
// Its axis ranges can be changed the same way (Task #043): right-click and choose Edit Axes.... Confirmed ranges go
// onto the frame the graph had before any range or label - its data, bins, fits and statistics untouched - and the
// window shows, copies and exports the frame over the new ranges.
//
// Its legend too (Task #044): right-click and choose Edit Legend... to show, hide or move it - unavailable for a graph
// without one. The window lays the new frame out again, legend and plot alike, without the data. Labels, ranges and
// the legend are edited on the one graph: a change to any of them keeps the others.
internal sealed class GraphWindow : Window
{
    // Comfortable for a first graph, and small enough for a 1280x720 screen.
    private const int DefaultWidth = 760;
    private const int DefaultHeight = 520;

    private readonly IGraphPlotRenderer? _plot;
    private readonly GraphCanvas _canvas;
    private readonly GraphExportController _export;
    private readonly GraphLabelEditController _labels;
    private readonly GraphAxesEditController _axes;
    private readonly GraphLegendEditController _legend;
    private readonly IAsyncRelayCommand _copyImage;

    // Ctrl+C copies the graph while this window is active. The binding belongs to this window alone, so the worksheet's
    // own Ctrl+C in the main window is untouched.
    internal static readonly KeyGesture CopyImageGesture = new(Key.C, KeyModifiers.Control);

    public GraphWindow(GraphPresentationState graph, IGraphPlotRenderer? plot, IGraphExportWorkflowFactory exports)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(exports);

        _plot = plot;
        _canvas = new GraphCanvas { Model = graph.Frame, Plot = plot };
        _export = exports.Create(this);
        _labels = new GraphLabelEditController(graph, new AvaloniaGraphLabelsDialog(this));
        _labels.GraphChanged += (_, _) => Show(_labels.Graph);
        _axes = new GraphAxesEditController(graph, new AvaloniaGraphAxesDialog(this));
        _axes.GraphChanged += (_, _) => Show(_axes.Graph);
        _legend = new GraphLegendEditController(graph, new AvaloniaGraphLegendDialog(this));
        _legend.GraphChanged += (_, _) => Show(_legend.Graph);

        // One command for the menu item, the shortcut and the right-click menu. It is not run again while a copy is still
        // in progress.
        _copyImage = new AsyncRelayCommand(() => _export.CopyImageAsync(Snapshot()));
        KeyBindings.Add(new KeyBinding { Gesture = CopyImageGesture, Command = _copyImage });
        var editLabels = new AsyncRelayCommand(() => _labels.EditAsync(focus: null));
        var editAxes = new AsyncRelayCommand(() => _axes.EditAsync());

        // A graph without a legend has none to edit: the item is there, but not available.
        var editLegend = new AsyncRelayCommand(() => _legend.EditAsync(), () => _legend.CanEdit);

        Title = WindowTitle(graph.Frame);
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var menu = new Menu { Items = { FileMenu() } };
        DockPanel.SetDock(menu, Dock.Top);
        var contextMenu = new ContextMenu
        {
            Items = { CopyImageItem(_copyImage), new Separator(), EditLabelsItem(editLabels) }
        };
        if (graph.Definition.Supports(GraphCapability.AxisRange))
        {
            contextMenu.Items.Add(EditAxesItem(editAxes));
        }

        if (graph.Definition.Supports(GraphCapability.Legend))
        {
            contextMenu.Items.Add(EditLegendItem(editLegend));
        }

        var graphArea = GraphArea(_canvas, contextMenu);
        graphArea.DoubleTapped += OnGraphAreaDoubleTapped;
        Content = new DockPanel { Children = { menu, graphArea } };
    }

    // The graph area as the user right-clicks and double-clicks it. The canvas draws through a custom operation that is
    // not hit-tested, so a transparent host takes the click; it adds no size and draws nothing. Wherever the
    // right-click lands, the menu acts on the whole graph.
    internal static Border GraphArea(GraphCanvas canvas, ContextMenu contextMenu) => new()
    {
        Background = Brushes.Transparent,
        Child = canvas,
        ContextMenu = contextMenu
    };

    // The one Copy Image item, made for File > Copy Image and for the right-click menu alike: both run the command that
    // Ctrl+C runs.
    internal static MenuItem CopyImageItem(ICommand copyImage) =>
        new() { Header = "_Copy Image", Command = copyImage, InputGesture = CopyImageGesture };

    // Right-click > Edit Labels...: every label of the graph, hidden ones included.
    internal static MenuItem EditLabelsItem(ICommand editLabels) =>
        new() { Header = "_Edit Labels...", Command = editLabels };

    // Right-click > Edit Axes...: the range of every axis the graph type lets the user choose.
    internal static MenuItem EditAxesItem(ICommand editAxes) =>
        new() { Header = "Edit _Axes...", Command = editAxes };

    // Right-click > Edit Legend...: shown, hidden, and on which side - unavailable for a graph without a legend.
    internal static MenuItem EditLegendItem(ICommand editLegend) =>
        new() { Header = "Edit Le_gend...", Command = editLegend };

    // What the window is called after the graph it shows: its title, or "Graph" when it has none.
    internal static string WindowTitle(GraphRenderModel frame) =>
        string.IsNullOrWhiteSpace(frame.Title) ? "Graph" : frame.Title;

    // A double-click on a title the graph shows edits the labels, opened on that title. Anywhere else - the plot, the
    // ticks, the legend, the statistics panel, a hidden title's empty place - it does nothing.
    private async void OnGraphAreaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_canvas.LabelAt(e.GetPosition(_canvas)) is not { } field)
        {
            return;
        }

        e.Handled = true;
        await _labels.EditAsync(field);
    }

    // The graph under its new labels or over its new axis ranges: drawn at once, named after its title, and the graph
    // both editors work on from now on.
    private void Show(GraphPresentationState graph)
    {
        _labels.Show(graph);
        _axes.Show(graph);
        _legend.Show(graph);
        _canvas.Model = graph.Frame;
        Title = WindowTitle(graph.Frame);
    }

    private MenuItem FileMenu()
    {
        var copy = CopyImageItem(_copyImage);

        var png = new MenuItem { Header = "Export _PNG..." };
        png.Click += async (_, _) => await _export.ExportPngAsync(Snapshot());

        var powerPoint = new MenuItem { Header = "Export Power_Point..." };
        powerPoint.Click += async (_, _) => await _export.ExportPowerPointAsync(Snapshot());

        var close = new MenuItem { Header = "_Close" };
        close.Click += (_, _) => Close();

        return new MenuItem
        {
            Header = "_File",
            Items = { copy, new Separator(), png, powerPoint, new Separator(), close }
        };
    }

    // What an export draws, fixed here on the UI thread: the graph as it is shown now - under the labels and over the
    // axis ranges last confirmed - and the theme it is being shown in. The export work that follows never looks at this
    // window again.
    private GraphExportSnapshot Snapshot() => new(_labels.Graph.Frame, _plot, _canvas.CurrentTheme);
}
