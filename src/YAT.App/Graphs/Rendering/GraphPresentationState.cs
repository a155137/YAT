using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// A presented graph as a graph window keeps it: the frame as it was before the labels were put on it (the graph type's
// frame with its statistics panel and specification lines), the graph type, the label options, and the frame those
// options make - the one on screen.
//
// Labels are the last presentation step and only rename the title and the axis titles, so changing them needs nothing
// but the frame before them: WithLabels puts other options on that same frame. Nothing is read, queried, built or
// computed again - no data, no statistics, no specification, no bins, no fitted lines - and Auto finds the graph type's
// own titles there however the labels were set before.
public sealed class GraphPresentationState
{
    public GraphPresentationState(
        GraphRenderModel unlabelledFrame,
        GraphTypeDefinition definition,
        GraphLabelOptions labelOptions)
    {
        ArgumentNullException.ThrowIfNull(unlabelledFrame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(labelOptions);

        UnlabelledFrame = unlabelledFrame;
        Definition = definition;
        LabelOptions = labelOptions;
        Frame = GraphLabelsBuilder.Attach(unlabelledFrame, definition, labelOptions);
    }

    // The frame before the labels: the graph type's own titles, its panel, its lines and the axes they reach.
    public GraphRenderModel UnlabelledFrame { get; }

    public GraphTypeDefinition Definition { get; }

    public GraphLabelOptions LabelOptions { get; }

    // The frame with the labels resolved: what is drawn, copied and exported.
    public GraphRenderModel Frame { get; }

    // The same graph under other labels. Options the label rules refuse are refused here too.
    public GraphPresentationState WithLabels(GraphLabelOptions labelOptions) =>
        new(UnlabelledFrame, Definition, labelOptions);
}
