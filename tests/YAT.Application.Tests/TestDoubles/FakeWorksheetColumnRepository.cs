using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeWorksheetColumnRepository : IWorksheetColumnRepository
{
    public List<WorksheetColumn> Added { get; } = [];

    public Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken)
    {
        Added.Add(column);
        return Task.CompletedTask;
    }
}
