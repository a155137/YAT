using Avalonia.Controls;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Views;

// A graph in its own window: it shows one render model and shares no state with the main window's worksheet grid, so a
// graph stays on screen while the user keeps working. Resizing, maximising and restoring only change the area the graph
// is laid out in.
internal sealed class GraphWindow : Window
{
    // Comfortable for a first graph, and small enough for a 1280x720 screen.
    private const int DefaultWidth = 760;
    private const int DefaultHeight = 520;

    public GraphWindow(GraphRenderModel model, IGraphPlotRenderer? plot = null)
    {
        ArgumentNullException.ThrowIfNull(model);

        Title = string.IsNullOrWhiteSpace(model.Title) ? "Graph" : model.Title;
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = 320;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = new GraphCanvas { Model = model, Plot = plot };
    }
}
