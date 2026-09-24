namespace YAT.Application.Graphs;

// What a graph type can show besides its plot. A graph type declares its capabilities in its definition; the setup
// dialog and the graph preparation ask for a capability, never for a graph type, so a new graph gains an option by
// declaring it.
public enum GraphCapability
{
    // A panel beside the plot with the Mean, standard deviation and N of every series, shown or hidden and with the
    // statistics the user chose (see GraphStatisticsOptions).
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
    AxisRange,

    // The legend of a graph of groups or of several variables drawn together: shown or hidden, and on which side of the
    // plot (see GraphLegendOptions).
    Legend,

    // How the graph looks: its series colours, its grid, and the colours behind and around its plot (see
    // GraphAppearanceOptions). Drawing only - never the data, the statistics or the axes.
    Appearance,

    // The box plot's own box width and whether its means and outliers are marked (see BoxPlotOptions). Drawing only -
    // never the statistics, the whiskers or the axes.
    BoxPlotControls
}
