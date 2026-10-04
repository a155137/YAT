using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// How a drawn graph is zoomed and panned (Task #055): wheel steps, a drag, and Reset View, turned into the view the graph
// is shown over (GraphPresentationState.WithView). Nothing else changes - not the configured ranges, not the ticks, not
// the labels, legend, statistics or appearance - and nothing is read, queried or computed again.
//
// Only the axes the graph type gives a range are navigated: a box plot's categories never move. Zooming is about the value
// under the cursor, on every navigable axis or on one; panning follows the pointer from where the drag began. Both are
// kept within each axis's limits (GraphViewNavigator.Limits), measured against the range the axis shows before any
// navigation, so they are clamped, never refused.
//
// The view also records the plot area it was last navigated or resized in, so a thinned interval stays readable on
// screen (GraphViewOptions.PlotWidth, PlotHeight).
//
// It knows nothing of windows or pointers: the graph window says where the plot area is and where the pointer is, in the
// canvas's coordinates, so tests drive it without one.
public sealed class GraphViewController
{
    private (SKRect PlotArea, SKPoint From, GraphRenderModel Start)? _pan;
    private GraphPresentationState _graph;

    public GraphViewController(GraphPresentationState graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ResetCommand = new RelayCommand(() => Reset(), () => CanReset);
        _graph = graph;
    }

    // The graph as it is shown now.
    public GraphPresentationState Graph
    {
        get => _graph;
        private set
        {
            _graph = value;
            ResetCommand.NotifyCanExecuteChanged();
        }
    }

    // Reset View as a command (Task #056): what the right-click menu's Reset View and the Home key run, and the very
    // Reset a double-click in the plot runs. Available only while the graph is zoomed or panned, which it follows as the
    // graph changes - here or by another edit shown to this controller.
    public IRelayCommand ResetCommand { get; }

    // Whether the graph is zoomed or panned: whether Reset View has anything to reset.
    public bool CanReset => !Graph.ViewOptions.IsDefault;

    // Raised after the view changed Graph.
    public event EventHandler? GraphChanged;

    // Whether a pan is under way (a drag in the plot that has passed the drag threshold).
    public bool IsPanning => _pan is not null;

    // Zooms by a wheel delta about the point: on the one axis given, or - with none - on every axis the graph type lets
    // the user navigate. True when the view changed.
    public bool ZoomAt(GraphAxisField? axis, SKRect plotArea, SKPoint point, double wheelDelta)
    {
        // Nothing to zoom - a box plot's categories - is no zoom at all: the view is not even measured again.
        var fields = Axes(axis).ToList();
        if (!double.IsFinite(wheelDelta) || wheelDelta == 0 || !IsUsable(plotArea) || fields.Count == 0)
        {
            return false;
        }

        var scale = GraphViewNavigator.ZoomScale(wheelDelta);
        var view = Measured(Graph.ViewOptions, plotArea);
        foreach (var field in fields)
        {
            var shown = Shown(Graph.UnlabelledFrame, field);
            var anchor = GraphViewNavigator.ValueAt(field, shown, plotArea, point);
            view = With(view, field, GraphViewNavigator.Zoom(shown, anchor, scale, Limits(field)));
        }

        return Apply(view);
    }

    // A drag in the plot has passed the threshold at from: the pan starts from the view as it is.
    public void BeginPan(SKRect plotArea, SKPoint from)
    {
        _pan = IsUsable(plotArea) ? (plotArea, from, Graph.UnlabelledFrame) : null;
    }

    // The pointer of the pan is at point: the view is the one the pan started from, moved as far as the pointer has. True
    // when the view changed.
    public bool PanTo(SKPoint point)
    {
        if (_pan is not { } pan)
        {
            return false;
        }

        var view = Measured(Graph.ViewOptions, pan.PlotArea);
        foreach (var field in Axes(null))
        {
            var start = Shown(pan.Start, field);
            var shift = GraphViewNavigator.Shift(field, start, pan.PlotArea, pan.From, point);
            view = With(view, field, GraphViewNavigator.Pan(start, shift, Limits(field)));
        }

        return Apply(view);
    }

    // The window was resized: a navigated graph's plot area now spans other pixels, which may thin an interval's ticks
    // differently (GraphAxisTickBuilder.MinimumTickSpacing). The ranges it shows do not change. True when the view changed.
    public bool Resize(SKRect plotArea) =>
        !Graph.ViewOptions.IsDefault && IsUsable(plotArea) && Apply(Measured(Graph.ViewOptions, plotArea));

    // The pan is over (the button released, or the pointer lost): the view stays where it was moved to.
    public void EndPan() => _pan = null;

    // Reset View: every axis back over its configured range - Auto or chosen - with its ticks as they are. True when the
    // view changed.
    public bool Reset()
    {
        _pan = null;
        return Apply(GraphViewOptions.Default);
    }

    // The graph as it is shown now, after something else changed it (its labels, ranges, ticks, legend, ...).
    public void Show(GraphPresentationState graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        Graph = graph;
    }

    // The view with the plot area it is shown in now: how many pixels a navigated axis's ticks are spread over.
    private static GraphViewOptions Measured(GraphViewOptions view, SKRect plotArea) =>
        view with { PlotWidth = plotArea.Width, PlotHeight = plotArea.Height };

    private IEnumerable<GraphAxisField> Axes(GraphAxisField? axis) =>
        (axis is { } only ? new[] { only } : new[] { GraphAxisField.X, GraphAxisField.Y }).Where(Graph.Definition.SupportsAxisRange);

    private GraphViewLimits Limits(GraphAxisField field) =>
        GraphViewNavigator.Limits(Graph.Definition.AxisKind(field), Shown(Graph.ConfiguredFrame, field));

    private static GraphAxisRange Shown(GraphRenderModel frame, GraphAxisField field) =>
        field == GraphAxisField.X ? frame.XAxis.Range : frame.YAxis.Range;

    // A navigated range, kept only where it is one an axis can be drawn over; else the axis keeps the view it had.
    private static GraphViewOptions With(GraphViewOptions view, GraphAxisField field, GraphAxisRange range) =>
        GraphAxisRangeRules.IsUsable(range.Minimum, range.Maximum) ? view.With(field, range) : view;

    private bool Apply(GraphViewOptions view)
    {
        if (view == Graph.ViewOptions)
        {
            return false;
        }

        Graph = Graph.WithView(view);
        GraphChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool IsUsable(SKRect plotArea) =>
        float.IsFinite(plotArea.Width) && float.IsFinite(plotArea.Height) && plotArea.Width > 0 && plotArea.Height > 0;
}
