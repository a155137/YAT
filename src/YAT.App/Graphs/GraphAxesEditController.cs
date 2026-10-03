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
// The scale dialog also marks its axis (Task #054): Auto ticks, an interval or values of the user's own, confirmed with
// its range by the same OK (GraphPresentationState.WithAxisScale). Ranges and ticks are kept apart: the Edit Axes dialog
// changes ranges only and keeps every axis's ticks - and refuses a range an interval of them would draw too many ticks
// over - and the scale dialog keeps the other axis's range and ticks.
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
            var edited = await _dialog.EditAsync(Graph.Definition, Graph.AxisRangeOptions, Graph.BaseFrame, Graph.AxisTickOptions);
            if (edited is null || !Usable(edited, Graph.AxisTickOptions))
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
            var edited = await _scaleDialog.EditAsync(Graph.Definition, axis, Graph.AxisRangeOptions, Graph.AxisTickOptions, Graph.BaseFrame);
            if (edited is null)
            {
                return false;
            }

            var options = axis == GraphAxisField.X ? Graph.AxisRangeOptions with { X = edited.Range } : Graph.AxisRangeOptions with { Y = edited.Range };
            var ticks = Graph.AxisTickOptions.With(axis, edited.Ticks);
            if (!Usable(options, ticks))
            {
                return false;
            }

            Graph = Graph.WithAxisScale(options, ticks);
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

    // Whether the graph can be shown over these ranges and marked with these ticks: the range rules, the fit to the
    // automatic ends, then the ticks over the ranges that makes.
    private bool Usable(GraphAxisRangeOptions ranges, GraphAxisTickOptions ticks) =>
        GraphConfigurationValidator.AxisRangeErrors(ranges, Graph.Definition).Count == 0
        && GraphAxisViewportBuilder.Conflicts(Graph.BaseFrame, Graph.Definition, ranges).Count == 0
        && GraphAxisTickBuilder.Problems(Graph.BaseFrame, Graph.Definition, ranges, ticks).Count == 0;
}

// What the Edit Scale dialog confirms for its axis (Task #054): its range and its ticks.
public sealed record GraphAxisScaleEdit(GraphAxisRangeOption Range, GraphAxisTickOption Ticks);

// The Edit Axes dialog of a drawn graph. Returns the ranges the user confirmed - which the axis range rules accept, which
// fit the graph's automatic ranges and over which the axes' ticks can be drawn - or null when the edit was cancelled.
// autoFrame is the graph's frame before any range was chosen: what Auto shows, and what a range with one end chosen is
// checked against. ticks are the axes' ticks as they are: kept, never edited here.
public interface IGraphAxesDialog
{
    Task<GraphAxisRangeOptions?> EditAsync(
        GraphTypeDefinition definition,
        GraphAxisRangeOptions current,
        GraphRenderModel autoFrame,
        GraphAxisTickOptions ticks);
}

// The Edit X Scale / Edit Y Scale dialog of a drawn graph (Tasks #052, #054): the range and the ticks of one axis.
// Returns what the user confirmed for that axis, or null when the edit was cancelled. currentRanges and currentTicks
// hold both axes' as they are (the other axis's are kept, and this axis's are checked with them); autoFrame is the
// graph's frame before any range was chosen.
public interface IGraphAxisScaleDialog
{
    Task<GraphAxisScaleEdit?> EditAsync(
        GraphTypeDefinition definition,
        GraphAxisField axis,
        GraphAxisRangeOptions currentRanges,
        GraphAxisTickOptions currentTicks,
        GraphRenderModel autoFrame);
}
