using YAT.Application.Analyses;
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
using YAT.Application.Updates;
using YAT.app.Analyses;
using YAT.app.Clipboard;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.Graphs.Export;
using YAT.app.Lifecycle;
using YAT.app.Updates;
using YAT.app.ViewModels;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;
using YAT.Infrastructure.Settings;
using YAT.Infrastructure.Updates;

namespace YAT.app.Composition;

// Manual wiring only: constructs, wires and returns. Metadata repositories are created solely as part of a
// ProjectSession; the UI reaches them through a MainWindowSession built over that session.
public sealed class CompositionRoot
{
    private readonly TimeProvider _timeProvider;
    private readonly DuckDbProjectStorage _projectStorage;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GraphPaletteLibraryService, GraphPaletteLibraryAccess> _paletteAccess = new();

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
            new GraphFilterValuesQueryService(projectSession.WorksheetColumns, projectSession.RawDataStore),
            new AnalysisDataQueryService(projectSession.Worksheets, projectSession.WorksheetColumns, projectSession.RawDataStore),
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

    // The window-independent halves of a graph export: drawing the graph off screen, and writing a presentation. The
    // dialogs belong to the graph window that shows them, so the UI adds those (see IGraphExportWorkflowFactory).
    public GraphExportService CreateGraphExportService() => new();

    public IPowerPointGraphExporter CreatePowerPointExporter() => new PowerPointGraphExporter();

    // palettes: the user's graph palettes, whose default a new graph setup starts with (Task #050). Without them every
    // setup starts with YAT Default.
    public GraphSetupController CreateGraphSetup(
        IGraphSetupDialogs dialogs,
        IGraphWindowPresenter windows,
        GraphPaletteLibraryService? palettes = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(windows);
        return new GraphSetupController(
            dialogs,
            windows,
            new ScatterRenderModelBuilder(),
            new HistogramRenderModelBuilder(),
            new ProbabilityPlotRenderModelBuilder(),
            new EmpiricalCdfRenderModelBuilder(),
            new BoxPlotRenderModelBuilder(),
            palettes: palettes is null ? null : GraphPaletteAccess(palettes));
    }

    // The windows' one way to the user's palettes (Task #050): their choices and the Palette Manager. One for each library,
    // so the graph setups and the graph windows share it.
    public GraphPaletteLibraryAccess GraphPaletteAccess(GraphPaletteLibraryService palettes)
    {
        ArgumentNullException.ThrowIfNull(palettes);
        return _paletteAccess.GetValue(palettes, library => new GraphPaletteLibraryAccess(library));
    }

    // The user's graph palettes (Task #050), loaded now from filePath - by default graph-palettes.json in YAT's folder of
    // the user's roaming application data. Loading never fails and never writes: what cannot be read is left out, and
    // the worst case is YAT Default alone. Only the application passes no path; tests give a folder of their own.
    public GraphPaletteLibraryService CreateGraphPaletteLibrary(string? filePath = null) =>
        new(
            new JsonGraphPaletteLibraryStore(filePath ?? JsonGraphPaletteLibraryStore.DefaultFilePath, _timeProvider),
            new GraphPalette([.. GraphThemes.Light.SeriesPalette.Select(GraphAppearance.FromSkia)]));

    // The Statistics menu's analyses. The setup dialogs and the result window belong to the UI, which supplies them;
    // the statistics themselves are the builder's, which is why it is built here and not in a window.
    public DescriptiveStatisticsController CreateDescriptiveStatistics(
        IAnalysisSetupDialogs dialogs,
        IAnalysisResultPresenter results)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(results);
        return new DescriptiveStatisticsController(dialogs, results, new DescriptiveStatisticsBuilder());
    }

    public CapabilityAnalysisController CreateCapabilityAnalysis(
        ICapabilityAnalysisSetupDialogs dialogs,
        IAnalysisResultPresenter results)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(results);
        return new CapabilityAnalysisController(dialogs, results, new CapabilityAnalysisBuilder());
    }

    public MainWindowShellViewModel CreateMainWindowShellViewModel(
        ProjectLifecycleController lifecycle,
        GraphSetupController graphs,
        DescriptiveStatisticsController statistics,
        CapabilityAnalysisController capability,
        UpdateCheckController? updates = null) =>
        new(lifecycle, graphs, statistics, capability, updates);

    // The update connection for the application's lifetime (Task #051.B): one HttpClient, the update information at
    // manifestUrl (by default the address Directory.Build.props gives the build) and packages kept under updatesRoot (by
    // default %LOCALAPPDATA%\YAT\updates). The application disposes it when it exits. Only the application passes no
    // arguments; tests give a handler and a folder of their own.
    public UpdateConnection CreateUpdateConnection(Uri? manifestUrl = null, string? updatesRoot = null, HttpMessageHandler? handler = null) =>
        new(
            manifestUrl ?? UpdateEndpoint.ManifestUrl ?? throw new InvalidOperationException("This build has no update address (YatUpdateManifestUrl)."),
            ApplicationInfo.Current.Version,
            updatesRoot,
            handler);

    // Help > Check for Updates... over an update connection - or, in tests, over any manifest source and downloader. The
    // application's check installs for this very process (Task #051.C), from the packages the connection keeps.
    public UpdateCheckController CreateUpdateCheck(IUpdateDialogs dialogs, UpdateConnection connection) =>
        CreateUpdateCheck(dialogs, connection.Source, connection.Downloader, installer: CreateUpdateInstaller(connection.Store.Root));

    public UpdateCheckController CreateUpdateCheck(
        IUpdateDialogs dialogs,
        IUpdateManifestSource source,
        IUpdatePackageDownloader downloader,
        UpdateEnvironment? environment = null,
        IUpdateInstaller? installer = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        return new UpdateCheckController(
            dialogs,
            new UpdateCheckService(source, downloader, environment ?? UpdateEnvironment.Current(ApplicationInfo.Current.Version), installer));
    }

    // Installs verified packages kept under updatesRoot for a YAT (Task #051.C): by default this process, in the folder
    // it runs from.
    public UpdateInstaller CreateUpdateInstaller(string updatesRoot, UpdateInstallTarget? target = null) =>
        new(target ?? UpdateInstallTarget.Current(ApplicationInfo.Current.Version), updatesRoot);

    // "YAT has been updated to vX.Y.Z." - the version the updater just installed in this YAT's folder, once (Task #051.C).
    public string? TakeInstalledUpdateNotice(string? installation = null) =>
        UpdateInstallNotice.Take(installation ?? AppContext.BaseDirectory, ApplicationInfo.Current.Version);

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
