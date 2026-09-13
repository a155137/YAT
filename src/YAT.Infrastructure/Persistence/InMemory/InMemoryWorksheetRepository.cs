using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Infrastructure.Persistence.InMemory;

public sealed class InMemoryWorksheetRepository : IWorksheetRepository
{
    private readonly Dictionary<Guid, Worksheet> _worksheets = [];

    public Task<Worksheet?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_worksheets.GetValueOrDefault(id));

    public Task AddAsync(Worksheet worksheet, CancellationToken cancellationToken)
    {
        _worksheets[worksheet.Id] = worksheet;
        return Task.CompletedTask;
    }
}
