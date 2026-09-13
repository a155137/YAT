namespace YAT.Application.Features.Worksheets.CreateWorksheet;

public sealed record CreateWorksheetCommand(Guid ProjectId, string Name);
