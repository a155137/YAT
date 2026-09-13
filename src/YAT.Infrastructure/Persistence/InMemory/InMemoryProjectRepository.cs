using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Infrastructure.Persistence.InMemory;

public sealed class InMemoryProjectRepository : IProjectRepository
{
    private readonly Dictionary<Guid, Project> _projects = [];

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_projects.GetValueOrDefault(id));

    public Task AddAsync(Project project, CancellationToken cancellationToken)
    {
        _projects[project.Id] = project;
        return Task.CompletedTask;
    }
}
