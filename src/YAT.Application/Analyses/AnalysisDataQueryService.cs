using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Filtering;
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
//   Missing observations. Nothing is dropped for being empty: a row without a value keeps its place as null, and the
//   data spans the worksheet's logical row count rather than the longest selected column, so what a variable reports
//   as missing does not change because another variable was selected with it.
//
//   The row filter (Task #053). With one, only the rows it keeps are the analysis's rows, in worksheet order: RowCount
//   is how many it kept, and every count and statistic - N, Missing, Mean, StDev, capability - is over them. Its
//   columns are read in the same aligned windows (RowFilterEvaluator); a row beyond every column read has no value in
//   any of them, and is kept or not as the filter treats a row without values.
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

        var kept = await ReadAsync(configuration, variables, group, storedColumnIds, rowCount, values, numericGroup, textGroup, cancellationToken);

        return new AnalysisData(
            configuration.WorksheetId,
            kept,
            [.. variables.Select((column, index) => new AnalysisVariableData(Info(column), new ReadOnlyMemory<double?>(values[index], 0, kept)))],
            GroupData(group, numericGroup, textGroup, kept));
    }

    // Reads the variables, the group column and the filter's columns together, one bounded row window at a time, into the
    // row-aligned buffers, and returns how many rows the analysis has: every worksheet row without a filter, the rows it
    // keeps - written to the front of the buffers in worksheet order - with one. Columns without stored raw values are
    // never requested: they are simply all null, like the rows beyond a shorter column.
    private async Task<int> ReadAsync(
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

        // The filter's columns, each once - shared with a variable or the group where it is one of them.
        RowFilterEvaluator? evaluator = null;
        if (configuration.Filter is { } filter)
        {
            var positions = filter.ColumnIds.ToDictionary(id => id, id => storedColumnIds.Contains(id) ? Position(columnIds, id) : -1);
            evaluator = RowFilterEvaluator.Create(filter, id => positions[id]);
        }

        if (rowCount == 0)
        {
            return 0;
        }

        if (columnIds.Count == 0)
        {
            // Nothing stored to read: every row has no value in any column, and is kept as the filter treats such a row.
            return evaluator is null || evaluator.KeepsRowWithoutValues ? rowCount : 0;
        }

        if (evaluator is not null)
        {
            return await ReadFilteredAsync(configuration, evaluator, columnIds, valuePositions, groupPosition, rowCount, values, numericGroup, textGroup, cancellationToken);
        }

        long rowOffset = 0;
        while (rowOffset < rowCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var block = await ReadBlockAsync(configuration.WorksheetId, columnIds, rowOffset, rowCount, cancellationToken);

            // The read is as long as the longest requested column, so an empty block means the selected columns have
            // ended: the worksheet's remaining rows stay null for all of them.
            if (block.RowCount == 0)
            {
                return rowCount;
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

        return rowCount;
    }

    // The same read with a filter: each window's kept rows are written one after another to the front of the buffers, so
    // the analysis's row i is the i-th kept worksheet row. Rows beyond every column read are kept or not as the filter
    // treats a row without values (their variables and group are null either way).
    private async Task<int> ReadFilteredAsync(
        AnalysisConfiguration configuration,
        RowFilterEvaluator evaluator,
        List<Guid> columnIds,
        int[] valuePositions,
        int groupPosition,
        int rowCount,
        double?[][] values,
        double?[]? numericGroup,
        string?[]? textGroup,
        CancellationToken cancellationToken)
    {
        var kept = 0;
        long rowOffset = 0;
        while (rowOffset < rowCount)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var block = await ReadBlockAsync(configuration.WorksheetId, columnIds, rowOffset, rowCount, cancellationToken);
            if (block.RowCount == 0)
            {
                return evaluator.KeepsRowWithoutValues ? kept + (int)(rowCount - rowOffset) : kept;
            }

            var stored = new IReadOnlyList<double?>?[values.Length];
            for (var index = 0; index < values.Length; index++)
            {
                stored[index] = valuePositions[index] < 0 ? null : ((NumericRawDataColumn)block.Columns[valuePositions[index]]).Values;
            }

            var numericGroupValues = groupPosition >= 0 && numericGroup is not null ? ((NumericRawDataColumn)block.Columns[groupPosition]).Values : null;
            var textGroupValues = groupPosition >= 0 && textGroup is not null ? ((StringRawDataColumn)block.Columns[groupPosition]).Values : null;

            var bound = evaluator.Bind(block);
            for (var row = 0; row < block.RowCount; row++)
            {
                if (!bound.Keeps(row))
                {
                    continue;
                }

                for (var index = 0; index < values.Length; index++)
                {
                    if (stored[index] is { } column)
                    {
                        values[index][kept] = column[row];
                    }
                }

                if (numericGroupValues is not null)
                {
                    numericGroup![kept] = numericGroupValues[row];
                }
                else if (textGroupValues is not null)
                {
                    textGroup![kept] = textGroupValues[row];
                }

                kept++;
            }

            rowOffset += block.RowCount;
        }

        return kept;
    }

    // One aligned window across all requested columns, from rowOffset and at most to the worksheet's last row: shorter
    // columns are padded with null by the store.
    private async Task<RawDataBlock> ReadBlockAsync(Guid worksheetId, List<Guid> columnIds, long rowOffset, int rowCount, CancellationToken cancellationToken)
    {
        try
        {
            var window = (int)Math.Min(ReadChunkRowCount, rowCount - rowOffset);
            return await _rawDataStore.ReadColumnsAsync(worksheetId, columnIds, rowOffset, window, cancellationToken);
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
            AnalysisValidationReason.ColumnNotFound or AnalysisValidationReason.ColumnFromAnotherWorksheet
                or AnalysisValidationReason.FilterColumnNotFound or AnalysisValidationReason.FilterColumnFromAnotherWorksheet =>
                new AnalysisDataException(AnalysisDataError.ColumnUnavailable, "A column of this analysis is no longer available."),
            _ => new AnalysisDataException(AnalysisDataError.InvalidConfiguration, "This analysis cannot be run with the current settings.")
        };
    }

    private static AnalysisGroupData? GroupData(WorksheetColumn? group, double?[]? numericGroup, string?[]? textGroup, int count)
    {
        if (group is null)
        {
            return null;
        }

        if (numericGroup is not null)
        {
            return new NumericAnalysisGroupData(Info(group), new ReadOnlyMemory<double?>(numericGroup, 0, count));
        }

        return textGroup is not null ? new StringAnalysisGroupData(Info(group), new ReadOnlyMemory<string?>(textGroup, 0, count)) : null;
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
