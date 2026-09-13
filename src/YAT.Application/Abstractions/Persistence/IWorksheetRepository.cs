using YAT.Domain.Entities;

namespace YAT.Application.Abstractions.Persistence;

public interface IWorksheetRepository
{
    Task<Worksheet?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(Worksheet worksheet, CancellationToken cancellationToken);
}
