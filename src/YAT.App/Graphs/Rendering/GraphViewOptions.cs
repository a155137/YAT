using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// What part of a graph a graph window shows right now because the user zoomed or panned it (Task #055): for each axis,
// the range it is viewed over, or null where it shows its configured range (#043/#052) - Auto or chosen - as it is.
//
// A view is not a range. The configured range is what the graph is set to show, and stays so: Edit Scale shows it, and
// Reset View returns to it. The view is where the user has navigated since, and only a range committed in Edit Scale or
// Edit Axes ends it for that axis. Neither touches the ticks (#054), which mark whatever the axis shows.
//
// Ranges are on the axis's own values: normal scores on a probability axis, where navigating is linear on screen. Kept by
// the graph window only, never by the configuration or a project.
//
// PlotWidth and PlotHeight are the plot area the view was last navigated or resized in, in pixels (0 where unknown): how
// far apart a navigated axis's ticks are on screen, so that an interval thinned for a zoomed-out view stays readable
// (GraphAxisTickBuilder). They are measured when the user zooms, pans or resizes the window - never by laying the
// frame out again, so a frame cannot change the measure it was made with.
public sealed record GraphViewOptions(GraphAxisRange? X, GraphAxisRange? Y)
{
    // Nothing navigated: every axis over its configured range.
    public static GraphViewOptions Default { get; } = new(null, null);

    public bool IsDefault => X is null && Y is null;

    public double PlotWidth { get; init; }

    public double PlotHeight { get; init; }

    // The pixels a navigated axis spans on screen, or null for an axis that is not navigated or not measured.
    public double? PixelsFor(GraphAxisField axis) =>
        For(axis) is null ? null
        : (axis == GraphAxisField.X ? PlotWidth : PlotHeight) is > 0 and var pixels ? pixels
        : null;

    public GraphAxisRange? For(GraphAxisField axis) => axis == GraphAxisField.X ? X : Y;

    public GraphViewOptions With(GraphAxisField axis, GraphAxisRange? range) =>
        axis == GraphAxisField.X ? this with { X = range } : this with { Y = range };
}

// Puts the view on a presented frame (Task #055): the step after the configured ranges (GraphAxisViewportBuilder) and
// before the ticks (GraphAxisTickBuilder). An axis with a view is shown over it, with the Auto ticks its scale reads on
// there - the ticks step then marks it as the user chose; an axis without one, or one the graph type gives no range to
// (a box plot's categories), is left as it is. Nothing is read, queried, built or computed again; with no view it returns
// the very frame it was given.
public static class GraphViewBuilder
{
    public static GraphRenderModel Attach(GraphRenderModel frame, GraphTypeDefinition definition, GraphViewOptions view)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(view);

        if (view.IsDefault)
        {
            return frame;
        }

        var result = frame;
        if (Viewed(frame.XAxis, definition, GraphAxisField.X, view.X) is { } x)
        {
            result = result.WithXAxis(x);
        }

        if (Viewed(frame.YAxis, definition, GraphAxisField.Y, view.Y) is { } y)
        {
            result = result.WithYAxis(y);
        }

        return result;
    }

    private static GraphAxisModel? Viewed(GraphAxisModel axis, GraphTypeDefinition definition, GraphAxisField field, GraphAxisRange? range)
    {
        if (range is not { } shown || !definition.SupportsAxisRange(field))
        {
            return null;
        }

        if (!shown.IsValid)
        {
            throw new ArgumentException($"The {field} view must be a finite, non-empty range.", nameof(range));
        }

        return new GraphAxisModel(shown, GraphAxisViewportBuilder.Ticks(axis.Scale, shown), axis.Title) { Scale = axis.Scale };
    }
}
