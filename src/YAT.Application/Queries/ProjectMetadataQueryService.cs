using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.Queries;

// Reads a project's stored metadata through the repositories (e.g. to show a project that was created or opened).
public sealed class ProjectMetadataQueryService
{
    private readonly IProjectRepository _projects;
    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _worksheetColumns;

    public ProjectMetadataQueryService(
        IProjectRepository projects,
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository worksheetColumns)
    {
        _projects = projects;
        _worksheets = worksheets;
        _worksheetColumns = worksheetColumns;
    }

    public async Task<ProjectMetadata> LoadAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _projects.GetByIdAsync(projectId, cancellationToken)
            ?? throw new EntityNotFoundException(nameof(Project), projectId);

        var worksheets = new List<WorksheetMetadata>();
        foreach (var worksheet in await _worksheets.GetByProjectIdAsync(projectId, cancellationToken))
        {
            var columns = await _worksheetColumns.GetByWorksheetIdAsync(worksheet.Id, cancellationToken);
            worksheets.Add(new WorksheetMetadata(worksheet, columns));
        }

        return new ProjectMetadata(project, worksheets);
    }
}
