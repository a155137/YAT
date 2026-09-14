namespace YAT.Application.Features.Worksheets.DeleteWorksheetColumns;

// One or more distinct columns of the worksheet, deleted together.
public sealed record DeleteWorksheetColumnsCommand(Guid WorksheetId, IReadOnlyList<Guid> ColumnIds);
