using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How the box plot options of a drawn box plot are edited (Task #047): its box width, and whether its means and
// outliers are marked. The dialog is shown the options as they are; nothing changes until the user confirms, and a
// cancelled edit leaves the graph exactly as it was.
//
// The options are drawing only, so a confirmed edit is the same box plot model with other options - its boxes, its
// frame and its axes the very same objects - drawn by a new plot renderer. No data is read, no box is worked out
// again, and the presented graph (its labels, axis ranges, legend and appearance) is not touched: the window swaps
// the plot it draws, copies and exports, and keeps everything else.
//
// Only a box plot has these options. It knows nothing of windows: the dialog comes through IGraphBoxPlotDialog, so
// tests drive it without one.
public sealed class GraphBoxPlotEditController
{
    private readonly GraphTypeDefinition _definition;
    private readonly IGraphBoxPlotDialog _dialog;
    private bool _editing;

    public GraphBoxPlotEditController(GraphTypeDefinition definition, IGraphPlotRenderer? plot, IGraphBoxPlotDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(dialog);

        _definition = definition;
        Plot = plot;
        _dialog = dialog;
    }

    // What the graph's plot is drawn by now.
    public IGraphPlotRenderer? Plot { get; private set; }

    // Whether the graph is a box plot with options to edit.
    public bool CanEdit => _definition.Supports(GraphCapability.BoxPlotControls) && Plot is BoxPlotRenderer;

    // Raised after confirmed options changed Plot.
    public event EventHandler? PlotChanged;

    // Edits the options. True when the user confirmed some and the plot is now drawn with them.
    public async Task<bool> EditAsync()
    {
        // One edit at a time: the dialog is modal, and a second request while it is open is not a second dialog.
        if (_editing || !CanEdit || Plot is not BoxPlotRenderer boxPlot)
        {
            return false;
        }

        _editing = true;
        try
        {
            var edited = await _dialog.EditAsync(boxPlot.Model.Options);
            if (edited is null || !edited.IsValid)
            {
                return false;
            }

            Plot = new BoxPlotRenderer(boxPlot.Model with { Options = edited });
            PlotChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally
        {
            _editing = false;
        }
    }
}

// The Edit Box Plot dialog of a drawn box plot. Returns the options the user confirmed, or null when the edit was
// cancelled.
public interface IGraphBoxPlotDialog
{
    Task<BoxPlotOptions?> EditAsync(BoxPlotOptions current);
}
