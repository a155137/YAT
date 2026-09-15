namespace YAT.Application.Features.Worksheets.RenameWorksheet;

public sealed record RenameWorksheetCommand(Guid WorksheetId, string Name);
