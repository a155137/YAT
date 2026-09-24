using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How the legend of a drawn graph is edited (Task #044): shown or hidden, and on which side of the plot. The dialog is
// shown the options as they are; nothing changes until the user confirms, and a cancelled edit leaves the graph exactly
// as it was. Confirmed options are put on the frame the graph had before its legend, axis ranges and labels
// (GraphPresentationState.WithLegend), so no data is read and nothing is computed again - not the series, their
// order, colours or statistics - and Auto on the right brings back the legend as the graph type gave it. The window
// then lays out, shows, copies and exports the new frame.
//
// A graph without a legend (no groups, nothing drawn together) has nothing to edit. It knows nothing of windows: the
// dialog comes through IGraphLegendDialog, so tests drive it without one.
public sealed class GraphLegendEditController
{
    private readonly IGraphLegendDialog _dialog;
    private bool _editing;

    public GraphLegendEditController(GraphPresentationState graph, IGraphLegendDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dialog);

        Graph = graph;
        _dialog = dialog;
    }

    // The graph as it is shown now.
    public GraphPresentationState Graph { get; private set; }

    // Whether the graph has a legend to edit: the graph type offers one, and gave this graph one.
    public bool CanEdit => Graph.Definition.Supports(GraphCapability.Legend) && Graph.BaseFrame.Legend is not null;

    // Raised after confirmed options changed Graph.
    public event EventHandler? GraphChanged;

    // Edits the legend. True when the user confirmed options and the graph now shows them.
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
            var edited = await _dialog.EditAsync(Graph.LegendOptions);
            if (edited is null || !edited.IsValid)
            {
                return false;
            }

            Graph = Graph.WithLegend(edited);
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _editing = false;
        }
    }

    // The graph as it is shown now, after something else changed it (its labels or axis ranges, edited on their own).
    public void Show(GraphPresentationState graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
    }
}

// The Edit Legend dialog of a drawn graph. Returns the options the user confirmed, or null when the edit was cancelled.
public interface IGraphLegendDialog
{
    Task<GraphLegendOptions?> EditAsync(GraphLegendOptions current);
}
