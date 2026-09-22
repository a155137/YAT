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
    FittedLine
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
