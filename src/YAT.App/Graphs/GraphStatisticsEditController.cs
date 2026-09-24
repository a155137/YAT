using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How the statistics panel of a drawn graph is edited (Task #045): shown or hidden, and which of its statistics. The
// dialog is shown the options as they are; nothing changes until the user confirms, and a cancelled edit leaves the
// graph exactly as it was. Confirmed options are put on the frame the graph had before its statistics, legend, axis
// ranges and labels (GraphPresentationState.WithStatistics), whose panel was worked out whole when the graph was
// prepared - so no data is read and nothing is computed again, not the statistics, the series, their order or colours,
// and a panel hidden when the graph was set up can be shown now. The window then lays out, shows, copies and exports
// the new frame.
//
// A graph without a panel (none was worked out for it) has nothing to edit. It knows nothing of windows: the dialog
// comes through IGraphStatisticsDialog, so tests drive it without one.
public sealed class GraphStatisticsEditController
{
    private readonly IGraphStatisticsDialog _dialog;
    private bool _editing;

    public GraphStatisticsEditController(GraphPresentationState graph, IGraphStatisticsDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dialog);

        Graph = graph;
        _dialog = dialog;
    }

    // The graph as it is shown now.
    public GraphPresentationState Graph { get; private set; }

    // Whether the graph has a statistics panel to edit: the graph type offers one, and one was worked out for this
    // graph.
    public bool CanEdit =>
        Graph.Definition.Supports(GraphCapability.StatisticsPanel) && Graph.BaseFrame.StatisticsPanel is not null;

    // Raised after confirmed options changed Graph.
    public event EventHandler? GraphChanged;

    // Edits the statistics. True when the user confirmed options and the graph now shows them.
    public async Task<bool> EditAsync()
    {
        // One edit at a time: the dialog is modal, and a second request while it is open is not a second dialog.
        if (_editing || !CanEdit)
        {
            return false;
        }

        _editing = true;
        try
        {
            var edited = await _dialog.EditAsync(Graph.Definition, Graph.StatisticsOptions);
            if (edited is null || !edited.IsValid)
            {
                return false;
            }

            Graph = Graph.WithStatistics(edited);
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _editing = false;
        }
    }

    // The graph as it is shown now, after something else changed it (its labels, axis ranges or legend, edited on
    // their own).
    public void Show(GraphPresentationState graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
    }
}

// The Edit Statistics dialog of a drawn graph. Returns the options the user confirmed, or null when the edit was
// cancelled.
public interface IGraphStatisticsDialog
{
    Task<GraphStatisticsOptions?> EditAsync(GraphTypeDefinition definition, GraphStatisticsOptions current);
}
