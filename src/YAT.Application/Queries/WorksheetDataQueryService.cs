using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Queries;

// Loads a bounded page of worksheet data: column metadata from the repository, values from the raw data store.
// Only columns with stored raw values are read; columns that exist only as metadata get empty (null) values.
public sealed class WorksheetDataQueryService
{
    public const int MaxPageRowCount = 500;

    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public WorksheetDataQueryService(
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository columns,
        IWorksheetRawDataStore rawDataStore)
    {
        _worksheets = worksheets;
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    // Returns at most rowCount (≤ MaxPageRowCount) rows starting at rowOffset; fewer at the end of the data,
    // and none when rowOffset is at or past the last row.
    public async Task<WorksheetDataPage> GetPageAsync(
        Guid worksheetId,
        long rowOffset,
        int rowCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rowCount, MaxPageRowCount);
        cancellationToken.ThrowIfCancellationRequested();

        if (await _worksheets.GetByIdAsync(worksheetId, cancellationToken) is null)
        {
            throw new EntityNotFoundException(nameof(Worksheet), worksheetId);
        }

        var columns = await _columns.GetByWorksheetIdAsync(worksheetId, cancellationToken);
        var totalRowCount = await _rawDataStore.GetWorksheetRowCountAsync(worksheetId, cancellationToken);
        var pageRowCount = rowOffset >= totalRowCount ? 0 : (int)Math.Min(rowCount, totalRowCount - rowOffset);

        var storedValues = new Dictionary<Guid, RawDataColumn>();
        if (columns.Count > 0 && pageRowCount > 0)
        {
            var storedColumnIds = await _rawDataStore.GetStoredColumnIdsAsync(worksheetId, cancellationToken);
            var idsToRead = columns.Select(column => column.Id).Where(storedColumnIds.Contains).ToArray();
            if (idsToRead.Length > 0)
            {
                var block = await _rawDataStore.ReadColumnsAsync(worksheetId, idsToRead, rowOffset, pageRowCount, cancellationToken);
                foreach (var values in block.Columns)
                {
                    storedValues[values.ColumnId] = values;
                }
            }
        }

        var pageColumns = columns
            .Select(column => new WorksheetDataPageColumn(
                column,
                storedValues.TryGetValue(column.Id, out var values)
                    ? FitToRowCount(values, pageRowCount)
                    : EmptyValues(column, pageRowCount)))
            .ToArray();

        return new WorksheetDataPage(worksheetId, totalRowCount, rowOffset, pageRowCount, Array.AsReadOnly(pageColumns));
    }

    // The read is padded to the longest requested column; raw columns without metadata can make the worksheet
    // longer than that, so values are padded with nulls up to the page row count.
    private static RawDataColumn FitToRowCount(RawDataColumn values, int rowCount)
    {
        if (values.RowCount == rowCount)
        {
            return values;
        }

        return values switch
        {
            NumericRawDataColumn numeric => new NumericRawDataColumn(numeric.ColumnId, Pad(numeric.Values, rowCount)),
            StringRawDataColumn text => new StringRawDataColumn(text.ColumnId, Pad(text.Values, rowCount)),
            _ => throw new InvalidOperationException($"Unsupported raw data column type {values.GetType().Name}.")
        };
    }

    private static T?[] Pad<T>(IReadOnlyList<T?> values, int rowCount)
    {
        var padded = new T?[rowCount];
        for (var row = 0; row < Math.Min(values.Count, rowCount); row++)
        {
            padded[row] = values[row];
        }

        return padded;
    }

    private static RawDataColumn EmptyValues(WorksheetColumn column, int rowCount) =>
        column.DataType == WorksheetDataType.Numeric
            ? new NumericRawDataColumn(column.Id, new double?[rowCount])
            : new StringRawDataColumn(column.Id, new string?[rowCount]);
}
