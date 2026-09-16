using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How a prepared graph reaches the screen. The desktop implementation opens a graph window; tests use a fake.
//
// The seam takes a render model, never a configuration or worksheet data: everything below the UI stays unaware of how
// a graph is drawn, and Task #027 only has to pass a real model instead of the sample one.
public interface IGraphWindowPresenter
{
    void ShowGraph(GraphRenderModel model);
}
