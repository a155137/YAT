namespace YAT.Application.Graphs;

// One worksheet column used in one role of a graph. The column is referenced by its stable Id: a column's name and
// index may change without changing what the graph is configured to show.
public sealed record GraphColumnAssignment(GraphVariableRole Role, Guid WorksheetColumnId);

// A graph's configuration: its type, the worksheet it reads (V1 graphs use exactly one) and the columns assigned to its
// roles. Validate it with GraphConfigurationValidator before using it; nothing here reads worksheet values.
public sealed record GraphConfiguration(GraphType GraphType, Guid WorksheetId, IReadOnlyList<GraphColumnAssignment> Assignments)
{
    // The column of a single-valued role: the first one assigned to it, or null when it has none. Roles that take
    // several columns are read with FindColumnIds.
    public Guid? FindColumnId(GraphVariableRole role) =>
        Assignments.FirstOrDefault(assignment => assignment.Role == role)?.WorksheetColumnId;

    // Every column assigned to a role, in the order the configuration lists them (which is the order the graph reads
    // them in). Empty when the role has none.
    public IReadOnlyList<Guid> FindColumnIds(GraphVariableRole role) =>
        [.. Assignments.Where(assignment => assignment.Role == role).Select(assignment => assignment.WorksheetColumnId)];
}
