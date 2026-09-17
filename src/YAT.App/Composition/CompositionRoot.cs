using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Projects.RenameProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Features.Worksheets.DeleteWorksheetColumns;
using YAT.Application.Features.Worksheets.RenameWorksheet;
using YAT.Application.Graphs;
using YAT.Application.Ingestion;
using YAT.Application.Queries;
using YAT.app.Clipboard;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.app.Composition;

// Manual wiring only: constructs, wires and returns. Metadata repositories are created solely as part of a
// ProjectSession; the UI reaches them through a MainWindowSession built over that session.
public sealed class CompositionRoot
{
    private readonly TimeProvider _timeProvider;
    private readonly DuckDbProjectStorage _projectStorage;

    // temporaryProjectsDirectory: where temporary projects and project working folders are kept
    // (default %TEMP%\YAT\projects).
    public CompositionRoot(TimeProvider timeProvider, string? temporaryProjectsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
        _projectStorage = new DuckDbProjectStorage(temporaryProjectsDirectory ?? DuckDbProjectStorage.DefaultTemporaryRoot);
    }

    internal TimeProvider TimeProvider => _timeProvider;

    // The production project lifecycle: one current persistent project session at a time.
    public ProjectWorkspace CreateProjectWorkspace() => new(this);

    // Legacy/test composition: metadata repositories in memory (lost on dispose) and raw data in the DuckDB database
    // at databasePath (":memory:" for a private in-memory database). Each call creates new repositories and a new raw
    // store; nothing is shared between sessions. Not the production project model: use CreateProjectWorkspace.
    public ProjectSession CreateProjectSession(string databasePath)
    {
        var settings = new DuckDbRawDataStoreSettings(databasePath);
        var projects = new InMemoryProjectRepository();
        var worksheets = new InMemoryWorksheetRepository();
        var worksheetColumns = new InMemoryWorksheetColumnRepository();
        var rawDataStore = new DuckDbWorksheetRawDataStore(settings);
        var pasteExecution = new PasteExecutionService(worksheets, worksheetColumns, rawDataStore);

        return new ProjectSession(projects, worksheets, worksheetColumns, rawDataStore, pasteExecution, database: null);
    }

    // A persistent session over a new temporary project database (no project stored yet).
    internal ProjectSession CreateTemporaryProjectSession() => CreatePersistentSession(_projectStorage.CreateTemporary());

    // A persistent session over a new project file at filePath (no project stored yet).
    internal ProjectSession CreateProjectFileSession(string filePath) => CreatePersistentSession(_projectStorage.Create(filePath));

    // A persistent session over an existing project file, with ProjectId set to the file's project.
    internal async Task<ProjectSession> OpenProjectFileSessionAsync(string filePath, CancellationToken cancellationToken)
    {
        var database = _projectStorage.Open(filePath);
        try
        {
            var projects = new DuckDbProjectRepository(database);
            var project = await projects.GetFileProjectAsync(cancellationToken)
                ?? throw new ProjectStorageException(ProjectStorageError.NotAYatProject, "The project file does not contain a project.");

            var session = CreatePersistentSession(database, projects);
            session.ProjectId = project.Id;
            return session;
        }
        catch
        {
            database.Dispose();
            throw;
        }
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
            new CreateProjectHandler(projectSession.Projects, _timeProvider),
            new RenameProjectHandler(projectSession.Projects, _timeProvider),
            new CreateWorksheetHandler(projectSession.Projects, projectSession.Worksheets, _timeProvider),
            new RenameWorksheetHandler(projectSession.Worksheets, _timeProvider),
            new AddWorksheetColumnHandler(projectSession.Worksheets, projectSession.WorksheetColumns),
            clipboardReader,
            clipboardWriter,
            new TabularTextParser(),
            new WorksheetPastePlanner(new ColumnDataTypeDetector()),
            projectSession.PasteExecution,
            projectSession.WorksheetColumns,
            new WorksheetDataQueryService(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore),
            new DeleteWorksheetColumnsHandler(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore),
            new WorksheetColumnsTsvExporter(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore),
            new ProjectMetadataQueryService(projectSession.Projects, projectSession.Worksheets, projectSession.WorksheetColumns),
            new GraphDataQueryService(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore),
            projectSession.ProjectId);
    }

    public MainWindowViewModel CreateMainWindowViewModel(MainWindowSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new MainWindowViewModel(session);
    }

    // The desktop project lifecycle over the workspace. The clipboard and dialogs are supplied by the UI (tied to a window).
    public ProjectLifecycleController CreateProjectLifecycle(
        ProjectWorkspace workspace,
        IClipboardTextReader clipboardReader,
        IClipboardTextWriter clipboardWriter,
        IProjectLifecycleDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(clipboardReader);
        ArgumentNullException.ThrowIfNull(clipboardWriter);
        ArgumentNullException.ThrowIfNull(dialogs);
        return new ProjectLifecycleController(this, workspace, clipboardReader, clipboardWriter, dialogs);
    }

    public GraphSetupController CreateGraphSetup(IGraphSetupDialogs dialogs, IGraphWindowPresenter windows)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(windows);
        return new GraphSetupController(dialogs, windows, new ScatterRenderModelBuilder());
    }

    public MainWindowShellViewModel CreateMainWindowShellViewModel(ProjectLifecycleController lifecycle, GraphSetupController graphs) =>
        new(lifecycle, graphs);

    private ProjectSession CreatePersistentSession(DuckDbProjectDatabase database, DuckDbProjectRepository? projects = null)
    {
        try
        {
            projects ??= new DuckDbProjectRepository(database);
            var worksheets = new DuckDbWorksheetRepository(database);
            var worksheetColumns = new DuckDbWorksheetColumnRepository(database, _timeProvider);
            var rawDataStore = new DuckDbWorksheetRawDataStore(database);
            var pasteExecution = new PasteExecutionService(worksheets, worksheetColumns, rawDataStore);

            return new ProjectSession(projects, worksheets, worksheetColumns, rawDataStore, pasteExecution, database);
        }
        catch
        {
            database.Dispose();
            throw;
        }
    }
}
