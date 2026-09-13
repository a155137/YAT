using YAT.Domain.Enums;

namespace YAT.Application.Ingestion;

// Metadata-only outcome of one executed paste. It never carries raw cell values.
public sealed record PasteExecutionResult(
    Guid WorksheetId,
    int StartColumnIndex,
    int RowCount,
    IReadOnlyList<PastedColumn> Columns);

// IsNew is true when the paste created the column; false when it replaced an existing column and kept its Id.
public sealed record PastedColumn(
    Guid ColumnId,
    int Index,
    string Name,
    WorksheetDataType DataType,
    bool IsNew);
