using System.Globalization;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Queries;
using YAT.Domain.Enums;

namespace YAT.app.Composition;

// Display projection of one bounded WorksheetDataPage: column descriptors plus at most one page of rows holding
// display text only. Built in the App layer; the UI never receives typed raw columns or the whole dataset.
public sealed class WorksheetGridPage
{
    internal static readonly WorksheetGridPage Empty = new(Guid.Empty, 0, 0, [], []);

    internal WorksheetGridPage(
        Guid worksheetId,
        long totalRowCount,
        long rowOffset,
        IReadOnlyList<WorksheetGridColumn> columns,
        IReadOnlyList<WorksheetGridRow> rows)
    {
        WorksheetId = worksheetId;
        TotalRowCount = totalRowCount;
        RowOffset = rowOffset;
        Columns = columns;
        Rows = rows;
    }

    public Guid WorksheetId { get; }

    public long TotalRowCount { get; }

    public long RowOffset { get; }

    // Ordered by WorksheetColumn.Index; Cells of every row follow the same order.
    public IReadOnlyList<WorksheetGridColumn> Columns { get; }

    public IReadOnlyList<WorksheetGridRow> Rows { get; }

    internal static WorksheetGridPage FromDataPage(WorksheetDataPage page)
    {
        var columns = page.Columns
            .Select(column => new WorksheetGridColumn(column.Column.Id, column.Column.Index, column.Column.Name, column.Column.DataType))
            .ToArray();

        var rows = new WorksheetGridRow[page.RowCount];
        for (var row = 0; row < rows.Length; row++)
        {
            var cells = new string[page.Columns.Count];
            for (var column = 0; column < cells.Length; column++)
            {
                cells[column] = FormatCell(page.Columns[column].Values, row);
            }

            rows[row] = new WorksheetGridRow(page.RowOffset + row + 1, Array.AsReadOnly(cells));
        }

        return new WorksheetGridPage(page.WorksheetId, page.TotalRowCount, page.RowOffset, Array.AsReadOnly(columns), Array.AsReadOnly(rows));
    }

    // Empty cells are blank. Numbers use the shortest round-trip form in invariant culture (e.g. 0.132, 1.57E-06).
    internal static string FormatCell(RawDataColumn values, int row) => values switch
    {
        NumericRawDataColumn numeric => numeric.Values[row]?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        StringRawDataColumn text => text.Values[row] ?? string.Empty,
        _ => string.Empty
    };
}

public sealed record WorksheetGridColumn(Guid ColumnId, int Index, string Name, WorksheetDataType DataType);

// RowNumber is the 1-based worksheet row shown in the grid; it is display-only.
public sealed record WorksheetGridRow(long RowNumber, IReadOnlyList<string> Cells);
