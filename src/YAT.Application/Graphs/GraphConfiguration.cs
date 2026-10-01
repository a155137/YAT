using YAT.Application.Specifications;

namespace YAT.Application.Graphs;

// One worksheet column used in one role of a graph. The column is referenced by its stable Id: a column's name and
// index may change without changing what the graph is configured to show.
public sealed record GraphColumnAssignment(GraphVariableRole Role, Guid WorksheetColumnId);

// A graph's configuration: its type, the worksheet it reads (V1 graphs use exactly one) and the columns assigned to its
// roles. Validate it with GraphConfigurationValidator before using it; nothing here reads worksheet values.
public sealed record GraphConfiguration(GraphType GraphType, Guid WorksheetId, IReadOnlyList<GraphColumnAssignment> Assignments)
{
    // Whether the statistics panel is shown and with which statistics (Task #045). Graph types that do not declare
    // GraphCapability.StatisticsPanel ignore it; the data query never reads it. The graph type's panel with every
    // statistic unless the user chose.
    public GraphStatisticsOptions StatisticsOptions { get; init; } = GraphStatisticsOptions.Default;

    // The specification the measured variable is held to. It is data about the measurement rather than a presentation
    // option: graph types that declare SpecificationLines draw it, the others ignore it, and the data query never reads
    // it. None unless the user entered one.
    public Specification Specification { get; init; } = Specification.None;

    // What a probability plot shows of its own plot. Graph types that do not declare GraphCapability.FittedLine ignore
    // it; the data query never reads it.
    public ProbabilityPlotOptions ProbabilityPlotOptions { get; init; } = ProbabilityPlotOptions.Default;

    // What a histogram shows of its own plot: its Y scale and its bins. Graph types that do not declare
    // GraphCapability.HistogramControls ignore it; the data query never reads it.
    public HistogramOptions HistogramOptions { get; init; } = HistogramOptions.Default;

    // Where the graph title and the axis titles come from. Graph types that do not declare GraphCapability.Labels
    // ignore it; the data query and the graph types' builders never read it. Every label Auto unless the user chose.
    public GraphLabelOptions LabelOptions { get; init; } = GraphLabelOptions.Default;

    // The range each axis is shown over. Graph types that do not declare GraphCapability.AxisRange ignore it; the data
    // query and the graph types' builders never read it. Both axes Auto unless the user chose.
    public GraphAxisRangeOptions AxisRangeOptions { get; init; } = GraphAxisRangeOptions.Default;

    // Whether the legend is shown and where. Graph types that do not declare GraphCapability.Legend ignore it; the data
    // query and the graph types' builders never read it. The graph type's own legend, on the right, unless the user
    // chose.
    public GraphLegendOptions LegendOptions { get; init; } = GraphLegendOptions.Default;

    // How the graph looks: its series colours, its grid and its backgrounds (Task #046). Graph types that do not
    // declare GraphCapability.Appearance ignore it; the data query and the graph types' builders never read it. The
    // graph theme's own look unless the user chose.
    public GraphAppearanceOptions AppearanceOptions { get; init; } = GraphAppearanceOptions.Default;

    // How a box plot draws its boxes: their width, and whether their means and outliers are marked (Task #047). Graph
    // types that do not declare GraphCapability.BoxPlotControls ignore it; the data query never reads it. Boxes as
    // they have always been drawn unless the user chose.
    public BoxPlotOptions BoxPlotOptions { get; init; } = BoxPlotOptions.Default;

    // Which worksheet rows the graph uses (Task #049): null - every row - unless the user selected values of a column.
    // Unlike the options above, it is read by the data query and nothing after it: every graph type is built from the
    // rows it keeps, and none of them knows there was a filter.
    public GraphValueFilter? Filter { get; init; }

    // The column of a single-valued role: the first one assigned to it, or null when it has none. Roles that take
    // several columns are read with FindColumnIds.
    public Guid? FindColumnId(GraphVariableRole role) =>
        Assignments.FirstOrDefault(assignment => assignment.Role == role)?.WorksheetColumnId;

    // Every column assigned to a role, in the order the configuration lists them (which is the order the graph reads
    // them in). Empty when the role has none.
    public IReadOnlyList<Guid> FindColumnIds(GraphVariableRole role) =>
        [.. Assignments.Where(assignment => assignment.Role == role).Select(assignment => assignment.WorksheetColumnId)];
}
