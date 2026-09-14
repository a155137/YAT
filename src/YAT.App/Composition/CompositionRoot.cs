using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Features.Worksheets.DeleteWorksheetColumns;
using YAT.Application.Ingestion;
using YAT.Application.Queries;
using YAT.app.Clipboard;
using YAT.app.ViewModels;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.app.Composition;

// Manual wiring only: constructs, wires and returns. Worksheet and column repositories are created solely
// as part of a ProjectSession; the UI reaches them through a MainWindowSession built over that session.
public sealed class CompositionRoot
{
    private readonly TimeProvider _timeProvider;

    // Projects are not project-scoped: one repository for the application, shared by every MainWindowSession.
    private readonly InMemoryProjectRepository _projects = new();

    public CompositionRoot(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

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

    // The UI operations run against the given session's own repositories and paste execution; the caller keeps
    // owning the session. The clipboard reader and writer are supplied by the UI because they are tied to a window.
    public MainWindowSession CreateMainWindowSession(
        ProjectSession projectSession,
        IClipboardTextReader clipboardReader,
        IClipboardTextWriter clipboardWriter)
    {
        ArgumentNullException.ThrowIfNull(projectSession);
        ArgumentNullException.ThrowIfNull(clipboardReader);
        ArgumentNullException.ThrowIfNull(clipboardWriter);

        return new MainWindowSession(
            new CreateProjectHandler(_projects, _timeProvider),
            new CreateWorksheetHandler(_projects, projectSession.Worksheets, _timeProvider),
            new AddWorksheetColumnHandler(projectSession.Worksheets, projectSession.WorksheetColumns),
            clipboardReader,
            clipboardWriter,
            new TabularTextParser(),
            new WorksheetPastePlanner(new ColumnDataTypeDetector()),
            projectSession.PasteExecution,
            projectSession.WorksheetColumns,
            new WorksheetDataQueryService(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore),
            new DeleteWorksheetColumnsHandler(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore),
            new WorksheetColumnsTsvExporter(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore));
    }

    public MainWindowViewModel CreateMainWindowViewModel(MainWindowSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MainWindowViewModel(session);
    }
}
