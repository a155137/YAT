namespace YAT.Application.Features.Worksheets.DeleteWorksheetColumn;

public sealed record DeleteWorksheetColumnCommand(Guid WorksheetId, Guid ColumnId);
