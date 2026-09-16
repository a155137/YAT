using Avalonia.Controls;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Views;

// Opens graphs as windows of the desktop application. The graph window is not modal: the user keeps working in the main
// window while it is open.
public sealed class AvaloniaGraphWindowPresenter : IGraphWindowPresenter
{
    private readonly Window _owner;

    public AvaloniaGraphWindowPresenter(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public void ShowGraph(GraphRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        new GraphWindow(model).Show(_owner);
    }
}
