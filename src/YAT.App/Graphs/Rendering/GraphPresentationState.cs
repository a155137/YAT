using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// A presented graph as a graph window keeps it: the frame as it was before the statistics, legend, axis range and
// label options were put on it (the graph type's frame with its whole statistics panel and its specification lines),
// the graph type, those options, and the frame they make - the one on screen.
//
// The statistics options, the legend, the axis ranges and the labels are the last presentation steps
// (GraphStatisticsPresentationBuilder, GraphLegendPresentationBuilder, GraphAxisViewportBuilder, then
// GraphLabelsBuilder). None changes anything but whether and with which statistics the panel is shown, whether and
// where the legend stands, the axes' ranges and ticks, or the titles, so changing any of them needs nothing but the
// frame before them: WithStatistics, WithLegend, WithAxisRanges and WithLabels put other options on that same base
// frame. Nothing is read, queried, built or computed again - no data, no statistics, no specification, no bins, no
// fitted lines - and Auto finds the graph type's own panel, legend, ranges and titles there however they were set
// before.
public sealed class GraphPresentationState
{
    public GraphPresentationState(
        GraphRenderModel baseFrame,
        GraphTypeDefinition definition,
        GraphLabelOptions labelOptions,
        GraphAxisRangeOptions? axisRangeOptions = null,
        GraphLegendOptions? legendOptions = null,
        GraphStatisticsOptions? statisticsOptions = null)
    {
        ArgumentNullException.ThrowIfNull(baseFrame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(labelOptions);

        BaseFrame = baseFrame;
        Definition = definition;
        LabelOptions = labelOptions;
        AxisRangeOptions = axisRangeOptions ?? GraphAxisRangeOptions.Default;
        LegendOptions = legendOptions ?? GraphLegendOptions.Default;
        StatisticsOptions = statisticsOptions ?? GraphStatisticsOptions.Default;
        var withStatistics = GraphStatisticsPresentationBuilder.Attach(baseFrame, definition, StatisticsOptions);
        var withLegend = GraphLegendPresentationBuilder.Attach(withStatistics, definition, LegendOptions);
        UnlabelledFrame = GraphAxisViewportBuilder.Attach(withLegend, definition, AxisRangeOptions);
        Frame = GraphLabelsBuilder.Attach(UnlabelledFrame, definition, labelOptions);
    }

    // The frame before the statistics, legend, axis range and label options: the graph type's own legend, ranges and
    // titles, its whole panel, its lines and the axes they reach. What Auto means for the panel, the legend, every axis
    // and every title.
    public GraphRenderModel BaseFrame { get; }

    // The frame with the statistics, the legend and the axis ranges resolved, before the labels.
    public GraphRenderModel UnlabelledFrame { get; }

    public GraphTypeDefinition Definition { get; }

    public GraphLabelOptions LabelOptions { get; }

    public GraphAxisRangeOptions AxisRangeOptions { get; }

    public GraphLegendOptions LegendOptions { get; }

    public GraphStatisticsOptions StatisticsOptions { get; }

    // The frame with the statistics, the legend, the axis ranges and the labels resolved: what is drawn, copied and
    // exported.
    public GraphRenderModel Frame { get; }

    // The same graph under other labels, over the same axis ranges. Options the label rules refuse are refused here
    // too.
    public GraphPresentationState WithLabels(GraphLabelOptions labelOptions) =>
        new(BaseFrame, Definition, labelOptions, AxisRangeOptions, LegendOptions, StatisticsOptions);

    // The same graph over other axis ranges, under the same labels. Ranges the rules refuse, or that do not fit the
    // automatic ends of this graph (GraphAxisViewportBuilder.Conflicts), are refused here too.
    public GraphPresentationState WithAxisRanges(GraphAxisRangeOptions axisRangeOptions) =>
        new(BaseFrame, Definition, LabelOptions, axisRangeOptions, LegendOptions, StatisticsOptions);

    // The same graph with its legend shown, hidden or moved, over the same axis ranges and under the same labels.
    public GraphPresentationState WithLegend(GraphLegendOptions legendOptions) =>
        new(BaseFrame, Definition, LabelOptions, AxisRangeOptions, legendOptions, StatisticsOptions);

    // The same graph with its statistics panel shown or hidden, or showing other statistics - the panel worked out
    // with the graph, never again - over the same axis ranges and under the same labels and legend. Options the
    // statistics rules refuse are refused here too.
    public GraphPresentationState WithStatistics(GraphStatisticsOptions statisticsOptions) =>
        new(BaseFrame, Definition, LabelOptions, AxisRangeOptions, LegendOptions, statisticsOptions);
}
