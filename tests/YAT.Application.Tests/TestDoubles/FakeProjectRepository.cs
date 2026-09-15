using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeProjectRepository : IProjectRepository
{
    private readonly Dictionary<Guid, Project> _stored = [];

    public List<Project> Added { get; } = [];

    public List<Project> Updated { get; } = [];

    public void Seed(Project project) => _stored[project.Id] = project;

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_stored.GetValueOrDefault(id));

    public Task AddAsync(Project project, CancellationToken cancellationToken)
    {
        _stored[project.Id] = project;
        Added.Add(project);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Project project, CancellationToken cancellationToken)
    {
        if (!_stored.ContainsKey(project.Id))
        {
            return Task.FromException(new EntityNotFoundException(nameof(Project), project.Id));
        }

        _stored[project.Id] = project;
        Updated.Add(project);
        return Task.CompletedTask;
    }
}
