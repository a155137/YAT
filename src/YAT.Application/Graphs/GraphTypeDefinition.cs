using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// The graph types YAT can configure. Graphs are identified by this type, never by their display text.
public enum GraphType
{
    ScatterPlot,
    Histogram,
    ProbabilityPlot,
    EmpiricalCdf,

    // Added after the first four; the order graphs are offered in is the order of GraphTypeDefinitions.All, not of
    // this enum.
    BoxPlot
}

// What a worksheet column is used for in a graph. Roles differ per graph type: a scatter plot has X and Y, the
// single-variable graphs have Variable. Later graphs may add roles (By, Weight, Label, Panel).
public enum GraphVariableRole
{
    X,
    Y,
    Variable,
    Group
}

// One role of one graph type: whether it must be assigned, which column data types it accepts, and whether it takes
// more than one column.
//
// Cardinality is these two flags and nothing more:
//
//     required, single    -> exactly one column (a scatter plot's X axis)
//     required, multiple  -> one or more columns (a box plot's graph variables)
//     optional, single    -> at most one column (a grouping column)
//
// AllowsMultiple is what the generic validator and the setup dialog read; no graph type is named anywhere in them.
public sealed record GraphRoleDefinition(
    GraphVariableRole Role,
    string DisplayName,
    bool IsRequired,
    IReadOnlyList<WorksheetDataType> AllowedDataTypes,
    bool AllowsMultiple = false)
{
    // The most columns a role that takes several may be given: enough to compare, few enough to read.
    public const int MaximumColumns = 10;

    public bool Allows(WorksheetDataType dataType) => AllowedDataTypes.Contains(dataType);
}

// The specification of one graph type: its display name and its roles. Validation, the setup dialog and later the graph
// data pipeline all read the roles from here, so the rules live in one place.
public sealed record GraphTypeDefinition(GraphType GraphType, string DisplayName, IReadOnlyList<GraphRoleDefinition> Roles)
{
    // What this graph type can show besides its plot (see GraphCapability). None unless declared.
    public IReadOnlyList<GraphCapability> Capabilities { get; init; } = [];

    public bool Supports(GraphCapability capability) => Capabilities.Contains(capability);

    // What each axis reads, as far as a range the user chooses for it goes (GraphCapability.AxisRange). None - no range
    // to choose - unless declared.
    public GraphAxisKind XAxisKind { get; init; } = GraphAxisKind.None;

    public GraphAxisKind YAxisKind { get; init; } = GraphAxisKind.None;

    public GraphAxisKind AxisKind(GraphAxisField axis) => axis == GraphAxisField.X ? XAxisKind : YAxisKind;

    // Whether the user may choose the range of this axis: the graph type offers axis ranges and the axis has one.
    public bool SupportsAxisRange(GraphAxisField axis) =>
        Supports(GraphCapability.AxisRange) && AxisKind(axis) != GraphAxisKind.None;

    public IEnumerable<GraphRoleDefinition> RequiredRoles => Roles.Where(role => role.IsRequired);

    public IEnumerable<GraphRoleDefinition> OptionalRoles => Roles.Where(role => !role.IsRequired);

    public GraphRoleDefinition? FindRole(GraphVariableRole role) => Roles.FirstOrDefault(candidate => candidate.Role == role);
}
