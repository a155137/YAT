using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// A presented graph as a graph window keeps it: the frame as it was before the statistics, legend, axis range, axis tick
// and label options were put on it (the graph type's frame with its whole statistics panel and its specification lines),
// the graph type, those options, and the frame they make - the one on screen.
//
// The statistics options, the legend, the axis ranges, the view, the axis ticks and the labels are the last presentation
// steps (GraphStatisticsPresentationBuilder, GraphLegendPresentationBuilder, GraphAxisViewportBuilder, GraphViewBuilder,
// GraphAxisTickBuilder, then GraphLabelsBuilder). None changes anything but whether and with which statistics the panel
// is shown, whether and where the legend stands, the axes' ranges and ticks, or the titles, so changing any of them needs
// nothing but the frame before them: WithStatistics, WithLegend, WithAxisRanges, WithAxisScale, WithView and WithLabels
// put other options on that same base frame, each keeping all the others. Nothing is read, queried, built or computed
// again - no data, no statistics, no specification, no bins, no fitted lines - and Auto finds the graph type's own
// panel, legend, ranges, ticks and titles there however they were set before.
//
// It also keeps how the graph looks (Task #046), which is not a presentation step at all: the appearance never touches
// a frame. It is the theme the frame is drawn in that it changes (GraphAppearance.Resolve, where the graph is drawn and
// exported), so WithAppearance keeps the very frames it had and changes nothing but the appearance kept with them.
public sealed class GraphPresentationState
{
    public GraphPresentationState(
        GraphRenderModel baseFrame,
        GraphTypeDefinition definition,
        GraphLabelOptions labelOptions,
        GraphAxisRangeOptions? axisRangeOptions = null,
        GraphLegendOptions? legendOptions = null,
        GraphStatisticsOptions? statisticsOptions = null,
        GraphAppearanceOptions? appearanceOptions = null,
        GraphAxisTickOptions? axisTickOptions = null,
        GraphViewOptions? viewOptions = null)
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
        AppearanceOptions = Checked(appearanceOptions ?? GraphAppearanceOptions.Default);
        AxisTickOptions = axisTickOptions ?? GraphAxisTickOptions.Default;
        ViewOptions = viewOptions ?? GraphViewOptions.Default;
        var withStatistics = GraphStatisticsPresentationBuilder.Attach(baseFrame, definition, StatisticsOptions);
        var withLegend = GraphLegendPresentationBuilder.Attach(withStatistics, definition, LegendOptions);
        ConfiguredFrame = GraphAxisViewportBuilder.Attach(withLegend, definition, AxisRangeOptions);
        var viewed = GraphViewBuilder.Attach(ConfiguredFrame, definition, ViewOptions);
        UnlabelledFrame = GraphAxisTickBuilder.Attach(viewed, definition, AxisTickOptions, ViewOptions);
        Frame = GraphLabelsBuilder.Attach(UnlabelledFrame, definition, labelOptions);
    }

    // The frame before the statistics, legend, axis range and label options: the graph type's own legend, ranges and
    // titles, its whole panel, its lines and the axes they reach. What Auto means for the panel, the legend, every axis
    // and every title.
    public GraphRenderModel BaseFrame { get; }

    // The frame over the configured ranges, before the view and the ticks: what the view is navigated from and Reset
    // View returns to.
    public GraphRenderModel ConfiguredFrame { get; }

    // The frame with the statistics, the legend and the axis ranges resolved, before the labels.
    public GraphRenderModel UnlabelledFrame { get; }

    public GraphTypeDefinition Definition { get; }

    public GraphLabelOptions LabelOptions { get; }

    public GraphAxisRangeOptions AxisRangeOptions { get; }

    // The ticks each axis is marked with (Task #054): kept by the graph window only, never by the configuration, so a
    // graph always starts on Auto ticks.
    public GraphAxisTickOptions AxisTickOptions { get; }

    // The part of the graph the window is zoomed or panned to (Task #055), apart from the configured ranges: kept by the
    // graph window only, so a graph always starts unnavigated.
    public GraphViewOptions ViewOptions { get; }

    public GraphLegendOptions LegendOptions { get; }

    public GraphStatisticsOptions StatisticsOptions { get; }

    // How the graph looks. Not applied to any frame: the theme the frame is drawn in is resolved with it.
    public GraphAppearanceOptions AppearanceOptions { get; }

    // The frame with the statistics, the legend, the axis ranges and the labels resolved: what is drawn, copied and
    // exported.
    public GraphRenderModel Frame { get; }

    // The same graph under other labels, over the same axis ranges. Options the label rules refuse are refused here
    // too.
    public GraphPresentationState WithLabels(GraphLabelOptions labelOptions) =>
        new(BaseFrame, Definition, labelOptions, AxisRangeOptions, LegendOptions, StatisticsOptions, AppearanceOptions, AxisTickOptions, ViewOptions);

    // The same graph over other axis ranges, under the same labels and marked with the same ticks. Ranges the rules
    // refuse, or that do not fit the automatic ends of this graph (GraphAxisViewportBuilder.Conflicts), are refused here
    // too, and so is a range a tick interval would draw too many ticks over (GraphAxisTickBuilder.Problems).
    public GraphPresentationState WithAxisRanges(GraphAxisRangeOptions axisRangeOptions) =>
        new(BaseFrame, Definition, LabelOptions, axisRangeOptions, LegendOptions, StatisticsOptions, AppearanceOptions, AxisTickOptions, ViewOptions);

    // The same graph over other axis ranges and marked with other ticks, both at once (Task #054: one OK of the Edit
    // Scale dialog), under the same labels. What WithAxisRanges refuses, and ticks GraphAxisTickBuilder.Problems
    // finds wrong over the new ranges, are refused here too.
    public GraphPresentationState WithAxisScale(GraphAxisRangeOptions axisRangeOptions, GraphAxisTickOptions axisTickOptions) =>
        new(BaseFrame, Definition, LabelOptions, axisRangeOptions, LegendOptions, StatisticsOptions, AppearanceOptions, axisTickOptions, ViewOptions);

    // The same graph zoomed or panned to another view (Task #055), or back to its configured ranges with
    // GraphViewOptions.Default: the same configured ranges, ticks, labels, legend, statistics and appearance.
    public GraphPresentationState WithView(GraphViewOptions viewOptions) =>
        new(BaseFrame, Definition, LabelOptions, AxisRangeOptions, LegendOptions, StatisticsOptions, AppearanceOptions, AxisTickOptions, viewOptions);

    // The same graph with its legend shown, hidden or moved, over the same axis ranges and under the same labels.
    public GraphPresentationState WithLegend(GraphLegendOptions legendOptions) =>
        new(BaseFrame, Definition, LabelOptions, AxisRangeOptions, legendOptions, StatisticsOptions, AppearanceOptions, AxisTickOptions, ViewOptions);

    // The same graph with its statistics panel shown or hidden, or showing other statistics - the panel worked out
    // with the graph, never again - over the same axis ranges and under the same labels and legend. Options the
    // statistics rules refuse are refused here too.
    public GraphPresentationState WithStatistics(GraphStatisticsOptions statisticsOptions) =>
        new(BaseFrame, Definition, LabelOptions, AxisRangeOptions, LegendOptions, statisticsOptions, AppearanceOptions, AxisTickOptions, ViewOptions);

    // The same graph - the very same frames, nothing presented again - to be drawn with another appearance. An
    // appearance the rules refuse is refused here too.
    public GraphPresentationState WithAppearance(GraphAppearanceOptions appearanceOptions) =>
        new(this, Checked(appearanceOptions));

    private GraphPresentationState(GraphPresentationState graph, GraphAppearanceOptions appearanceOptions)
    {
        BaseFrame = graph.BaseFrame;
        Definition = graph.Definition;
        LabelOptions = graph.LabelOptions;
        AxisRangeOptions = graph.AxisRangeOptions;
        AxisTickOptions = graph.AxisTickOptions;
        ViewOptions = graph.ViewOptions;
        ConfiguredFrame = graph.ConfiguredFrame;
        LegendOptions = graph.LegendOptions;
        StatisticsOptions = graph.StatisticsOptions;
        UnlabelledFrame = graph.UnlabelledFrame;
        Frame = graph.Frame;
        AppearanceOptions = appearanceOptions;
    }

    private static GraphAppearanceOptions Checked(GraphAppearanceOptions appearanceOptions)
    {
        ArgumentNullException.ThrowIfNull(appearanceOptions);
        return appearanceOptions.IsValid
            ? appearanceOptions
            : throw new ArgumentException(
                "The appearance is not valid; validate the configuration first.", nameof(appearanceOptions));
    }
}
