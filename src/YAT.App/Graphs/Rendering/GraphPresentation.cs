using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Everything a graph shows besides its plot, put on the frame its graph type's builder made. The graph preparation
// calls this once per graph, and nothing else composes these steps, so every place that prepares a graph - the graph
// window, and the robustness harness that checks it - gets the same frame from the same configuration.
//
// The order is fixed:
//
//     1. the statistics panel, from the graph data (the only step that reads it);
//     2. the specification lines, which may widen the displayed X axis;
//     3. the labels, which only rename: the graph title and the axis titles of the finished frame (a widened X axis
//        included).
//
// No step depends on another - the panel reads the data, never the axis, the lines read the axis range, never the
// panel, and the labels read only the three titles, which no other step changes - so the order only has to be one and
// the same everywhere. Each step replaces what it put there before, so applying the same configuration to its own
// result changes nothing. With the default configuration's labels (all Auto) the last step returns its frame as is.
//
// Present keeps the frame as it was before the labels, so a graph window can put other labels on it later without
// the data (see GraphPresentationState); Apply is the frame it presents.
public static class GraphPresentation
{
    public static GraphRenderModel Apply(
        GraphRenderModel frame,
        GraphData data,
        GraphConfiguration configuration,
        CancellationToken cancellationToken = default) =>
        Present(frame, data, configuration, cancellationToken).Frame;

    public static GraphPresentationState Present(
        GraphRenderModel frame,
        GraphData data,
        GraphConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(configuration);

        var definition = GraphTypeDefinitions.For(configuration.GraphType);
        var withPanel = GraphStatisticsPanelBuilder.Attach(frame, data, definition, configuration.PresentationOptions, cancellationToken);
        var withLines = GraphSpecificationLinesBuilder.Attach(withPanel, definition, configuration.Specification);
        return new GraphPresentationState(withLines, definition, configuration.LabelOptions);
    }
}
