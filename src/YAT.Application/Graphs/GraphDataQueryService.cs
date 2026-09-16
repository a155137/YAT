using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Graphs;

// Turns a validated GraphConfiguration into graph-ready observations: it resolves the configured columns, reads their
// raw values in aligned row windows and applies the graph's null rules, keeping worksheet row order.
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

    // The observations of the configuration's graph: ScatterGraphData for a scatter plot, UnivariateGraphData for the
    // single-variable graphs. Throws GraphDataException when the configuration cannot be used or the data cannot be read.
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
        return definition.GraphType == GraphType.ScatterPlot
            ? await LoadScatterAsync(configuration, worksheetColumns, group, cancellationToken)
            : await LoadUnivariateAsync(configuration, worksheetColumns, group, cancellationToken);
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
            GraphValidationReason.ColumnNotFound or GraphValidationReason.ColumnFromAnotherWorksheet =>
                new GraphDataException(GraphDataError.ColumnUnavailable, "A column of this graph is no longer available."),
            _ => new GraphDataException(GraphDataError.InvalidConfiguration, "This graph configuration cannot be used.")
        };
    }

    private async Task<GraphData> LoadScatterAsync(
        GraphConfiguration configuration,
        IReadOnlyList<WorksheetColumn> worksheetColumns,
        WorksheetColumn? group,
        CancellationToken cancellationToken)
    {
        var x = FindColumn(configuration, worksheetColumns, GraphVariableRole.X)!;
        var y = FindColumn(configuration, worksheetColumns, GraphVariableRole.Y)!;

        var xValues = new DoubleBuffer();
        var yValues = new DoubleBuffer();
        var groupValues = GroupBuffer.For(group);

        await ReadAsync(configuration.WorksheetId, [x, y], group, (block, reader, row) =>
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
        CancellationToken cancellationToken)
    {
        var variable = FindColumn(configuration, worksheetColumns, GraphVariableRole.Variable)!;

        var values = new DoubleBuffer();
        var groupValues = GroupBuffer.For(group);

        await ReadAsync(configuration.WorksheetId, [variable], group, (block, reader, row) =>
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

    // Reads the value columns and the group column together, one bounded row window at a time, and offers every row to
    // the caller. A column without stored raw values reads as all-null, like a row beyond a shorter column.
    private async Task ReadAsync(
        Guid worksheetId,
        IReadOnlyList<WorksheetColumn> valueColumns,
        WorksheetColumn? group,
        Action<RawDataBlock, BlockReader, int> onRow,
        CancellationToken cancellationToken)
    {
        IReadOnlySet<Guid> storedColumnIds;
        try
        {
            storedColumnIds = await _rawDataStore.GetStoredColumnIdsAsync(worksheetId, cancellationToken);
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
