using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Puts the user's legend options on a presented frame (Task #044): after the statistics panel and the specification,
// before the axis ranges and the labels.
//
// It decides two things and nothing else: whether the frame keeps the legend its graph type gave it, and on which side
// of the plot that legend stands (GraphRenderModel.LegendPosition, which only the layout reads). Hide gives a frame
// without a legend, so the layout gives its room back to the plot; Auto and Show keep the graph type's legend - Show
// never makes one up for a graph that has none. The legend's entries, their order and their series indexes are the
// graph type's, and the plot model, the statistics panel, the lines and the axes are never touched: the series are
// what they were, only their key is or is not shown.
//
// With the legend where it always was - Auto or Show, on the right - it returns the very frame it was given, and so it
// does for a legend hidden on a frame that has none.
public static class GraphLegendPresentationBuilder
{
    public static GraphRenderModel Attach(
        GraphRenderModel frame,
        GraphTypeDefinition definition,
        GraphLegendOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        if (!definition.Supports(GraphCapability.Legend))
        {
            return frame;
        }

        if (!options.IsValid)
        {
            throw new ArgumentException(
                "The legend options are not valid; validate the configuration first.", nameof(options));
        }

        var legend = options.Mode == GraphLegendMode.Hide ? null : frame.Legend;
        return ReferenceEquals(legend, frame.Legend) && options.Position == frame.LegendPosition
            ? frame
            : frame.WithLegend(legend, options.Position);
    }
}
