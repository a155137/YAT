using YAT.Domain.Entities;

namespace YAT.Application.Queries;

// The stored metadata of one project, as needed to show it: the project, and its worksheets in order with their
// column metadata ordered by Index. Never any raw values.
public sealed record ProjectMetadata(Project Project, IReadOnlyList<WorksheetMetadata> Worksheets);

public sealed record WorksheetMetadata(Worksheet Worksheet, IReadOnlyList<WorksheetColumn> Columns);
