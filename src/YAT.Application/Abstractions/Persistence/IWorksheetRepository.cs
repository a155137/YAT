using YAT.Domain.Entities;

namespace YAT.Application.Abstractions.Persistence;

public interface IWorksheetRepository
{
    Task<Worksheet?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    // Returns the project's worksheets in creation (first add) order; empty when it has none.
    Task<IReadOnlyList<Worksheet>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken);

    Task AddAsync(Worksheet worksheet, CancellationToken cancellationToken);

    // Replaces the stored worksheet that has the same Id. Throws EntityNotFoundException when no such worksheet exists.
    Task UpdateAsync(Worksheet worksheet, CancellationToken cancellationToken);
}
