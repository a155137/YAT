using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;

namespace YAT.Application.Tests.TestDoubles;

// In-memory raw store with the IWorksheetRawDataStore semantics: whole-column replacement, NULL-padded reads,
// unknown or foreign column ids rejected on read. Records writes and reads for assertions.
internal sealed class FakeWorksheetRawDataStore : IWorksheetRawDataStore
{
    private readonly Dictionary<Guid, (Guid WorksheetId, RawDataColumn Column)> _stored = [];

    public List<(Guid WorksheetId, RawDataBlock Block)> Writes { get; } = [];

    public List<(Guid WorksheetId, IReadOnlyList<Guid> ColumnIds, long RowOffset, int RowCount)> Reads { get; } = [];

    // Thrown by ReadColumnsAsync instead of returning a block.
    public Exception? ReadFailure { get; set; }

    // Thrown by WriteColumnsAsync instead of recording the write.
    public Exception? WriteFailure { get; set; }

    // Runs at the start of WriteColumnsAsync, e.g. to observe metadata state at the moment of the raw write.
    public Action? OnWrite { get; set; }

    // Stores a live raw column without recording a write.
    public void Seed(Guid worksheetId, RawDataColumn column) => _stored[column.ColumnId] = (worksheetId, column);

    public Task WriteColumnsAsync(Guid worksheetId, RawDataBlock block, CancellationToken cancellationToken)
    {
        OnWrite?.Invoke();

        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Writes.Add((worksheetId, block));
        foreach (var column in block.Columns)
        {
            _stored[column.ColumnId] = (worksheetId, column);
        }

        return Task.CompletedTask;
    }

    public Task<RawDataBlock> ReadColumnsAsync(
        Guid worksheetId,
        IReadOnlyList<Guid> columnIds,
        long rowOffset,
        int rowCount,
        CancellationToken cancellationToken)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        Reads.Add((worksheetId, columnIds.ToArray(), rowOffset, rowCount));

        var columns = columnIds
            .Select(id => _stored.TryGetValue(id, out var entry) && entry.WorksheetId == worksheetId
                ? entry.Column
                : throw new EntityNotFoundException("RawDataColumn", id))
            .ToArray();

        var longest = columns.Max(column => column.RowCount);
        var resultRowCount = rowCount == 0 || rowOffset >= longest ? 0 : (int)Math.Min(rowCount, longest - rowOffset);

        RawDataColumn[] window = columns
            .Select(column => column switch
            {
                NumericRawDataColumn numeric => (RawDataColumn)new NumericRawDataColumn(numeric.ColumnId, Window(numeric.Values, rowOffset, resultRowCount)),
                StringRawDataColumn text => new StringRawDataColumn(text.ColumnId, Window(text.Values, rowOffset, resultRowCount)),
                _ => throw new NotSupportedException()
            })
            .ToArray();

        return Task.FromResult(new RawDataBlock(window));
    }

    public List<(Guid WorksheetId, IReadOnlyList<Guid> ColumnIds)> Deletes { get; } = [];

    // Thrown by DeleteColumnsAsync instead of retiring the columns.
    public Exception? DeleteFailure { get; set; }

    // Runs at the start of DeleteColumnsAsync, e.g. to observe metadata state at the moment of the raw delete.
    public Action? OnDelete { get; set; }

    public Task DeleteColumnsAsync(Guid worksheetId, IReadOnlyList<Guid> columnIds, CancellationToken cancellationToken)
    {
        OnDelete?.Invoke();

        if (DeleteFailure is not null)
        {
            throw DeleteFailure;
        }

        foreach (var id in columnIds)
        {
            if (!_stored.TryGetValue(id, out var entry) || entry.WorksheetId != worksheetId)
            {
                throw new EntityNotFoundException("RawDataColumn", id);
            }
        }

        Deletes.Add((worksheetId, columnIds.ToArray()));
        foreach (var id in columnIds)
        {
            _stored.Remove(id);
        }

        return Task.CompletedTask;
    }

    public Task<long> GetWorksheetRowCountAsync(Guid worksheetId, CancellationToken cancellationToken) =>
        Task.FromResult(_stored.Values
            .Where(entry => entry.WorksheetId == worksheetId)
            .Select(entry => (long)entry.Column.RowCount)
            .DefaultIfEmpty(0)
            .Max());

    public Task<IReadOnlySet<Guid>> GetStoredColumnIdsAsync(Guid worksheetId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlySet<Guid>>(_stored
            .Where(pair => pair.Value.WorksheetId == worksheetId)
            .Select(pair => pair.Key)
            .ToHashSet());

    private static T?[] Window<T>(IReadOnlyList<T?> values, long rowOffset, int rowCount)
    {
        var window = new T?[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            var source = rowOffset + row;
            window[row] = source < values.Count ? values[(int)source] : default;
        }

        return window;
    }
}
