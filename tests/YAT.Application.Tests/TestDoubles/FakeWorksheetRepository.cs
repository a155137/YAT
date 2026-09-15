using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeWorksheetRepository : IWorksheetRepository
{
    private readonly Dictionary<Guid, Worksheet> _stored = [];
    private readonly List<Guid> _order = [];

    public List<Worksheet> Added { get; } = [];

    public List<Worksheet> Updated { get; } = [];

    public void Seed(Worksheet worksheet) => Store(worksheet);

    public Task<Worksheet?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_stored.GetValueOrDefault(id));

    public Task<IReadOnlyList<Worksheet>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken)
    {
        IReadOnlyList<Worksheet> worksheets = _order.Select(id => _stored[id]).Where(worksheet => worksheet.ProjectId == projectId).ToList();
        return Task.FromResult(worksheets);
    }

    public Task AddAsync(Worksheet worksheet, CancellationToken cancellationToken)
    {
        Store(worksheet);
        Added.Add(worksheet);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Worksheet worksheet, CancellationToken cancellationToken)
    {
        if (!_stored.ContainsKey(worksheet.Id))
        {
            return Task.FromException(new EntityNotFoundException(nameof(Worksheet), worksheet.Id));
        }

        _stored[worksheet.Id] = worksheet;
        Updated.Add(worksheet);
        return Task.CompletedTask;
    }

    private void Store(Worksheet worksheet)
    {
        if (!_stored.ContainsKey(worksheet.Id))
        {
            _order.Add(worksheet.Id);
        }

        _stored[worksheet.Id] = worksheet;
    }
}
