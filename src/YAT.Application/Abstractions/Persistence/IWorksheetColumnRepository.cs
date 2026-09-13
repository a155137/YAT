using YAT.Domain.Entities;

namespace YAT.Application.Abstractions.Persistence;

public interface IWorksheetColumnRepository
{
    // Returns the worksheet's column metadata ordered by Index; empty when it has none.
    Task<IReadOnlyList<WorksheetColumn>> GetByWorksheetIdAsync(Guid worksheetId, CancellationToken cancellationToken);

    Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken);
}
