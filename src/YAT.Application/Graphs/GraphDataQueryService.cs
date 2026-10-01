using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// Turns a validated GraphConfiguration into graph-ready observations: it resolves the configured columns, reads their
// raw values in aligned row windows, keeps the rows the configuration's filter selects (Task #049) and applies the
// graph's null rules, keeping worksheet row order.
//
// Row alignment is the point: the configured columns are read together per row window (IWorksheetRawDataStore returns a
// rectangular block, shorter columns padded with null), so X[i], Y[i] and Group[i] always come from the same worksheet
// row. Columns are never read separately and zipped.
//
// It computes nothing: no sorting, grouping, binning, plotting positions or statistics.
public sealed class GraphDataQueryService
{
    // Rows read per raw store call.
    public const int ReadChunkRowCount = 50_000;

    private static readonly GraphConfigurationValidator Validator = new();

    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public GraphDataQueryService(
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository columns,
        IWorksheetRawDataStore rawDataStore)
    {
        _worksheets = worksheets;
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    // The observations of the configuration's graph: ScatterGraphData for a scatter plot, MultiVariableGraphData for a
    // graph whose variable role takes several columns (every graph that reads measured variables, from Task #041 on,
    // even with one variable selected), UnivariateGraphData for a variable role that takes one. Throws
    // GraphDataException when the configuration cannot be used or the data cannot be read.
    public async Task<GraphData> LoadAsync(GraphConfiguration configuration, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        if (!GraphTypeDefinitions.TryGet(configuration.GraphType, out var definition))
        {
            throw new GraphDataException(GraphDataError.UnsupportedGraphType, "This graph type is not supported.");
        }

        if (await _worksheets.GetByIdAsync(configuration.WorksheetId, cancellationToken) is null)
        {
            throw new GraphDataException(GraphDataError.WorksheetUnavailable, "The worksheet of this graph is no longer available.");
        }

        // The configuration is checked against the current column metadata before any values are read.
        var worksheetColumns = await _columns.GetByWorksheetIdAsync(configuration.WorksheetId, cancellationToken);
        Validate(configuration, worksheetColumns);

        var group = FindColumn(configuration, worksheetColumns, GraphVariableRole.Group);

        // The rows the graph keeps (Task #049): null keeps every row, exactly as before there were filters.
        var filter = configuration.Filter is { } valueFilter
            ? new RowFilter(valueFilter, worksheetColumns.First(column => column.Id == valueFilter.ColumnId))
            : null;

        // What the graph data looks like follows the graph's own roles: a scatter plot pairs two measured columns,
        // a role that takes several columns reads one variable per column, and everything else reads one variable.
        if (definition.GraphType == GraphType.ScatterPlot)
        {
            return await LoadScatterAsync(configuration, worksheetColumns, group, filter, cancellationToken);
        }

        return definition.FindRole(GraphVariableRole.Variable)?.AllowsMultiple == true
            ? await LoadMultiVariableAsync(configuration, worksheetColumns, group, filter, cancellationToken)
            : await LoadUnivariateAsync(configuration, worksheetColumns, group, filter, cancellationToken);
    }

    private static void Validate(GraphConfiguration configuration, IReadOnlyList<WorksheetColumn> worksheetColumns)
    {
        // The same rules the setup dialog uses; graph roles are defined once (GraphTypeDefinitions).
        var result = Validator.Validate(configuration, worksheetColumns);
        if (result.IsValid)
        {
            return;
        }

        var error = result.Errors[0];
        throw error.Reason switch
        {
            GraphValidationReason.UnknownGraphType =>
                new GraphDataException(GraphDataError.UnsupportedGraphType, "This graph type is not supported."),
            GraphValidationReason.ColumnNotFound or GraphValidationReason.ColumnFromAnotherWorksheet
                or GraphValidationReason.FilterColumnNotFound or GraphValidationReason.FilterColumnFromAnotherWorksheet =>
                new GraphDataException(GraphDataError.ColumnUnavailable, "A column of this graph is no longer available."),
            _ => new GraphDataException(GraphDataError.InvalidConfiguration, "This graph configuration cannot be used.")
        };
    }

    private async Task<GraphData> LoadScatterAsync(
        GraphConfiguration configuration,
        IReadOnlyList<WorksheetColumn> worksheetColumns,
        WorksheetColumn? group,
        RowFilter? filter,
        CancellationToken cancellationToken)
    {
        var x = FindColumn(configuration, worksheetColumns, GraphVariableRole.X)!;
        var y = FindColumn(configuration, worksheetColumns, GraphVariableRole.Y)!;

        var xValues = new DoubleBuffer();
        var yValues = new DoubleBuffer();
        var groupValues = GroupBuffer.For(group);

        await ReadAsync(configuration.WorksheetId, [x, y], group, filter, (block, reader, row) =>
        {
            // One worksheet row: both values must be present, and the row's own group value travels with it.
            if (reader.Numeric(block, 0, row) is not { } xValue || reader.Numeric(block, 1, row) is not { } yValue)
            {
                return;
            }

            xValues.Add(xValue);
            yValues.Add(yValue);
            groupValues?.Add(block, reader, row);
        }, cancellationToken);

        return new ScatterGraphData(
            configuration.WorksheetId, Info(x), Info(y), xValues.Values, yValues.Values, groupValues?.ToGroupData(Info(group!)));
    }

    private async Task<GraphData> LoadUnivariateAsync(
        GraphConfiguration configuration,
        IReadOnlyList<WorksheetColumn> worksheetColumns,
        WorksheetColumn? group,
        RowFilter? filter,
        CancellationToken cancellationToken)
    {
        var variable = FindColumn(configuration, worksheetColumns, GraphVariableRole.Variable)!;

        var values = new DoubleBuffer();
        var groupValues = GroupBuffer.For(group);

        await ReadAsync(configuration.WorksheetId, [variable], group, filter, (block, reader, row) =>
        {
            if (reader.Numeric(block, 0, row) is not { } value)
            {
                return;
            }

            values.Add(value);
            groupValues?.Add(block, reader, row);
        }, cancellationToken);

        return new UnivariateGraphData(
            configuration.GraphType, configuration.WorksheetId, Info(variable), values.Values, groupValues?.ToGroupData(Info(group!)));
    }

    // Several measured variables of one worksheet: every variable and the group column are read in the same aligned
    // row windows, and each variable then keeps the rows it has a value in.
    //
    // The compaction is per variable on purpose: an empty Reg1 cell must not take that row's Reg2 observation away.
    // Because the group value is taken from the same row while the block is still aligned, every observation keeps the
    // group of the worksheet row it came from, whatever the other variables did with that row.
    private async Task<GraphData> LoadMultiVariableAsync(
        GraphConfiguration configuration,
        IReadOnlyList<WorksheetColumn> worksheetColumns,
        WorksheetColumn? group,
        RowFilter? filter,
        CancellationToken cancellationToken)
    {
        var variables = configuration.FindColumnIds(GraphVariableRole.Variable)
            .Select(columnId => worksheetColumns.First(column => column.Id == columnId))
            .ToArray();

        IReadOnlySet<Guid> storedColumnIds;
        try
        {
            storedColumnIds = await _rawDataStore.GetStoredColumnIdsAsync(configuration.WorksheetId, cancellationToken);
        }
        catch (RawDataStorageException exception)
        {
            throw ReadFailed(exception);
        }

        // A variable without stored raw values simply has no observations; the other variables are still read.
        var readable = variables.Where(column => storedColumnIds.Contains(column.Id)).ToArray();
        var values = variables.Select(_ => new DoubleBuffer()).ToArray();
        var groupValues = variables.Select(_ => GroupBuffer.For(group)).ToArray();
        var ordinals = variables.Select(column => Array.IndexOf(readable, column)).ToArray();

        if (readable.Length > 0)
        {
            await ReadAsync(configuration.WorksheetId, readable, group, filter, (block, reader, row) =>
            {
                // One worksheet row, offered to every variable: each keeps it only if it has a value there, and takes
                // this row's own group value with it.
                for (var index = 0; index < variables.Length; index++)
                {
                    if (ordinals[index] < 0 || reader.Numeric(block, ordinals[index], row) is not { } value)
                    {
                        continue;
                    }

                    values[index].Add(value);
                    groupValues[index]?.Add(block, reader, row);
                }
            }, cancellationToken, storedColumnIds);
        }

        return new MultiVariableGraphData(
            configuration.GraphType,
            configuration.WorksheetId,
            [
                .. variables.Select((column, index) => new UnivariateGraphData(
                    configuration.GraphType,
                    configuration.WorksheetId,
                    Info(column),
                    values[index].Values,
                    groupValues[index]?.ToGroupData(Info(group!))))
            ]);
    }

    // Reads the value columns, the group column and the filter column together, one bounded row window at a time, and
    // offers every row the filter keeps to the caller. A column without stored raw values reads as all-null, like a row
    // beyond a shorter column.
    //
    // The filter decides first, on the row as it is stored, before the caller applies the graph's null rules: a row the
    // filter drops never reaches the graph data, so nothing built from it - series, statistics, display samples - sees it.
    // Without a filter every row is offered, as it always was, and no column is read for it.
    //
    // known: the worksheet's stored column ids when the caller has already looked them up (a graph that reads several
    // variables decides for itself which of them can be read); null asks the store for them.
    private async Task ReadAsync(
        Guid worksheetId,
        IReadOnlyList<WorksheetColumn> valueColumns,
        WorksheetColumn? group,
        RowFilter? filter,
        Action<RawDataBlock, BlockReader, int> onRow,
        CancellationToken cancellationToken,
        IReadOnlySet<Guid>? known = null)
    {
        IReadOnlySet<Guid> storedColumnIds;
        try
        {
            storedColumnIds = known ?? await _rawDataStore.GetStoredColumnIdsAsync(worksheetId, cancellationToken);
        }
        catch (RawDataStorageException exception)
        {
            throw ReadFailed(exception);
        }

        // Without stored values a required column is entirely empty, so no row can be used.
        if (valueColumns.Any(column => !storedColumnIds.Contains(column.Id)))
        {
            return;
        }

        // One request per distinct column: a scatter plot may use the same column for X and Y, and a group column may
        // repeat a value column. Every role then reads its own position of the same aligned block.
        var columnIds = new List<Guid>();
        var valueIndexes = new int[valueColumns.Count];
        for (var index = 0; index < valueColumns.Count; index++)
        {
            valueIndexes[index] = Position(columnIds, valueColumns[index].Id);
        }

        var groupIsStored = group is not null && storedColumnIds.Contains(group.Id);
        var reader = new BlockReader(valueIndexes, groupIsStored ? Position(columnIds, group!.Id) : -1);

        // The filter column has its own position too, shared when it is also the group column or a value column. A filter
        // column without stored values is missing in every row.
        var keeps = filter?.At(storedColumnIds.Contains(filter.Column.Id) ? Position(columnIds, filter.Column.Id) : -1);

        long rowOffset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            RawDataBlock block;
            try
            {
                // Aligned window across all requested columns: shorter columns are padded with null by the store.
                block = await _rawDataStore.ReadColumnsAsync(worksheetId, columnIds, rowOffset, ReadChunkRowCount, cancellationToken);
            }
            catch (EntityNotFoundException exception)
            {
                throw new GraphDataException(GraphDataError.ColumnUnavailable, "A column of this graph is no longer available.", exception);
            }
            catch (RawDataStorageException exception)
            {
                throw ReadFailed(exception);
            }

            for (var row = 0; row < block.RowCount; row++)
            {
                if (keeps is not null && !keeps.Keeps(block, row))
                {
                    continue;
                }

                onRow(block, reader, row);
            }

            // The read is as long as the longest requested column, so a short block means the data has ended.
            if (block.RowCount < ReadChunkRowCount)
            {
                return;
            }

            rowOffset += block.RowCount;
        }
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

    private static GraphDataException ReadFailed(Exception exception) =>
        new(GraphDataError.DataReadFailed, "The worksheet data of this graph could not be read.", exception);

    private static WorksheetColumn? FindColumn(
        GraphConfiguration configuration,
        IReadOnlyList<WorksheetColumn> worksheetColumns,
        GraphVariableRole role) =>
        configuration.FindColumnId(role) is { } columnId
            ? worksheetColumns.FirstOrDefault(column => column.Id == columnId)
            : null;

    private static GraphColumnInfo Info(WorksheetColumn column) => new(column.Id, column.Name, column.DataType);

    // Reads one value of one requested column of a block; the group column may be absent from the block (GroupIndex < 0).
    internal sealed class BlockReader(int[] valueIndexes, int groupIndex)
    {
        public int GroupIndex { get; } = groupIndex;

        // The value of the valueOrdinal-th value column (X is 0 and Y is 1 for a scatter plot) in this row.
        public double? Numeric(RawDataBlock block, int valueOrdinal, int row) =>
            ((NumericRawDataColumn)block.Columns[valueIndexes[valueOrdinal]]).Values[row];

        public double? NumericGroup(RawDataBlock block, int row) =>
            GroupIndex < 0 ? null : ((NumericRawDataColumn)block.Columns[GroupIndex]).Values[row];

        public string? StringGroup(RawDataBlock block, int row) =>
            GroupIndex < 0 ? null : ((StringRawDataColumn)block.Columns[GroupIndex]).Values[row];
    }

    // The graph's filter and the worksheet column it selects rows by, before the column has a place in a read.
    private sealed class RowFilter(GraphValueFilter filter, WorksheetColumn column)
    {
        public WorksheetColumn Column { get; } = column;

        // The filter over the column at this position of every block read; -1 when the column is not read, and so has no
        // value in any row.
        public RowPredicate At(int columnIndex) => filter switch
        {
            NumericValueFilter numeric => new NumericRowPredicate(numeric, columnIndex),
            TextValueFilter text => new TextRowPredicate(text, columnIndex),
            _ => throw new NotSupportedException($"Filter type '{filter.GetType().Name}' is not supported.")
        };
    }

    // Whether one row of a block is kept: its value is a selected one - by exact double equality or ordinal comparison,
    // in the column's own type - or it has no value and Missing is selected.
    private abstract class RowPredicate
    {
        public abstract bool Keeps(RawDataBlock block, int row);
    }

    private sealed class NumericRowPredicate(NumericValueFilter filter, int columnIndex) : RowPredicate
    {
        public override bool Keeps(RawDataBlock block, int row) =>
            (columnIndex < 0 ? null : ((NumericRawDataColumn)block.Columns[columnIndex]).Values[row]) is { } value
                ? filter.Contains(value)
                : filter.IncludeMissing;
    }

    private sealed class TextRowPredicate(TextValueFilter filter, int columnIndex) : RowPredicate
    {
        public override bool Keeps(RawDataBlock block, int row) =>
            (columnIndex < 0 ? null : ((StringRawDataColumn)block.Columns[columnIndex]).Values[row]) is { } value
                ? filter.Contains(value)
                : filter.IncludeMissing;
    }

    // Growable buffer of the kept values. The final data wraps the buffer without copying it again.
    private sealed class DoubleBuffer
    {
        private double[] _items = new double[1024];
        private int _count;

        public ReadOnlyMemory<double> Values => new(_items, 0, _count);

        public void Add(double value)
        {
            if (_count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }

            _items[_count++] = value;
        }
    }

    // The group value of every kept observation, in the group column's own type. A missing group value stays null.
    private abstract class GroupBuffer
    {
        public static GroupBuffer? For(WorksheetColumn? group) => group?.DataType switch
        {
            WorksheetDataType.Numeric => new NumericGroupBuffer(),
            WorksheetDataType.String => new StringGroupBuffer(),
            _ => null
        };

        public abstract void Add(RawDataBlock block, BlockReader reader, int row);

        public abstract GraphGroupData ToGroupData(GraphColumnInfo column);

        private sealed class NumericGroupBuffer : GroupBuffer
        {
            private double?[] _items = new double?[1024];
            private int _count;

            public override void Add(RawDataBlock block, BlockReader reader, int row)
            {
                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, _items.Length * 2);
                }

                _items[_count++] = reader.NumericGroup(block, row);
            }

            public override GraphGroupData ToGroupData(GraphColumnInfo column) => new NumericGroupData(column, new(_items, 0, _count));
        }

        private sealed class StringGroupBuffer : GroupBuffer
        {
            private string?[] _items = new string?[1024];
            private int _count;

            public override void Add(RawDataBlock block, BlockReader reader, int row)
            {
                if (_count == _items.Length)
                {
                    Array.Resize(ref _items, _items.Length * 2);
                }

                _items[_count++] = reader.StringGroup(block, row);
            }

            public override GraphGroupData ToGroupData(GraphColumnInfo column) => new StringGroupData(column, new(_items, 0, _count));
        }
    }
}
