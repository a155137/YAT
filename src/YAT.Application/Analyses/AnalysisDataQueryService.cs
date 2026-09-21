using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Analyses;

// Turns a validated AnalysisConfiguration into the worksheet rows an analysis reads: it resolves the configured
// columns and reads their raw values in aligned row windows, keeping every logical row of the worksheet in its place.
//
// Two things are the point here:
//
//   Row alignment. The configured columns are read together per row window (IWorksheetRawDataStore returns a
//   rectangular block, shorter columns padded with null), so Variable[i] and Group[i] always come from the same
//   worksheet row. Columns are never read separately and zipped.
//
//   Missing observations. Nothing is filtered out: a row without a value keeps its place as null, and the data spans
//   the worksheet's logical row count rather than the longest selected column, so what a variable reports as missing
//   does not change because another variable was selected with it.
//
// It computes nothing: no grouping, sorting or statistics.
public sealed class AnalysisDataQueryService
{
    // Rows read per raw store call, as for graph data.
    public const int ReadChunkRowCount = 50_000;

    private static readonly AnalysisConfigurationValidator Validator = new();

    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public AnalysisDataQueryService(
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository columns,
        IWorksheetRawDataStore rawDataStore)
    {
        _worksheets = worksheets;
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    // The worksheet rows of the configuration's analysis. Throws AnalysisDataException when the configuration cannot
    // be used or the data cannot be read.
    public async Task<AnalysisData> LoadAsync(AnalysisConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        if (await _worksheets.GetByIdAsync(configuration.WorksheetId, cancellationToken) is null)
        {
            throw new AnalysisDataException(
                AnalysisDataError.WorksheetUnavailable, "The worksheet of this analysis is no longer available.");
        }

        // The configuration is checked against the current column metadata before any values are read.
        var worksheetColumns = await _columns.GetByWorksheetIdAsync(configuration.WorksheetId, cancellationToken);
        Validate(configuration, worksheetColumns);

        var variables = configuration.VariableColumnIds.Select(id => Find(worksheetColumns, id)!).ToArray();
        var group = configuration.GroupColumnId is { } groupId ? Find(worksheetColumns, groupId) : null;

        long worksheetRowCount;
        IReadOnlySet<Guid> storedColumnIds;
        try
        {
            // The worksheet's own logical row count decides how many rows every variable is reported over.
            worksheetRowCount = await _rawDataStore.GetWorksheetRowCountAsync(configuration.WorksheetId, cancellationToken);
            storedColumnIds = await _rawDataStore.GetStoredColumnIdsAsync(configuration.WorksheetId, cancellationToken);
        }
        catch (RawDataStorageException exception)
        {
            throw ReadFailed(exception);
        }

        if (worksheetRowCount > int.MaxValue)
        {
            throw new AnalysisDataException(AnalysisDataError.WorksheetTooLarge, "This worksheet has too many rows to analyse.");
        }

        var rowCount = (int)worksheetRowCount;
        var values = variables.Select(_ => new double?[rowCount]).ToArray();
        var numericGroup = group?.DataType == WorksheetDataType.Numeric ? new double?[rowCount] : null;
        var textGroup = group?.DataType == WorksheetDataType.String ? new string?[rowCount] : null;

        await ReadAsync(configuration, variables, group, storedColumnIds, rowCount, values, numericGroup, textGroup, cancellationToken);

        return new AnalysisData(
            configuration.WorksheetId,
            rowCount,
            [.. variables.Select((column, index) => new AnalysisVariableData(Info(column), values[index]))],
            GroupData(group, numericGroup, textGroup));
    }

    // Reads the variables and the group column together, one bounded row window at a time, into the row-aligned
    // buffers. Columns without stored raw values are never requested: they are simply all null, like the rows beyond
    // a shorter column.
    private async Task ReadAsync(
        AnalysisConfiguration configuration,
        IReadOnlyList<WorksheetColumn> variables,
        WorksheetColumn? group,
        IReadOnlySet<Guid> storedColumnIds,
        int rowCount,
        double?[][] values,
        double?[]? numericGroup,
        string?[]? textGroup,
        CancellationToken cancellationToken)
    {
        // One request per distinct column: the group column may repeat a variable, and each role then reads its own
        // position of the same aligned block.
        var columnIds = new List<Guid>();
        var valuePositions = new int[variables.Count];
        for (var index = 0; index < variables.Count; index++)
        {
            valuePositions[index] = storedColumnIds.Contains(variables[index].Id) ? Position(columnIds, variables[index].Id) : -1;
        }

        var groupPosition = group is not null && storedColumnIds.Contains(group.Id) ? Position(columnIds, group.Id) : -1;
        if (columnIds.Count == 0 || rowCount == 0)
        {
            return;
        }

        long rowOffset = 0;
        while (rowOffset < rowCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RawDataBlock block;
            try
            {
                // Aligned window across all requested columns: shorter columns are padded with null by the store.
                var window = (int)Math.Min(ReadChunkRowCount, rowCount - rowOffset);
                block = await _rawDataStore.ReadColumnsAsync(configuration.WorksheetId, columnIds, rowOffset, window, cancellationToken);
            }
            catch (EntityNotFoundException exception)
            {
                throw new AnalysisDataException(
                    AnalysisDataError.ColumnUnavailable, "A column of this analysis is no longer available.", exception);
            }
            catch (RawDataStorageException exception)
            {
                throw ReadFailed(exception);
            }

            // The read is as long as the longest requested column, so an empty block means the selected columns have
            // ended: the worksheet's remaining rows stay null for all of them.
            if (block.RowCount == 0)
            {
                return;
            }

            var offset = (int)rowOffset;
            for (var index = 0; index < variables.Count; index++)
            {
                if (valuePositions[index] < 0)
                {
                    continue;
                }

                var stored = ((NumericRawDataColumn)block.Columns[valuePositions[index]]).Values;
                var target = values[index];
                for (var row = 0; row < block.RowCount; row++)
                {
                    target[offset + row] = stored[row];
                }
            }

            if (groupPosition >= 0 && numericGroup is not null)
            {
                var stored = ((NumericRawDataColumn)block.Columns[groupPosition]).Values;
                for (var row = 0; row < block.RowCount; row++)
                {
                    numericGroup[offset + row] = stored[row];
                }
            }
            else if (groupPosition >= 0 && textGroup is not null)
            {
                var stored = ((StringRawDataColumn)block.Columns[groupPosition]).Values;
                for (var row = 0; row < block.RowCount; row++)
                {
                    textGroup[offset + row] = stored[row];
                }
            }

            rowOffset += block.RowCount;
        }
    }

    private static void Validate(AnalysisConfiguration configuration, IReadOnlyList<WorksheetColumn> worksheetColumns)
    {
        // The same rules the setup dialog uses.
        var result = Validator.Validate(configuration, worksheetColumns);
        if (result.IsValid)
        {
            return;
        }

        var error = result.Errors[0];
        throw error.Reason switch
        {
            AnalysisValidationReason.ColumnNotFound or AnalysisValidationReason.ColumnFromAnotherWorksheet =>
                new AnalysisDataException(AnalysisDataError.ColumnUnavailable, "A column of this analysis is no longer available."),
            _ => new AnalysisDataException(AnalysisDataError.InvalidConfiguration, "This analysis cannot be run with the current settings.")
        };
    }

    private static AnalysisGroupData? GroupData(WorksheetColumn? group, double?[]? numericGroup, string?[]? textGroup)
    {
        if (group is null)
        {
            return null;
        }

        if (numericGroup is not null)
        {
            return new NumericAnalysisGroupData(Info(group), numericGroup);
        }

        return textGroup is not null ? new StringAnalysisGroupData(Info(group), textGroup) : null;
    }

    // The position of a column in the request, appending it when it is requested for the first time.
    private static int Position(List<Guid> columnIds, Guid columnId)
    {
        var index = columnIds.IndexOf(columnId);
        if (index < 0)
        {
            index = columnIds.Count;
            columnIds.Add(columnId);
        }

        return index;
    }

    private static AnalysisDataException ReadFailed(Exception exception) =>
        new(AnalysisDataError.DataReadFailed, "The worksheet data of this analysis could not be read.", exception);

    private static WorksheetColumn? Find(IReadOnlyList<WorksheetColumn> worksheetColumns, Guid columnId) =>
        worksheetColumns.FirstOrDefault(column => column.Id == columnId);

    private static AnalysisColumnInfo Info(WorksheetColumn column) => new(column.Id, column.Name, column.DataType);
}
