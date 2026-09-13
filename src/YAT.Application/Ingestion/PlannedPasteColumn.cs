using YAT.Domain.Enums;

namespace YAT.Application.Ingestion;

public sealed record PlannedPasteColumn(
    int SourceColumnIndex,
    int TargetColumnIndex,
    string OriginalHeader,
    string FinalHeader,
    WorksheetDataType DataType);
