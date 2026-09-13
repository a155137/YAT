namespace YAT.Application.Ingestion;

// Short-lived, metadata-only description of where one rectangular pasted block lands.
// It holds no cell values; the caller keeps the ParsedTabularData for the same operation.
public sealed class WorksheetPastePlan
{
    internal WorksheetPastePlan(
        Guid worksheetId,
        int startColumnIndex,
        int columnCount,
        int dataRowCount,
        IReadOnlyList<PlannedPasteColumn> columns)
    {
        WorksheetId = worksheetId;
        StartColumnIndex = startColumnIndex;
        ColumnCount = columnCount;
        DataRowCount = dataRowCount;
        Columns = columns;
    }

    public Guid WorksheetId { get; }

    public int StartColumnIndex { get; }

    public int ColumnCount { get; }

    public int DataRowCount { get; }

    public IReadOnlyList<PlannedPasteColumn> Columns { get; }
}
