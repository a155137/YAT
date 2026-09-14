using System.Globalization;
using System.Text;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Queries;

// Serializes whole worksheet columns as tab-separated text (header row, then one line per row) for the clipboard,
// in the format spreadsheet applications paste as separate columns and rows. Raw values are read in bounded chunks;
// only the resulting text is held in memory, never typed raw columns for the whole worksheet.
public sealed class WorksheetColumnsTsvExporter
{
    // Rows read per raw store call.
    public const int ReadChunkRowCount = 50_000;

    // Excel's sheet limit: 1,048,576 rows including the header row. Larger copies would not paste completely.
    public const int MaxDataRowCount = 1_048_575;

    private const char FieldSeparator = '\t';
    private const string RowSeparator = "\r\n";

    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public WorksheetColumnsTsvExporter(
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository columns,
        IWorksheetRawDataStore rawDataStore)
    {
        _worksheets = worksheets;
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    // Columns are written in worksheet Index order, whatever the order of columnIds. The row count is the longest of
    // the requested columns; shorter columns and columns without stored values produce empty fields.
    // Every row, the last included, ends with CRLF.
    public async Task<string> ExportAsync(Guid worksheetId, IReadOnlyList<Guid> columnIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(columnIds);
        if (columnIds.Count == 0)
        {
            throw new ArgumentException("At least one column id is required.", nameof(columnIds));
        }

        if (columnIds.Distinct().Count() != columnIds.Count)
        {
            throw new ArgumentException("Column ids must not contain duplicates.", nameof(columnIds));
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (await _worksheets.GetByIdAsync(worksheetId, cancellationToken) is null)
        {
            throw new EntityNotFoundException(nameof(Worksheet), worksheetId);
        }

        var worksheetColumns = await _columns.GetByWorksheetIdAsync(worksheetId, cancellationToken);
        var requested = columnIds.ToHashSet();
        var columns = worksheetColumns.Where(column => requested.Contains(column.Id)).ToArray();
        foreach (var columnId in columnIds)
        {
            if (columns.All(column => column.Id != columnId))
            {
                throw new EntityNotFoundException(nameof(WorksheetColumn), columnId);
            }
        }

        var text = new StringBuilder();
        AppendRow(text, columns.Select(column => column.Name).ToArray());

        var storedColumnIds = await _rawDataStore.GetStoredColumnIdsAsync(worksheetId, cancellationToken);
        var storedColumns = columns.Where(column => storedColumnIds.Contains(column.Id)).ToArray();
        if (storedColumns.Length == 0)
        {
            return text.ToString();
        }

        var fields = new string[columns.Length];
        var positions = storedColumns.Select(column => Array.IndexOf(columns, column)).ToArray();
        long rowOffset = 0;
        while (true)
        {
            // The read is as long as the longest requested column, so a short chunk means the data has ended.
            var block = await _rawDataStore.ReadColumnsAsync(
                worksheetId, storedColumns.Select(column => column.Id).ToArray(), rowOffset, ReadChunkRowCount, cancellationToken);

            if (rowOffset + block.RowCount > MaxDataRowCount)
            {
                throw new ValidationException(
                    $"The selected columns have more than {MaxDataRowCount.ToString("N0", CultureInfo.InvariantCulture)} rows, which is more than a spreadsheet can paste.");
            }

            for (var row = 0; row < block.RowCount; row++)
            {
                Array.Fill(fields, string.Empty);
                for (var index = 0; index < positions.Length; index++)
                {
                    fields[positions[index]] = FormatValue(block.Columns[index], row);
                }

                AppendRow(text, fields);
            }

            if (block.RowCount < ReadChunkRowCount)
            {
                return text.ToString();
            }

            rowOffset += block.RowCount;
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    // Empty cells are empty fields. Numbers use the shortest round-trip form in invariant culture, as the grid shows them.
    private static string FormatValue(RawDataColumn values, int row) => values switch
    {
        NumericRawDataColumn numeric => numeric.Values[row]?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        StringRawDataColumn text => text.Values[row] ?? string.Empty,
        _ => string.Empty
    };

    private static void AppendRow(StringBuilder text, IReadOnlyList<string> fields)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            if (index > 0)
            {
                text.Append(FieldSeparator);
            }

            AppendField(text, fields[index]);
        }

        text.Append(RowSeparator);
    }

    // Fields are written verbatim. Spreadsheets treat a field that starts with a quote, or contains a tab or line
    // break, as a quoted field, so only those are quoted (with inner quotes doubled) to paste back unchanged.
    private static void AppendField(StringBuilder text, string field)
    {
        if (field.Length == 0 || (field[0] != '"' && field.IndexOfAny(['\t', '\r', '\n']) < 0))
        {
            text.Append(field);
            return;
        }

        text.Append('"').Append(field.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }
}
