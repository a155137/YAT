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
// One axis is edited the same way from its own scale dialog (Task #052: double-click the axis): only that axis's range
// is replaced, the other's is kept, and the result goes through the same rules, the same fit to the automatic ranges and
// the same presentation path. Both dialogs edit the one range state, one edit at a time.
//
// It knows nothing of windows: the dialogs come through IGraphAxesDialog and IGraphAxisScaleDialog, so tests drive it
// without one.
public sealed class GraphAxesEditController
{
    private readonly IGraphAxesDialog _dialog;
    private readonly IGraphAxisScaleDialog? _scaleDialog;
    private bool _editing;

    public GraphAxesEditController(GraphPresentationState graph, IGraphAxesDialog dialog, IGraphAxisScaleDialog? scaleDialog = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dialog);

        Graph = graph;
        _dialog = dialog;
        _scaleDialog = scaleDialog;
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

    // Edits the range of one axis (Task #052). True when the user confirmed a range and the graph now shows it. An axis
    // the graph type gives no range - a box plot's categories - is never edited.
    public async Task<bool> EditScaleAsync(GraphAxisField axis)
    {
        if (_editing || _scaleDialog is null || !Graph.Definition.SupportsAxisRange(axis))
        {
            return false;
        }

        _editing = true;
        try
        {
            var edited = await _scaleDialog.EditAsync(Graph.Definition, axis, Graph.AxisRangeOptions, Graph.BaseFrame);
            if (edited is null)
            {
                return false;
            }

            var options = axis == GraphAxisField.X ? Graph.AxisRangeOptions with { X = edited } : Graph.AxisRangeOptions with { Y = edited };
            if (GraphConfigurationValidator.AxisRangeErrors(options, Graph.Definition).Count > 0
                || GraphAxisViewportBuilder.Conflicts(Graph.BaseFrame, Graph.Definition, options).Count > 0)
            {
                return false;
            }

            Graph = Graph.WithAxisRanges(options);
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

// The Edit X Scale / Edit Y Scale dialog of a drawn graph (Task #052): the range of one axis. Returns the range the user
// confirmed for that axis, or null when the edit was cancelled. current holds both axes' ranges as they are (the other
// axis is kept, and a range is checked with it); autoFrame is the graph's frame before any range was chosen.
public interface IGraphAxisScaleDialog
{
    Task<GraphAxisRangeOption?> EditAsync(
        GraphTypeDefinition definition,
        GraphAxisField axis,
        GraphAxisRangeOptions current,
        GraphRenderModel autoFrame);
}
