using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeWorksheetColumnRepository : IWorksheetColumnRepository
{
    private readonly Dictionary<Guid, WorksheetColumn> _stored = [];

    public List<WorksheetColumn> Added { get; } = [];

    public List<WorksheetColumn> Updated { get; } = [];

    public List<Guid> Deleted { get; } = [];

    // When set, the metadata write (Add, Update or Delete) with this 1-based number throws instead of storing.
    public (int WriteNumber, Exception Exception)? FailingWrite { get; set; }

    private int WriteCount => Added.Count + Updated.Count + Deleted.Count;

    public void Seed(WorksheetColumn column) => _stored[column.Id] = column;

    public Task<IReadOnlyList<WorksheetColumn>> GetByWorksheetIdAsync(Guid worksheetId, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorksheetColumn> columns = _stored.Values
            .Where(column => column.WorksheetId == worksheetId)
            .OrderBy(column => column.Index)
            .ToList();

        return Task.FromResult(columns);
    }

    public Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken)
    {
        ThrowIfFailingWrite();
        _stored[column.Id] = column;
        Added.Add(column);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(WorksheetColumn column, CancellationToken cancellationToken)
    {
        ThrowIfFailingWrite();
        if (!_stored.ContainsKey(column.Id))
        {
            throw new EntityNotFoundException(nameof(WorksheetColumn), column.Id);
        }

        _stored[column.Id] = column;
        Updated.Add(column);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid columnId, CancellationToken cancellationToken)
    {
        ThrowIfFailingWrite();
        if (!_stored.Remove(columnId))
        {
            throw new EntityNotFoundException(nameof(WorksheetColumn), columnId);
        }

        Deleted.Add(columnId);
        return Task.CompletedTask;
    }

    private void ThrowIfFailingWrite()
    {
        if (FailingWrite is { } failing && failing.WriteNumber == WriteCount + 1)
        {
            throw failing.Exception;
        }
    }
}
