using YAT.Domain.Entities;

namespace YAT.Application.Abstractions.Persistence;

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(Project project, CancellationToken cancellationToken);

    // Replaces the stored project that has the same Id. Throws EntityNotFoundException when no such project exists.
    Task UpdateAsync(Project project, CancellationToken cancellationToken);
}
