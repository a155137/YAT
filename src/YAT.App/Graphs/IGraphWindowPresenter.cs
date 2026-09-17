using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How a prepared graph reaches the screen. The desktop implementation opens a graph window; tests use a fake.
//
// The seam takes what is drawn - the shared frame and the graph type's own plot renderer - never a configuration or
// worksheet data: everything below the UI stays unaware of how a graph is drawn.
public interface IGraphWindowPresenter
{
    void ShowGraph(GraphRenderModel frame, IGraphPlotRenderer? plot);
}
