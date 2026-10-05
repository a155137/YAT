using Avalonia;
using Avalonia.Controls;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// Opens graphs as windows of the desktop application. The graph window is not modal: the user keeps working in the main
// window while it is open.
//
// The presenter carries the export workflow factory to every graph window it opens, so the windows never build their
// own export dependencies.
//
// The windows of one request - a graph for each of several variables - open one step apart, down and to the right, so
// each can be seen and picked; the first opens where a graph window always opens.
//
// Every window it opens is listed in the Graphs list (Task #061) while it is open: under its title as it changes, marked while
// it is the graph last activated, and brought back - restored, moved into view, activated - when it is chosen there.
public sealed class AvaloniaGraphWindowPresenter : IGraphWindowPresenter
{
    private readonly Window _owner;
    private readonly IGraphExportWorkflowFactory _exports;
    private readonly IGraphPaletteLibraryAccess? _palettes;
    private readonly OpenGraphsViewModel? _openGraphs;

    // palettes: the user's palettes, offered by every graph window's Edit Appearance... (Task #050).
    // openGraphs: the Graphs list the windows are listed in (Task #061); none lists them nowhere.
    public AvaloniaGraphWindowPresenter(
        Window owner,
        IGraphExportWorkflowFactory exports,
        IGraphPaletteLibraryAccess? palettes = null,
        OpenGraphsViewModel? openGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(exports);
        _owner = owner;
        _exports = exports;
        _palettes = palettes;
        _openGraphs = openGraphs;
    }

    // How far each further window of a request opens from the one before it, in layout units.
    internal const int CascadeStep = 24;

    public void ShowGraph(GraphPresentationState graph, IGraphPlotRenderer? plot, int cascade = 0, IReadOnlyList<GraphPanel>? panels = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfNegative(cascade);

        var window = new GraphWindow(graph, plot, _exports, _palettes, panels);
        if (cascade > 0)
        {
            // Placed first where every graph window is placed (centred on its owner), then moved along.
            window.Opened += (_, _) =>
            {
                var step = (int)Math.Round(cascade * CascadeStep * window.DesktopScaling);
                window.Position = new PixelPoint(window.Position.X + step, window.Position.Y + step);
            };
        }

        if (_openGraphs is { } openGraphs)
        {
            List(openGraphs, window);
        }

        window.Show(_owner);
    }

    // Listed from now until it closes, under its title as it changes and marked whenever it is activated.
    private static void List(OpenGraphsViewModel openGraphs, Window window)
    {
        var item = openGraphs.Add(Title(window), () => BringToFront(window));
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.TitleProperty)
            {
                openGraphs.Rename(item, Title(window));
            }
        };
        window.Activated += (_, _) => openGraphs.MarkActive(item);
        window.Closed += (_, _) => openGraphs.Remove(item);
    }

    private static string Title(Window window) => window.Title ?? string.Empty;

    // A window chosen in the Graphs list: restored if minimized, moved into its screen's working area if it lies beyond it
    // (GraphWindowPlacement), then activated - in that order, since activating alone leaves a minimized window minimized
    // and an off-screen one out of sight.
    internal static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        if (window.WindowState == WindowState.Normal
            && (window.Screens.ScreenFromWindow(window) ?? (window.Owner is WindowBase owner ? window.Screens.ScreenFromWindow(owner) : null) ?? window.Screens.Primary) is { } screen)
        {
            var size = PixelSize.FromSize(window.FrameSize ?? window.ClientSize, window.DesktopScaling);
            var position = GraphWindowPlacement.IntoView(new PixelRect(window.Position, size), screen.WorkingArea);
            if (position != window.Position)
            {
                window.Position = position;
            }
        }

        window.Activate();
    }
}
