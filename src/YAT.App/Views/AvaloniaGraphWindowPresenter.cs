using Avalonia.Controls;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Views;

// Opens graphs as windows of the desktop application. The graph window is not modal: the user keeps working in the main
// window while it is open.
//
// The presenter carries the export workflow factory to every graph window it opens, so the windows never build their
// own export dependencies.
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

    public void ShowGraph(GraphRenderModel frame, IGraphPlotRenderer? plot)
    {
        ArgumentNullException.ThrowIfNull(frame);
        new GraphWindow(frame, plot, _exports).Show(_owner);
    }
}
