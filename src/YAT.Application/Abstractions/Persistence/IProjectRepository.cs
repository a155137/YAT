using YAT.Domain.Entities;

namespace YAT.Application.Abstractions.Persistence;

public interface IProjectRepository
{
    Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task AddAsync(Project project, CancellationToken cancellationToken);
}
