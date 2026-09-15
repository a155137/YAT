using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Infrastructure.Persistence.InMemory;

public sealed class InMemoryWorksheetRepository : IWorksheetRepository
{
    private readonly Dictionary<Guid, Worksheet> _worksheets = [];

    // Worksheet Ids in first-add order: replacing a worksheet (add with the same Id, or update) keeps its position.
    private readonly List<Guid> _creationOrder = [];

    public Task<Worksheet?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_worksheets.GetValueOrDefault(id));

    public Task<IReadOnlyList<Worksheet>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Worksheet> worksheets = _creationOrder
            .Select(id => _worksheets[id])
            .Where(worksheet => worksheet.ProjectId == projectId)
            .ToList();

        return Task.FromResult(worksheets);
    }

    public Task AddAsync(Worksheet worksheet, CancellationToken cancellationToken)
    {
        if (!_worksheets.ContainsKey(worksheet.Id))
        {
            _creationOrder.Add(worksheet.Id);
        }

        _worksheets[worksheet.Id] = worksheet;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Worksheet worksheet, CancellationToken cancellationToken)
    {
        if (!_worksheets.ContainsKey(worksheet.Id))
        {
            return Task.FromException(new EntityNotFoundException(nameof(Worksheet), worksheet.Id));
        }

        _worksheets[worksheet.Id] = worksheet;
        return Task.CompletedTask;
    }
}
