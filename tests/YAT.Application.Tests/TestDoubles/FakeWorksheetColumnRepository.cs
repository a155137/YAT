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

    // Removes a column without recording a delete, e.g. to simulate a column deleted by another operation.
    public void Delete(Guid columnId) => _stored.Remove(columnId);

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

    public Task SaveBatchAsync(
        IReadOnlyList<WorksheetColumn> added,
        IReadOnlyList<WorksheetColumn> updated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(updated);

        var addedCopy = added.ToArray();
        var updatedCopy = updated.ToArray();
        if (addedCopy.Length == 0 && updatedCopy.Length == 0)
        {
            return Task.CompletedTask;
        }

        ThrowIfFailingWrite();
        foreach (var column in updatedCopy)
        {
            if (!_stored.ContainsKey(column.Id))
            {
                throw new EntityNotFoundException(nameof(WorksheetColumn), column.Id);
            }
        }

        foreach (var column in addedCopy)
        {
            _stored[column.Id] = column;
            Added.Add(column);
        }

        foreach (var column in updatedCopy)
        {
            _stored[column.Id] = column;
            Updated.Add(column);
        }

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
