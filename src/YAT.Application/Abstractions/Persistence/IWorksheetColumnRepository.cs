using YAT.Domain.Entities;

namespace YAT.Application.Abstractions.Persistence;

public interface IWorksheetColumnRepository
{
    // Returns the worksheet's column metadata ordered by Index; empty when it has none.
    Task<IReadOnlyList<WorksheetColumn>> GetByWorksheetIdAsync(Guid worksheetId, CancellationToken cancellationToken);

    Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken);

    // Replaces the stored column that has the same Id. Throws EntityNotFoundException when no such column exists.
    Task UpdateAsync(WorksheetColumn column, CancellationToken cancellationToken);

    // Removes the column with this Id. Other columns are not changed (no reindexing). Throws EntityNotFoundException
    // when no such column exists. A deleted Id is never reused.
    Task DeleteAsync(Guid columnId, CancellationToken cancellationToken);
}
