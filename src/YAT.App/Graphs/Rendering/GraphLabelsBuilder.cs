using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Puts the user's label choices on a finished frame: the graph title and the two axis titles, each kept as the graph
// type's builder wrote it (Auto), replaced by the typed text (Custom) or removed (Hidden). A removed label is null, so
// the layout gives its room back to the plot, as it does for any label a graph type leaves empty.
//
// It changes those three strings and nothing else - no range, tick, legend, panel or line - and only where a string
// actually changes; a frame whose labels all stay as they are is returned as the very frame it came with. The renderer
// and the exports only ever see the resolved strings.
public static class GraphLabelsBuilder
{
    public static GraphRenderModel Attach(
        GraphRenderModel frame,
        GraphTypeDefinition definition,
        GraphLabelOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        if (!definition.Supports(GraphCapability.Labels))
        {
            return frame;
        }

        var result = frame;

        var title = GraphLabelRules.Resolve(options.Title, frame.Title);
        if (!Same(title, frame.Title))
        {
            result = result.WithTitle(title);
        }

        var xTitle = GraphLabelRules.Resolve(options.XAxisTitle, frame.XAxis.Title);
        if (!Same(xTitle, frame.XAxis.Title))
        {
            result = result.WithXAxis(frame.XAxis.WithTitle(xTitle));
        }

        var yTitle = GraphLabelRules.Resolve(options.YAxisTitle, frame.YAxis.Title);
        if (!Same(yTitle, frame.YAxis.Title))
        {
            result = result.WithYAxis(frame.YAxis.WithTitle(yTitle));
        }

        return result;
    }

    private static bool Same(string? resolved, string? current) =>
        string.Equals(resolved, current, StringComparison.Ordinal);
}
