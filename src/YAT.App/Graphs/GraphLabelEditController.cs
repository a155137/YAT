using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How the labels of a drawn graph are edited. The dialog is shown the labels as they are; nothing changes until the
// user confirms, and a cancelled edit leaves the graph exactly as it was - even when the dialog opened a picked title
// as Custom. Confirmed labels are put on the frame the graph had before its labels (GraphPresentationState.WithLabels),
// so no data is read and nothing is computed again; the window then shows, copies and exports the new frame.
//
// It knows nothing of windows: the dialog comes through IGraphLabelsDialog, so tests drive it without one.
public sealed class GraphLabelEditController
{
    private readonly IGraphLabelsDialog _dialog;
    private bool _editing;

    public GraphLabelEditController(GraphPresentationState graph, IGraphLabelsDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dialog);

        Graph = graph;
        _dialog = dialog;
    }

    // The graph as it is shown now.
    public GraphPresentationState Graph { get; private set; }

    // Raised after confirmed labels changed Graph.
    public event EventHandler? GraphChanged;

    // Edits the labels, opened on the title the user picked on the graph (focus) or on all of them (null). True when
    // the user confirmed labels and the graph now shows them.
    public async Task<bool> EditAsync(GraphLabelField? focus)
    {
        // One edit at a time: the dialog is modal, and a second request while it is open is not a second dialog.
        if (_editing)
        {
            return false;
        }

        _editing = true;
        try
        {
            var shown = focus is { } field ? ShownText(Graph.Frame, field) : null;
            var edited = await _dialog.EditAsync(Graph.Definition, Graph.LabelOptions, focus, shown);
            if (edited is null || GraphConfigurationValidator.LabelErrors(edited).Count > 0)
            {
                return false;
            }

            Graph = Graph.WithLabels(edited);
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _editing = false;
        }
    }

    // The text a title shows on the graph.
    public static string? ShownText(GraphRenderModel frame, GraphLabelField field)
    {
        ArgumentNullException.ThrowIfNull(frame);

        return field switch
        {
            GraphLabelField.XAxisTitle => frame.XAxis.Title,
            GraphLabelField.YAxisTitle => frame.YAxis.Title,
            _ => frame.Title
        };
    }
}

// The Edit Labels dialog of a drawn graph. Returns the labels the user confirmed - which the label rules accept - or
// null when the edit was cancelled. focus is the title the user picked on the graph, and shownText the text it shows;
// the dialog opens on that title, ready to type over it.
public interface IGraphLabelsDialog
{
    Task<GraphLabelOptions?> EditAsync(
        GraphTypeDefinition definition,
        GraphLabelOptions current,
        GraphLabelField? focus,
        string? shownText);
}
