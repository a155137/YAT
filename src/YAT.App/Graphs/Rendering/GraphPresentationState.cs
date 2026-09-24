using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// A presented graph as a graph window keeps it: the frame as it was before the axis ranges and the labels were put on
// it (the graph type's frame with its statistics panel and specification lines), the graph type, the axis range and
// label options, and the frame those options make - the one on screen.
//
// Axis ranges and labels are the last presentation steps (GraphAxisViewportBuilder, then GraphLabelsBuilder). Neither
// changes anything but the axes' ranges and ticks or the titles, so changing either needs nothing but the frame before
// them: WithAxisRanges and WithLabels put other options on that same base frame. Nothing is read, queried, built or
// computed again - no data, no statistics, no specification, no bins, no fitted lines - and Auto finds the graph type's
// own ranges and titles there however they were set before.
public sealed class GraphPresentationState
{
    public GraphPresentationState(
        GraphRenderModel baseFrame,
        GraphTypeDefinition definition,
        GraphLabelOptions labelOptions,
        GraphAxisRangeOptions? axisRangeOptions = null)
    {
        ArgumentNullException.ThrowIfNull(baseFrame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(labelOptions);

        BaseFrame = baseFrame;
        Definition = definition;
        LabelOptions = labelOptions;
        AxisRangeOptions = axisRangeOptions ?? GraphAxisRangeOptions.Default;
        UnlabelledFrame = GraphAxisViewportBuilder.Attach(baseFrame, definition, AxisRangeOptions);
        Frame = GraphLabelsBuilder.Attach(UnlabelledFrame, definition, labelOptions);
    }

    // The frame before the axis ranges and the labels: the graph type's own ranges and titles, its panel, its lines
    // and the axes they reach. What Auto means for every axis and every title.
    public GraphRenderModel BaseFrame { get; }

    // The frame with the axis ranges resolved, before the labels.
    public GraphRenderModel UnlabelledFrame { get; }

    public GraphTypeDefinition Definition { get; }

    public GraphLabelOptions LabelOptions { get; }

    public GraphAxisRangeOptions AxisRangeOptions { get; }

    // The frame with the axis ranges and the labels resolved: what is drawn, copied and exported.
    public GraphRenderModel Frame { get; }

    // The same graph under other labels, over the same axis ranges. Options the label rules refuse are refused here
    // too.
    public GraphPresentationState WithLabels(GraphLabelOptions labelOptions) =>
        new(BaseFrame, Definition, labelOptions, AxisRangeOptions);

    // The same graph over other axis ranges, under the same labels. Ranges the rules refuse, or that do not fit the
    // automatic ends of this graph (GraphAxisViewportBuilder.Conflicts), are refused here too.
    public GraphPresentationState WithAxisRanges(GraphAxisRangeOptions axisRangeOptions) =>
        new(BaseFrame, Definition, LabelOptions, axisRangeOptions);
}
