using Avalonia;
using Avalonia.Controls;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Views;

// Opens graphs as windows of the desktop application. The graph window is not modal: the user keeps working in the main
// window while it is open.
//
// The presenter carries the export workflow factory to every graph window it opens, so the windows never build their
// own export dependencies.
//
// The windows of one request - a graph for each of several variables - open one step apart, down and to the right, so
// each can be seen and picked; the first opens where a graph window always opens.
public sealed class AvaloniaGraphWindowPresenter : IGraphWindowPresenter
{
    private readonly Window _owner;
    private readonly IGraphExportWorkflowFactory _exports;

    public AvaloniaGraphWindowPresenter(Window owner, IGraphExportWorkflowFactory exports)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(exports);
        _owner = owner;
        _exports = exports;
    }

    // How far each further window of a request opens from the one before it, in layout units.
    internal const int CascadeStep = 24;

    public void ShowGraph(GraphPresentationState graph, IGraphPlotRenderer? plot, int cascade = 0)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentOutOfRangeException.ThrowIfNegative(cascade);

        var window = new GraphWindow(graph, plot, _exports);
        if (cascade > 0)
        {
            // Placed first where every graph window is placed (centred on its owner), then moved along.
            window.Opened += (_, _) =>
            {
                var step = (int)Math.Round(cascade * CascadeStep * window.DesktopScaling);
                window.Position = new PixelPoint(window.Position.X + step, window.Position.Y + step);
            };
        }

        window.Show(_owner);
    }
}
