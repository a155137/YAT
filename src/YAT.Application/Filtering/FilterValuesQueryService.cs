using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Enums;

namespace YAT.Application.Filtering;

// The values a value-set condition of a row filter can be chosen from (Tasks #049, #053), for graphs and analyses alike:
// the distinct values of one Numeric or String column of a worksheet, as the raw store lists them - first-occurrence
// order, at most ValueSetCondition.MaximumDistinctValues, with Missing and "more than that" reported apart. Only that
// small list leaves here, never the column's rows.
//
// A column with metadata but no stored values has no values at all: every worksheet row is missing in it, as the graph
// data query reads it.
public sealed class FilterValuesQueryService
{
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public FilterValuesQueryService(IWorksheetColumnRepository columns, IWorksheetRawDataStore rawDataStore)
    {
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    // Throws RowFilterException when the column is no longer there, cannot filter (neither Numeric nor String) or its
    // values cannot be read.
    public async Task<FilterValues> LoadAsync(Guid worksheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var column = (await _columns.GetByWorksheetIdAsync(worksheetId, cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == columnId)
            ?? throw new RowFilterException(RowFilterError.ColumnUnavailable, "This column is no longer available.");

        if (column.DataType is not (WorksheetDataType.Numeric or WorksheetDataType.String))
        {
            throw new RowFilterException(RowFilterError.ColumnCannotFilter, "Only a Numeric or String column can filter rows.");
        }

        try
        {
            if (!(await _rawDataStore.GetStoredColumnIdsAsync(worksheetId, cancellationToken)).Contains(columnId))
            {
                var hasRows = await _rawDataStore.GetWorksheetRowCountAsync(worksheetId, cancellationToken) > 0;
                return new FilterValues(column.DataType == WorksheetDataType.Numeric
                    ? new NumericRawDistinctValues(columnId, [], hasRows, hasMore: false)
                    : new StringRawDistinctValues(columnId, [], hasRows, hasMore: false));
            }

            return new FilterValues(await _rawDataStore.GetDistinctValuesAsync(
                worksheetId, columnId, ValueSetCondition.MaximumDistinctValues, cancellationToken));
        }
        catch (EntityNotFoundException exception)
        {
            throw new RowFilterException(RowFilterError.ColumnUnavailable, "This column is no longer available.", exception);
        }
        catch (RawDataStorageException exception)
        {
            throw new RowFilterException(RowFilterError.DataReadFailed, "The values of this column could not be read.", exception);
        }
    }
}
