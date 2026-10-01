using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// The values a graph's filter can be chosen from (Task #049): the distinct values of one Numeric or String column of a
// worksheet, as the raw store lists them - first-occurrence order, at most GraphValueFilter.MaximumDistinctValues, with
// Missing and "more than that" reported apart. Only that small list leaves here, never the column's rows.
//
// A column with metadata but no stored values has no values at all: every worksheet row is missing in it, as the graph
// data query reads it.
public sealed class GraphFilterValuesQueryService
{
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public GraphFilterValuesQueryService(IWorksheetColumnRepository columns, IWorksheetRawDataStore rawDataStore)
    {
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    // Throws GraphDataException when the column is no longer there, cannot filter (neither Numeric nor String) or its
    // values cannot be read.
    public async Task<GraphFilterValues> LoadAsync(Guid worksheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var column = (await _columns.GetByWorksheetIdAsync(worksheetId, cancellationToken))
            .FirstOrDefault(candidate => candidate.Id == columnId)
            ?? throw new GraphDataException(GraphDataError.ColumnUnavailable, "This column is no longer available.");

        if (column.DataType is not (WorksheetDataType.Numeric or WorksheetDataType.String))
        {
            throw new GraphDataException(GraphDataError.InvalidConfiguration, "Only a Numeric or String column can filter a graph.");
        }

        try
        {
            if (!(await _rawDataStore.GetStoredColumnIdsAsync(worksheetId, cancellationToken)).Contains(columnId))
            {
                var hasRows = await _rawDataStore.GetWorksheetRowCountAsync(worksheetId, cancellationToken) > 0;
                return new GraphFilterValues(column.DataType == WorksheetDataType.Numeric
                    ? new NumericRawDistinctValues(columnId, [], hasRows, hasMore: false)
                    : new StringRawDistinctValues(columnId, [], hasRows, hasMore: false));
            }

            return new GraphFilterValues(await _rawDataStore.GetDistinctValuesAsync(
                worksheetId, columnId, GraphValueFilter.MaximumDistinctValues, cancellationToken));
        }
        catch (EntityNotFoundException exception)
        {
            throw new GraphDataException(GraphDataError.ColumnUnavailable, "This column is no longer available.", exception);
        }
        catch (RawDataStorageException exception)
        {
            throw new GraphDataException(GraphDataError.DataReadFailed, "The values of this column could not be read.", exception);
        }
    }
}
