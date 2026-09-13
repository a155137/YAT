using YAT.Domain.Enums;

namespace YAT.Application.Features.Worksheets.AddWorksheetColumn;

public sealed record AddWorksheetColumnCommand(
    Guid WorksheetId,
    int Index,
    string Name,
    WorksheetDataType DataType,
    ColumnSemanticType? SemanticType,
    string? Unit);
