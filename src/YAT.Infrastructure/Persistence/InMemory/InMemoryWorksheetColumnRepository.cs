using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Infrastructure.Persistence.InMemory;

public sealed class InMemoryWorksheetColumnRepository : IWorksheetColumnRepository
{
    private readonly Dictionary<Guid, WorksheetColumn> _columns = [];

    public Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken)
    {
        _columns[column.Id] = column;
        return Task.CompletedTask;
    }
}
