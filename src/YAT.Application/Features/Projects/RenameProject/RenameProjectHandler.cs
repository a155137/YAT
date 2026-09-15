using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Features.Projects.RenameProject;

public sealed class RenameProjectHandler
{
    private readonly IProjectRepository _projects;
    private readonly TimeProvider _timeProvider;

    public RenameProjectHandler(IProjectRepository projects, TimeProvider timeProvider)
    {
        _projects = projects;
        _timeProvider = timeProvider;
    }

    // Stores and returns a renamed copy of the project; the stored instance is replaced through the repository,
    // never modified in place.
    public async Task<Project> HandleAsync(RenameProjectCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ValidationException("Project name must not be empty.");
        }

        var project = await _projects.GetByIdAsync(command.ProjectId, cancellationToken)
            ?? throw new EntityNotFoundException(nameof(Project), command.ProjectId);

        var renamed = new Project
        {
            Id = project.Id,
            Name = command.Name.Trim(),
            Description = project.Description,
            CreatedAt = project.CreatedAt,
            UpdatedAt = _timeProvider.GetUtcNow()
        };

        await _projects.UpdateAsync(renamed, cancellationToken);

        return renamed;
    }
}
