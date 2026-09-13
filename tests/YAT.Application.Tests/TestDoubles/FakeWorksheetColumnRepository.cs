using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeWorksheetColumnRepository : IWorksheetColumnRepository
{
    public List<WorksheetColumn> Added { get; } = [];

    public Task<IReadOnlyList<WorksheetColumn>> GetByWorksheetIdAsync(Guid worksheetId, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorksheetColumn> columns = Added
            .Where(column => column.WorksheetId == worksheetId)
            .OrderBy(column => column.Index)
            .ToList();

        return Task.FromResult(columns);
    }

    public Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken)
    {
        Added.Add(column);
        return Task.CompletedTask;
    }
}
