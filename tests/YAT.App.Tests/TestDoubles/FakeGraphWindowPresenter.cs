using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.TestDoubles;

// Records the graphs that would have been opened in a window, so the path from the Graph menu to a rendered graph can
// be tested without a window: the frame each window would show first, and the presentation it would keep.
internal sealed class FakeGraphWindowPresenter : IGraphWindowPresenter
{
    public List<(GraphRenderModel Frame, IGraphPlotRenderer? Plot)> Shown { get; } = [];

    public List<GraphPresentationState> Graphs { get; } = [];

    // Where each shown window stood among the windows of its request.
    public List<int> Cascades { get; } = [];

    // The panels each shown window would draw (Task #058); null for a graph of one plot.
    public List<IReadOnlyList<GraphPanel>?> Panels { get; } = [];

    public (GraphRenderModel Frame, IGraphPlotRenderer? Plot) Last =>
        Shown.Count > 0 ? Shown[^1] : throw new InvalidOperationException("No graph was shown.");

    public void ShowGraph(GraphPresentationState graph, IGraphPlotRenderer? plot, int cascade = 0, IReadOnlyList<GraphPanel>? panels = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graphs.Add(graph);
        Cascades.Add(cascade);
        Panels.Add(panels);
        Shown.Add((graph.Frame, plot));
    }
}
