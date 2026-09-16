using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// The graph types YAT can configure. Graphs are identified by this type, never by their display text.
public enum GraphType
{
    ScatterPlot,
    Histogram,
    ProbabilityPlot,
    EmpiricalCdf
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

// One role of one graph type: whether it must be assigned, and which column data types it accepts.
public sealed record GraphRoleDefinition(
    GraphVariableRole Role,
    string DisplayName,
    bool IsRequired,
    IReadOnlyList<WorksheetDataType> AllowedDataTypes)
{
    public bool Allows(WorksheetDataType dataType) => AllowedDataTypes.Contains(dataType);
}

// The specification of one graph type: its display name and its roles. Validation, the setup dialog and later the graph
// data pipeline all read the roles from here, so the rules live in one place.
public sealed record GraphTypeDefinition(GraphType GraphType, string DisplayName, IReadOnlyList<GraphRoleDefinition> Roles)
{
    public IEnumerable<GraphRoleDefinition> RequiredRoles => Roles.Where(role => role.IsRequired);

    public IEnumerable<GraphRoleDefinition> OptionalRoles => Roles.Where(role => !role.IsRequired);

    public GraphRoleDefinition? FindRole(GraphVariableRole role) => Roles.FirstOrDefault(candidate => candidate.Role == role);
}
