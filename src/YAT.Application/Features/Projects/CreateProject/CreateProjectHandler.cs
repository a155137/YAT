using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Features.Projects.CreateProject;

public sealed class CreateProjectHandler
{
    private readonly IProjectRepository _projects;
    private readonly TimeProvider _timeProvider;

    public CreateProjectHandler(IProjectRepository projects, TimeProvider timeProvider)
    {
        _projects = projects;
        _timeProvider = timeProvider;
    }

    public async Task<Project> HandleAsync(CreateProjectCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ValidationException("Project name must not be empty.");
        }

        var now = _timeProvider.GetUtcNow();

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = command.Name.Trim(),
            Description = command.Description,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _projects.AddAsync(project, cancellationToken);

        return project;
    }
}
