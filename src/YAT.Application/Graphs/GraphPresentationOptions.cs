namespace YAT.Application.Graphs;

// What a graph type can show besides its plot. A graph type declares its capabilities in its definition; the setup
// dialog and the graph preparation ask for a capability, never for a graph type, so a new graph gains an option by
// declaring it.
public enum GraphCapability
{
    // A panel beside the plot with the Mean, standard deviation and N of every series.
    StatisticsPanel,

    // The lines of the graph's specification (LSL, Target, USL), drawn across the plot at their values on the X axis,
    // which is the axis a graph with this capability reads its measurement on.
    SpecificationLines,

    // The line a normally distributed sample would fall on, drawn through a probability plot (see
    // ProbabilityPlotOptions). It belongs to the plot itself, not to the frame around it.
    FittedLine,

    // The histogram's own Y scale and bins (see HistogramOptions).
    HistogramControls,

    // The graph title and the two axis titles, each shown as the graph type gives it, as typed, or not at all (see
    // GraphLabelOptions).
    Labels,

    // Several variables drawn together in one graph or each in a graph of its own (see GraphVariableLayout). Only a
    // graph type whose variable role takes several columns offers it.
    VariableLayout,

    // The range each axis is shown over, chosen automatically or by the user (see GraphAxisRangeOptions). Which axes
    // have one, and what may be typed for them, the graph type's axis kinds say.
    AxisRange
}

// How a graph is presented, as opposed to which data it reads. The graph data query ignores these options entirely:
// they change what is drawn around the plot, never the observations or the statistics of the graph itself.
//
// An option only takes effect on graph types whose definition declares the matching capability; elsewhere it is
// ignored, so the defaults can be the same for every graph.
public sealed record GraphPresentationOptions(bool ShowStatistics = true)
{
    // Statistics are shown unless the user turns them off.
    public static GraphPresentationOptions Default { get; } = new();
}
