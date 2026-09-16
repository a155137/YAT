using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.TestDoubles;

// Records the graphs that would have been opened in a window, so the path from the Graph menu to a rendered graph can
// be tested without a window.
internal sealed class FakeGraphWindowPresenter : IGraphWindowPresenter
{
    public List<GraphRenderModel> Shown { get; } = [];

    public GraphRenderModel LastModel => Shown.Count > 0 ? Shown[^1] : throw new InvalidOperationException("No graph was shown.");

    public void ShowGraph(GraphRenderModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        Shown.Add(model);
    }
}
