using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Features.Worksheets.CreateWorksheet;

public sealed class CreateWorksheetHandler
{
    private readonly IProjectRepository _projects;
    private readonly IWorksheetRepository _worksheets;
    private readonly TimeProvider _timeProvider;

    public CreateWorksheetHandler(
        IProjectRepository projects,
        IWorksheetRepository worksheets,
        TimeProvider timeProvider)
    {
        _projects = projects;
        _worksheets = worksheets;
        _timeProvider = timeProvider;
    }

    public async Task<Worksheet> HandleAsync(CreateWorksheetCommand command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
        {
            throw new ValidationException("Worksheet name must not be empty.");
        }

        var project = await _projects.GetByIdAsync(command.ProjectId, cancellationToken);
        if (project is null)
        {
            throw new EntityNotFoundException(nameof(Project), command.ProjectId);
        }

        var now = _timeProvider.GetUtcNow();

        var worksheet = new Worksheet
        {
            Id = Guid.NewGuid(),
            ProjectId = command.ProjectId,
            Name = command.Name.Trim(),
            RowCount = 0,
            ColumnCount = 0,
            CreatedAt = now,
            UpdatedAt = now
        };

        await _worksheets.AddAsync(worksheet, cancellationToken);

        return worksheet;
    }
}
