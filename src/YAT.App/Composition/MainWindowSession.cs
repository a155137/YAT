using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Domain.Entities;

namespace YAT.app.Composition;

// The operations the main window currently needs, backed by Application handlers that CompositionRoot wires over
// the ProjectSession's repositories. It exposes only Application commands and Domain metadata: no repositories,
// raw data store, paste execution or raw values. It does not own the ProjectSession and does not dispose it.
public sealed class MainWindowSession
{
    private readonly CreateProjectHandler _createProject;
    private readonly CreateWorksheetHandler _createWorksheet;
    private readonly AddWorksheetColumnHandler _addWorksheetColumn;

    internal MainWindowSession(
        CreateProjectHandler createProject,
        CreateWorksheetHandler createWorksheet,
        AddWorksheetColumnHandler addWorksheetColumn)
    {
        _createProject = createProject;
        _createWorksheet = createWorksheet;
        _addWorksheetColumn = addWorksheetColumn;
    }

    public Task<Project> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken) =>
        _createProject.HandleAsync(command, cancellationToken);

    public Task<Worksheet> CreateWorksheetAsync(CreateWorksheetCommand command, CancellationToken cancellationToken) =>
        _createWorksheet.HandleAsync(command, cancellationToken);

    public Task<WorksheetColumn> AddWorksheetColumnAsync(AddWorksheetColumnCommand command, CancellationToken cancellationToken) =>
        _addWorksheetColumn.HandleAsync(command, cancellationToken);
}
