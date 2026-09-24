using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How a prepared graph reaches the screen. The desktop implementation opens a graph window; tests use a fake.
//
// The seam takes what is drawn - the presented graph and the graph type's own plot renderer - never a configuration or
// worksheet data: everything below the UI stays unaware of how a graph is drawn. The presentation keeps the frame from
// before the labels, so the window can change the labels of the graph it shows without the data.
public interface IGraphWindowPresenter
{
    // cascade: where the window stands among the windows of one request - 0 for the first (or only) one, which opens
    // where a graph window always opens; each later one opens a step further down and to the right.
    void ShowGraph(GraphPresentationState graph, IGraphPlotRenderer? plot, int cascade = 0);
}
