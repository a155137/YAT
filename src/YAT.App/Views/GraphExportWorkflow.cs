using Avalonia.Controls;
using YAT.app.Clipboard;
using YAT.app.Graphs.Export;

namespace YAT.app.Views;

// How a graph window gets the export workflow it offers in its File menu.
//
// The dialogs of an export belong to the window they are shown over, so the workflow can only be built once that window
// exists. The window therefore asks for it instead of assembling it: what an export is made of - the rendering service,
// the presentation exporter, the dialogs - is decided where everything else is wired up.
public interface IGraphExportWorkflowFactory
{
    GraphExportController Create(Window owner);
}

// The desktop workflow: the shared, window-independent parts are given to it once, and each graph window adds its own
// dialogs.
public sealed class AvaloniaGraphExportWorkflowFactory : IGraphExportWorkflowFactory
{
    private readonly GraphExportService _service;
    private readonly IPowerPointGraphExporter _powerPoint;

    public AvaloniaGraphExportWorkflowFactory(GraphExportService service, IPowerPointGraphExporter powerPoint)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(powerPoint);
        _service = service;
        _powerPoint = powerPoint;
    }

    public GraphExportController Create(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        return new GraphExportController(
            new AvaloniaGraphExportDialogs(owner), _service, _powerPoint, new AvaloniaGraphImageClipboard(owner));
    }
}
