using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Everything a graph shows besides its plot, put on the frame its graph type's builder made. The graph preparation
// calls this once per graph, and nothing else composes these steps, so every place that prepares a graph - the graph
// window, and the robustness harness that checks it - gets the same frame from the same configuration.
//
// The order is fixed:
//
//     1. the statistics panel, from the graph data (the only step that reads it);
//     2. the specification lines, which may widen the displayed X axis.
//
// Neither step depends on the other - the panel reads the data, never the axis, and the lines read the axis, never the
// panel - so the order only has to be one and the same everywhere. Each step replaces what it put there before, so
// applying the same configuration to its own result changes nothing.
public static class GraphPresentation
{
    public static GraphRenderModel Apply(
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
        return GraphSpecificationLinesBuilder.Attach(withPanel, definition, configuration.Specification);
    }
}
