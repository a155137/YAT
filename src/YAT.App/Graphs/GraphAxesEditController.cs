using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How the axis ranges of a drawn graph are edited (Task #043). The dialog is shown the ranges as they are and the
// graph's automatic ranges beside them; nothing changes until the user confirms, and a cancelled edit leaves the graph
// exactly as it was. Confirmed ranges are put on the frame the graph had before any range or label
// (GraphPresentationState.WithAxisRanges), so no data is read and nothing is computed again - not the bins, the fits or
// the statistics - and leaving every range blank again brings back the automatic ranges exactly. The window then shows,
// copies and exports the new frame.
//
// It knows nothing of windows: the dialog comes through IGraphAxesDialog, so tests drive it without one.
public sealed class GraphAxesEditController
{
    private readonly IGraphAxesDialog _dialog;
    private bool _editing;

    public GraphAxesEditController(GraphPresentationState graph, IGraphAxesDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dialog);

        Graph = graph;
        _dialog = dialog;
    }

    // The graph as it is shown now.
    public GraphPresentationState Graph { get; private set; }

    // Raised after confirmed ranges changed Graph.
    public event EventHandler? GraphChanged;

    // Edits the axis ranges. True when the user confirmed ranges and the graph now shows them.
    public async Task<bool> EditAsync()
    {
        // One edit at a time: the dialog is modal, and a second request while it is open is not a second dialog.
        if (_editing || !Graph.Definition.Supports(GraphCapability.AxisRange))
        {
            return false;
        }

        _editing = true;
        try
        {
            var edited = await _dialog.EditAsync(Graph.Definition, Graph.AxisRangeOptions, Graph.BaseFrame);
            if (edited is null
                || GraphConfigurationValidator.AxisRangeErrors(edited, Graph.Definition).Count > 0
                || GraphAxisViewportBuilder.Conflicts(Graph.BaseFrame, Graph.Definition, edited).Count > 0)
            {
                return false;
            }

            Graph = Graph.WithAxisRanges(edited);
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _editing = false;
        }
    }

    // The graph as it is shown now, after something else changed it (its labels, edited on their own).
    public void Show(GraphPresentationState graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
    }
}

// The Edit Axes dialog of a drawn graph. Returns the ranges the user confirmed - which the axis range rules accept and
// which fit the graph's automatic ranges - or null when the edit was cancelled. autoFrame is the graph's frame before
// any range was chosen: what Auto shows, and what a range with one end chosen is checked against.
public interface IGraphAxesDialog
{
    Task<GraphAxisRangeOptions?> EditAsync(
        GraphTypeDefinition definition,
        GraphAxisRangeOptions current,
        GraphRenderModel autoFrame);
}
