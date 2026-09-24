using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How the appearance of a drawn graph is edited (Task #046): its series colours, its grid and its backgrounds. The
// dialog is shown the appearance as it is; nothing changes until the user confirms, and a cancelled edit leaves the
// graph exactly as it was. A confirmed appearance is kept with the graph (GraphPresentationState.WithAppearance) and
// changes nothing but the theme the graph is drawn in: the frames are the very same, and no data is read and nothing is
// worked out again. The window then draws, copies and exports the graph in its new colours.
//
// Every graph type has an appearance. It knows nothing of windows: the dialog comes through IGraphAppearanceDialog, so
// tests drive it without one.
public sealed class GraphAppearanceEditController
{
    private readonly IGraphAppearanceDialog _dialog;
    private bool _editing;

    public GraphAppearanceEditController(GraphPresentationState graph, IGraphAppearanceDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(dialog);

        Graph = graph;
        _dialog = dialog;
    }

    // The graph as it is shown now.
    public GraphPresentationState Graph { get; private set; }

    // Whether the graph type has an appearance to edit.
    public bool CanEdit => Graph.Definition.Supports(GraphCapability.Appearance);

    // Raised after a confirmed appearance changed Graph.
    public event EventHandler? GraphChanged;

    // Edits the appearance. True when the user confirmed one and the graph is now drawn with it.
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
            var edited = await _dialog.EditAsync(Graph.Definition, Graph.AppearanceOptions);
            if (edited is null || !edited.IsValid)
            {
                return false;
            }

            Graph = Graph.WithAppearance(edited);
            GraphChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _editing = false;
        }
    }

    // The graph as it is shown now, after something else changed it (its labels, axis ranges, legend or statistics,
    // edited on their own).
    public void Show(GraphPresentationState graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
    }
}

// The Edit Appearance dialog of a drawn graph. Returns the appearance the user confirmed, or null when the edit was
// cancelled.
public interface IGraphAppearanceDialog
{
    Task<GraphAppearanceOptions?> EditAsync(GraphTypeDefinition definition, GraphAppearanceOptions current);
}
