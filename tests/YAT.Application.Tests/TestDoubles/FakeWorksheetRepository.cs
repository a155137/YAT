using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeWorksheetRepository : IWorksheetRepository
{
    private readonly Dictionary<Guid, Worksheet> _stored = [];

    public List<Worksheet> Added { get; } = [];

    public void Seed(Worksheet worksheet) => _stored[worksheet.Id] = worksheet;

    public Task<Worksheet?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_stored.GetValueOrDefault(id));

    public Task AddAsync(Worksheet worksheet, CancellationToken cancellationToken)
    {
        _stored[worksheet.Id] = worksheet;
        Added.Add(worksheet);
        return Task.CompletedTask;
    }
}
