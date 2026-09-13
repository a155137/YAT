using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.app.ViewModels;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.app.Composition;

public sealed class CompositionRoot
{
    public CompositionRoot(TimeProvider timeProvider)
    {
        var projects = new InMemoryProjectRepository();
        var worksheets = new InMemoryWorksheetRepository();
        var worksheetColumns = new InMemoryWorksheetColumnRepository();

        CreateProject = new CreateProjectHandler(projects, timeProvider);
        CreateWorksheet = new CreateWorksheetHandler(projects, worksheets, timeProvider);
        AddWorksheetColumn = new AddWorksheetColumnHandler(worksheets, worksheetColumns);
    }

    public CreateProjectHandler CreateProject { get; }

    public CreateWorksheetHandler CreateWorksheet { get; }

    public AddWorksheetColumnHandler AddWorksheetColumn { get; }

    public MainWindowViewModel CreateMainWindowViewModel() => new(CreateProject, CreateWorksheet);
}
