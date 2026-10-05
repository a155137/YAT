using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
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
// Or one axis at a time (Task #052): double-click it - its ticks, tick labels or line - for its Edit X Scale or Edit Y
// Scale dialog, which edits the same ranges.
//
// Its legend too (Task #044): right-click and choose Edit Legend... to show, hide or move it - unavailable for a graph
// without one. The window lays the new frame out again, legend and plot alike, without the data.
//
// And its statistics panel (Task #045): right-click and choose Edit Statistics... to show or hide it or choose its
// statistics - offered by the graph types that have a panel, and unavailable for a graph without one. The panel was
// worked out whole with the graph, so even one hidden from the start is shown without the data.
//
// And its appearance (Task #046): right-click and choose Edit Appearance... to change its series colours, its grid and
// its backgrounds. Those change only the theme the graph is drawn in - the canvas resolves it, and every copy and
// export takes it from there - never the frame. Labels, ranges, the legend, the statistics and the appearance are
// edited on the one graph: a change to any of them keeps the others.
//
// And a box plot's own options (Task #047): right-click and choose Edit Box Plot... to change its box width or to mark
// its means and outliers or not. Those change only the plot renderer the window draws, copies and exports with - the
// same boxes under the same presented graph - so they and the graph's other edits keep each other too.
//
// And it can be zoomed and panned (Task #055): the mouse wheel zooms about the cursor - in the plot on every axis the graph
// type lets the user navigate (a box plot's Y only), over an axis on that axis alone - and a left-button drag in the plot
// pans it; a double-click in the plot is Reset View, back to the configured ranges. So are right-click > Reset View and
// the Home key (Task #056), both available while the graph is zoomed or panned; all three run the one Reset. The view is
// the graph's like everything above: shown, copied and exported, kept by every other edit, and never its ranges or its
// ticks. The title, the legend, the statistics panel and the reference line labels are never navigated, and right-click
// keeps its menu.
internal sealed class GraphWindow : Window
{
    // Comfortable for a first graph, and small enough for a 1280x720 screen.
    private const int DefaultWidth = 760;
    private const int DefaultHeight = 520;

    // The size of a graph window of seven panels or more (Task #058): still within a 1280x720 screen.
    private const int LargePanelCount = 7;
    private const int LargeWidth = 1000;
    private const int LargeHeight = 680;

    private IGraphPlotRenderer? _plot;

    // The panels of a graph drawn in panels (Task #058), fixed for the window's life; null for a graph of one plot.
    private readonly IReadOnlyList<GraphPanel>? _panels;
    private readonly GraphCanvas _canvas;
    private readonly GraphExportController _export;
    private readonly GraphLabelEditController _labels;
    private readonly GraphAxesEditController _axes;
    private readonly GraphViewController _view;

    // Where a left-button press in the plot was, and the plot area it was measured against, until it becomes a pan or
    // is released (Task #055).
    private (SKRect PlotArea, SKPoint From)? _press;
    private readonly GraphLegendEditController _legend;
    private readonly GraphStatisticsEditController _statistics;
    private readonly GraphAppearanceEditController _appearance;
    private readonly GraphBoxPlotEditController _boxPlot;
    private readonly IAsyncRelayCommand _copyImage;

    // Ctrl+C copies the graph while this window is active. The binding belongs to this window alone, so the worksheet's
    // own Ctrl+C in the main window is untouched.
    internal static readonly KeyGesture CopyImageGesture = new(Key.C, KeyModifiers.Control);

    // Home is Reset View (Task #056) while this window is active: no modifier, nothing else in the window uses it.
    internal static readonly KeyGesture ResetViewGesture = new(Key.Home);

    // palettes: the user's palettes for Edit Appearance... (Task #050) - their choices and the Palette Manager, never
    // where they are kept. A graph takes a palette's colours, never the palette, so nothing the library does reaches it.
    public GraphWindow(
        GraphPresentationState graph,
        IGraphPlotRenderer? plot,
        IGraphExportWorkflowFactory exports,
        IGraphPaletteLibraryAccess? palettes = null,
        IReadOnlyList<GraphPanel>? panels = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(exports);

        _plot = plot;
        _panels = panels is { Count: > 0 } ? panels : null;
        _canvas = new GraphCanvas { Model = graph.Frame, Plot = plot, Panels = _panels, Appearance = graph.AppearanceOptions };
        _export = exports.Create(this);
        _labels = new GraphLabelEditController(graph, new AvaloniaGraphLabelsDialog(this));
        _labels.GraphChanged += (_, _) => Show(_labels.Graph);
        _axes = new GraphAxesEditController(graph, new AvaloniaGraphAxesDialog(this), new AvaloniaGraphAxisScaleDialog(this));
        _axes.GraphChanged += (_, _) => Show(_axes.Graph);
        _view = new GraphViewController(graph);
        _view.GraphChanged += (_, _) => Show(_view.Graph);
        _legend = new GraphLegendEditController(graph, new AvaloniaGraphLegendDialog(this));
        _legend.GraphChanged += (_, _) => Show(_legend.Graph);
        _statistics = new GraphStatisticsEditController(graph, new AvaloniaGraphStatisticsDialog(this));
        _statistics.GraphChanged += (_, _) => Show(_statistics.Graph);
        _appearance = new GraphAppearanceEditController(graph, new AvaloniaGraphAppearanceDialog(this, palettes));
        _appearance.GraphChanged += (_, _) => Show(_appearance.Graph);

        // A box plot's own options change the plot it is drawn by, not the presented graph (Task #047).
        _boxPlot = new GraphBoxPlotEditController(graph.Definition, plot, new AvaloniaGraphBoxPlotDialog(this));
        _boxPlot.PlotChanged += (_, _) => ShowPlot(_boxPlot.Plot);

        // One command for the menu item, the shortcut and the right-click menu. It is not run again while a copy is still
        // in progress.
        _copyImage = new AsyncRelayCommand(() => _export.CopyImageAsync(Snapshot()));
        KeyBindings.Add(new KeyBinding { Gesture = CopyImageGesture, Command = _copyImage });
        KeyBindings.Add(new KeyBinding { Gesture = ResetViewGesture, Command = _view.ResetCommand });
        var editLabels = new AsyncRelayCommand(() => _labels.EditAsync(focus: null));
        var editAxes = new AsyncRelayCommand(() => _axes.EditAsync());

        // A graph without a legend has none to edit: the item is there, but not available.
        var editLegend = new AsyncRelayCommand(() => _legend.EditAsync(), () => _legend.CanEdit);

        // Likewise a graph without a statistics panel.
        var editStatistics = new AsyncRelayCommand(() => _statistics.EditAsync(), () => _statistics.CanEdit);
        var editBoxPlot = new AsyncRelayCommand(() => _boxPlot.EditAsync(), () => _boxPlot.CanEdit);
        var editAppearance = new AsyncRelayCommand(() => _appearance.EditAsync(), () => _appearance.CanEdit);

        Title = WindowTitle(graph.Frame);
        // Seven panels or more are a 3x3 grid, which needs more room than one graph to stay readable.
        var large = _panels is { Count: >= LargePanelCount };
        Width = large ? LargeWidth : DefaultWidth;
        Height = large ? LargeHeight : DefaultHeight;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var menu = new Menu { Items = { FileMenu() } };
        DockPanel.SetDock(menu, Dock.Top);
        var contextMenu = new ContextMenu();
        foreach (var item in ContextMenuItems(
            graph.Definition, _copyImage, editLabels, editAxes, editLegend, editStatistics, editBoxPlot, editAppearance, _view.ResetCommand))
        {
            contextMenu.Items.Add(item);
        }

        var graphArea = GraphArea(_canvas, contextMenu);
        graphArea.DoubleTapped += OnGraphAreaDoubleTapped;
        graphArea.PointerWheelChanged += OnGraphAreaWheel;
        graphArea.PointerPressed += OnGraphAreaPressed;
        graphArea.PointerMoved += OnGraphAreaMoved;
        graphArea.PointerReleased += OnGraphAreaReleased;
        graphArea.PointerCaptureLost += (_, _) => EndPan();
        _canvas.SizeChanged += (_, _) =>
        {
            if (_canvas.PlotArea is { } plotArea)
            {
                _view.Resize(plotArea);
            }
        };
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

    // The right-click menu of a graph of this type, in order: Copy Image and - for a graph the user can zoom and pan -
    // Reset View (Task #056), then the editors the graph type offers - labels, axes, legend, statistics, box plot,
    // appearance.
    internal static IReadOnlyList<Control> ContextMenuItems(
        GraphTypeDefinition definition,
        ICommand copyImage,
        ICommand editLabels,
        ICommand editAxes,
        ICommand editLegend,
        ICommand editStatistics,
        ICommand editBoxPlot,
        ICommand editAppearance,
        ICommand? resetView = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        List<Control> items = [CopyImageItem(copyImage)];
        if (resetView is not null && definition.Supports(GraphCapability.AxisRange))
        {
            items.Add(ResetViewItem(resetView));
        }

        items.AddRange([new Separator(), EditLabelsItem(editLabels)]);
        if (definition.Supports(GraphCapability.AxisRange))
        {
            items.Add(EditAxesItem(editAxes));
        }

        if (definition.Supports(GraphCapability.Legend))
        {
            items.Add(EditLegendItem(editLegend));
        }

        if (definition.Supports(GraphCapability.StatisticsPanel))
        {
            items.Add(EditStatisticsItem(editStatistics));
        }

        if (definition.Supports(GraphCapability.BoxPlotControls))
        {
            items.Add(EditBoxPlotItem(editBoxPlot));
        }

        if (definition.Supports(GraphCapability.Appearance))
        {
            items.Add(EditAppearanceItem(editAppearance));
        }

        return items;
    }

    // The one Copy Image item, made for File > Copy Image and for the right-click menu alike: both run the command that
    // Ctrl+C runs.
    internal static MenuItem CopyImageItem(ICommand copyImage) =>
        new() { Header = "_Copy Image", Command = copyImage, InputGesture = CopyImageGesture };

    // Reset View (Task #056): back to the configured ranges, as a double-click in the plot is; unavailable while the graph
    // is not zoomed or panned, which the command it runs says.
    internal static MenuItem ResetViewItem(ICommand resetView) =>
        new() { Header = "_Reset View", Command = resetView, InputGesture = ResetViewGesture };

    // Right-click > Edit Labels...: every label of the graph, hidden ones included.
    internal static MenuItem EditLabelsItem(ICommand editLabels) =>
        new() { Header = "_Edit Labels...", Command = editLabels };

    // Right-click > Edit Axes...: the range of every axis the graph type lets the user choose.
    internal static MenuItem EditAxesItem(ICommand editAxes) =>
        new() { Header = "Edit _Axes...", Command = editAxes };

    // Right-click > Edit Legend...: shown, hidden, and on which side - unavailable for a graph without a legend.
    internal static MenuItem EditLegendItem(ICommand editLegend) =>
        new() { Header = "Edit Le_gend...", Command = editLegend };

    // Right-click > Edit Statistics...: shown or hidden, and which statistics - unavailable for a graph without a
    // statistics panel.
    internal static MenuItem EditStatisticsItem(ICommand editStatistics) =>
        new() { Header = "Edit _Statistics...", Command = editStatistics };

    // Right-click > Edit Box Plot...: a box plot's box width, and whether its means and outliers are marked.
    internal static MenuItem EditBoxPlotItem(ICommand editBoxPlot) =>
        new() { Header = "Edit _Box Plot...", Command = editBoxPlot };

    // Right-click > Edit Appearance...: the series colours, the grid and the backgrounds - every graph type has them.
    internal static MenuItem EditAppearanceItem(ICommand editAppearance) =>
        new() { Header = "Edit A_ppearance...", Command = editAppearance };

    // What the window is called after the graph it shows: its title, or "Graph" when it has none.
    internal static string WindowTitle(GraphRenderModel frame) =>
        string.IsNullOrWhiteSpace(frame.Title) ? "Graph" : frame.Title;

    // A double-click on a title the graph shows edits the labels, opened on that title. Elsewhere on an axis - its ticks,
    // its tick labels, its line - it edits that axis's scale (Task #052), when the graph type gives the axis a range: not
    // a box plot's categories. In the plot's body it is Reset View (Task #055). Anywhere else - the legend, the
    // statistics panel, the reference line labels, a hidden title's empty place - it does nothing.
    private async void OnGraphAreaDoubleTapped(object? sender, TappedEventArgs e)
    {
        EndPan();
        var point = e.GetPosition(_canvas);
        var label = _canvas.LabelAt(point);
        var axis = label is null ? _canvas.AxisAt(point) : null;
        switch (DoubleClickTarget(label, axis, _axes.Graph.Definition))
        {
            case (GraphLabelField field, _):
                e.Handled = true;
                await _labels.EditAsync(field);
                break;
            case (null, GraphAxisField picked):
                e.Handled = true;
                await _axes.EditScaleAsync(picked);
                break;
            default:
                if (_canvas.InPlot(point))
                {
                    e.Handled = true;
                    _view.Reset();
                }

                break;
        }
    }

    // What a wheel step zooms (Task #055): nothing over a title; over an axis the graph type lets the user navigate, that
    // axis alone; in the plot's body - including where an axis that cannot be navigated reaches into it - every axis that
    // can be; anywhere else nothing.
    internal static (bool Zooms, GraphAxisField? Axis) WheelTarget(
        GraphLabelField? label,
        GraphAxisField? axis,
        bool inPlot,
        GraphTypeDefinition definition) =>
        label is not null ? (false, null)
        : axis is { } picked && definition.SupportsAxisRange(picked) ? (true, picked)
        : inPlot ? (true, null)
        : (false, null);

    private void OnGraphAreaWheel(object? sender, PointerWheelEventArgs e)
    {
        var point = e.GetPosition(_canvas);
        var label = _canvas.LabelAt(point);
        var (zooms, axis) = WheelTarget(label, label is null ? _canvas.AxisAt(point) : null, _canvas.InPlot(point), _view.Graph.Definition);
        if (zooms && _canvas.PlotAreaAt(point) is { } plotArea)
        {
            e.Handled = true;
            _view.ZoomAt(axis, plotArea, new SKPoint((float)point.X, (float)point.Y), e.Delta.Y);
        }
    }

    // A left-button press in the plot's body may start a pan; it does once the pointer has moved past the drag threshold,
    // so a click or a double-click never moves the graph. Other buttons - the right-click menu - are left alone.
    private void OnGraphAreaPressed(object? sender, PointerPressedEventArgs e)
    {
        EndPan();
        var point = e.GetPosition(_canvas);
        if (e.GetCurrentPoint(_canvas).Properties.IsLeftButtonPressed && _canvas.InPlot(point) && _canvas.PlotAreaAt(point) is { } plotArea)
        {
            _press = (plotArea, new SKPoint((float)point.X, (float)point.Y));
        }
    }

    // The pan follows the pointer from where the drag began, wherever the pointer goes once it is captured.
    private void OnGraphAreaMoved(object? sender, PointerEventArgs e)
    {
        if (_press is not { } press)
        {
            return;
        }

        var position = e.GetPosition(_canvas);
        var point = new SKPoint((float)position.X, (float)position.Y);
        if (!_view.IsPanning)
        {
            if (!GraphViewNavigator.IsDrag(press.From, point))
            {
                return;
            }

            e.Pointer.Capture(sender as IInputElement);
            _view.BeginPan(press.PlotArea, press.From);
        }

        e.Handled = true;
        _view.PanTo(point);
    }

    private void OnGraphAreaReleased(object? sender, PointerReleasedEventArgs e)
    {
        var panned = _view.IsPanning;
        EndPan();
        if (panned)
        {
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    // A pan ends where the pointer was released, or lost: the graph stays as it was moved.
    private void EndPan()
    {
        _press = null;
        _view.EndPan();
    }

    // What a double-click edits: the title under it first; else the axis under it, when the graph type gives that axis a
    // range; else nothing.
    internal static (GraphLabelField? Label, GraphAxisField? Axis) DoubleClickTarget(
        GraphLabelField? label,
        GraphAxisField? axis,
        GraphTypeDefinition definition) =>
        label is { } field ? (field, null)
        : axis is { } picked && definition.SupportsAxisRange(picked) ? (null, picked)
        : (null, null);

    // The graph under its new labels, over its new axis ranges, or with its new legend, statistics or appearance:
    // drawn at once, named after its title, and the graph every editor works on from now on.
    private void Show(GraphPresentationState graph)
    {
        _labels.Show(graph);
        _axes.Show(graph);
        _view.Show(graph);
        _legend.Show(graph);
        _statistics.Show(graph);
        _appearance.Show(graph);
        _canvas.Model = graph.Frame;
        _canvas.Appearance = graph.AppearanceOptions;
        Title = WindowTitle(graph.Frame);
    }

    // The graph's plot drawn by another renderer - a box plot with other options - under the same presented graph:
    // drawn at once, and what every copy and export draws from now on.
    private void ShowPlot(IGraphPlotRenderer? plot)
    {
        _plot = plot;
        _canvas.Plot = plot;
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
    private GraphExportSnapshot Snapshot() => new(_labels.Graph.Frame, _plot, _canvas.CurrentTheme) { Panels = _panels };
}
