using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Views;

// A graph in its own window: it shows one render model and shares no state with the main window's worksheet grid, so a
// graph stays on screen while the user keeps working. Resizing, maximising and restoring only change the area the graph
// is laid out in.
//
// Its File menu copies the graph image to the clipboard (also Ctrl+C, in this window only) and exports the graph. The
// window keeps what the graph is made of - the frame and its plot renderer - so a copy or an export can draw it again off
// screen instead of copying what happens to be on the screen. How an export is put
// together is not its business: it asks the factory it was given for the workflow, with itself as the owner of the
// dialogs.
internal sealed class GraphWindow : Window
{
    // Comfortable for a first graph, and small enough for a 1280x720 screen.
    private const int DefaultWidth = 760;
    private const int DefaultHeight = 520;

    private readonly GraphRenderModel _model;
    private readonly IGraphPlotRenderer? _plot;
    private readonly GraphCanvas _canvas;
    private readonly GraphExportController _export;
    private readonly IAsyncRelayCommand _copyImage;

    // Ctrl+C copies the graph while this window is active. The binding belongs to this window alone, so the worksheet's
    // own Ctrl+C in the main window is untouched.
    internal static readonly KeyGesture CopyImageGesture = new(Key.C, KeyModifiers.Control);

    public GraphWindow(GraphRenderModel model, IGraphPlotRenderer? plot, IGraphExportWorkflowFactory exports)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(exports);

        _model = model;
        _plot = plot;
        _canvas = new GraphCanvas { Model = model, Plot = plot };
        _export = exports.Create(this);

        // One command for the menu item and the shortcut. It is not run again while a copy is still in progress.
        _copyImage = new AsyncRelayCommand(() => _export.CopyImageAsync(Snapshot()));
        KeyBindings.Add(new KeyBinding { Gesture = CopyImageGesture, Command = _copyImage });

        Title = string.IsNullOrWhiteSpace(model.Title) ? "Graph" : model.Title;
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var menu = new Menu { Items = { FileMenu() } };
        DockPanel.SetDock(menu, Dock.Top);
        Content = new DockPanel { Children = { menu, _canvas } };
    }

    private MenuItem FileMenu()
    {
        var copy = new MenuItem { Header = "_Copy Image", Command = _copyImage, InputGesture = CopyImageGesture };

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

    // What an export draws, fixed here on the UI thread: the graph and the theme it is being shown in. The export work
    // that follows never looks at this window again.
    private GraphExportSnapshot Snapshot() => new(_model, _plot, _canvas.CurrentTheme);
}
