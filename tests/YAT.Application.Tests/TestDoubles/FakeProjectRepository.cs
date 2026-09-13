using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Application.Tests.TestDoubles;

internal sealed class FakeProjectRepository : IProjectRepository
{
    private readonly Dictionary<Guid, Project> _stored = [];

    public List<Project> Added { get; } = [];

    public void Seed(Project project) => _stored[project.Id] = project;

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(_stored.GetValueOrDefault(id));

    public Task AddAsync(Project project, CancellationToken cancellationToken)
    {
        _stored[project.Id] = project;
        Added.Add(project);
        return Task.CompletedTask;
    }
}
