using YAT.Application.Abstractions.Persistence;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeWorksheetRawDataStore : IWorksheetRawDataStore
{
    public List<(Guid WorksheetId, RawDataBlock Block)> Writes { get; } = [];

    // Thrown by WriteColumnsAsync instead of recording the write.
    public Exception? WriteFailure { get; set; }

    // Runs at the start of WriteColumnsAsync, e.g. to observe metadata state at the moment of the raw write.
    public Action? OnWrite { get; set; }

    public Task WriteColumnsAsync(Guid worksheetId, RawDataBlock block, CancellationToken cancellationToken)
    {
        OnWrite?.Invoke();

        if (WriteFailure is not null)
        {
            throw WriteFailure;
        }

        Writes.Add((worksheetId, block));
        return Task.CompletedTask;
    }

    public Task<RawDataBlock> ReadColumnsAsync(
        Guid worksheetId,
        IReadOnlyList<Guid> columnIds,
        long rowOffset,
        int rowCount,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("Paste execution tests do not read raw data.");
}
