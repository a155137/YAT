using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Ingestion;
using YAT.app.ViewModels;
using YAT.Infrastructure.Persistence.DuckDb;
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

    public MainWindowViewModel CreateMainWindowViewModel() => new(CreateProject, CreateWorksheet, AddWorksheetColumn);

    // Constructs and wires one project scope whose raw data lives in the DuckDB database at databasePath.
    // Every call creates new metadata repositories and a new raw store; nothing is shared between sessions.
    // The caller owns the returned session and must dispose it to release the database.
    public ProjectSession CreateProjectSession(string databasePath)
    {
        var settings = new DuckDbRawDataStoreSettings(databasePath);
        var worksheets = new InMemoryWorksheetRepository();
        var worksheetColumns = new InMemoryWorksheetColumnRepository();
        var rawDataStore = new DuckDbWorksheetRawDataStore(settings);
        var pasteExecution = new PasteExecutionService(worksheets, worksheetColumns, rawDataStore);

        return new ProjectSession(worksheets, worksheetColumns, rawDataStore, pasteExecution);
    }
}
