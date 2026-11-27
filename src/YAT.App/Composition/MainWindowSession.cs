using YAT.Application.Abstractions.Persistence;
using YAT.Application.Analyses;
using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Projects.RenameProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Features.Worksheets.DeleteWorksheetColumns;
using YAT.Application.Features.Worksheets.RenameWorksheet;
using YAT.Application.Filtering;
using YAT.Application.FtImport;
using YAT.Application.Graphs;
using YAT.Application.Ingestion;
using YAT.Application.Queries;
using YAT.app.Clipboard;
using YAT.Domain.Entities;

namespace YAT.app.Composition;

// The operations the main window currently needs, backed by Application services that CompositionRoot wires over
// the ProjectSession's repositories. Its public surface is Application commands, Domain metadata, metadata-only
// results and bounded display pages: no repositories, raw data store, parsed cells or typed raw columns.
// It does not own or dispose the ProjectSession.
//
// Operations run one at a time: paste and page loads work off the UI thread, and the session repositories are
// not thread-safe, so every operation waits for the previous one to finish.
public sealed class MainWindowSession
{
    // Rows per grid page; the query service never returns more.
    public const int GridPageSize = WorksheetDataQueryService.MaxPageRowCount;
    public const int MaxWindowColumnCount = WorksheetDataQueryService.MaxWindowColumnCount;

    private readonly CreateProjectHandler _createProject;
    private readonly RenameProjectHandler _renameProject;
    private readonly CreateWorksheetHandler _createWorksheet;
    private readonly RenameWorksheetHandler _renameWorksheet;
    private readonly AddWorksheetColumnHandler _addWorksheetColumn;
    private readonly IClipboardTextReader _clipboard;
    private readonly IClipboardTextWriter _clipboardWriter;
    private readonly WorksheetPastePlanner _planner;
    private readonly PasteExecutionService _pasteExecution;
    private readonly IWorksheetColumnRepository _worksheetColumns;
    private readonly WorksheetDataQueryService _dataQuery;
    private readonly DeleteWorksheetColumnsHandler _deleteWorksheetColumns;
    private readonly WorksheetColumnsTsvExporter _columnsExporter;
    private readonly ProjectMetadataQueryService _metadataQuery;
    private readonly GraphDataQueryService _graphData;
    private readonly FilterValuesQueryService _filterValues;
    private readonly AnalysisDataQueryService _analysisData;
    private readonly Guid? _projectId;
    private readonly IAtomicWorksheetCreator? _atomicWorksheetCreator;
    private readonly FtStreamingImportService _ftStreamingImport = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _retired;

    internal MainWindowSession(
        CreateProjectHandler createProject,
        RenameProjectHandler renameProject,
        CreateWorksheetHandler createWorksheet,
        RenameWorksheetHandler renameWorksheet,
        AddWorksheetColumnHandler addWorksheetColumn,
        IClipboardTextReader clipboard,
        IClipboardTextWriter clipboardWriter,
        WorksheetPastePlanner planner,
        PasteExecutionService pasteExecution,
        IWorksheetColumnRepository worksheetColumns,
        WorksheetDataQueryService dataQuery,
        DeleteWorksheetColumnsHandler deleteWorksheetColumns,
        WorksheetColumnsTsvExporter columnsExporter,
        ProjectMetadataQueryService metadataQuery,
        GraphDataQueryService graphData,
        FilterValuesQueryService filterValues,
        AnalysisDataQueryService analysisData,
        Guid? projectId,
        IAtomicWorksheetCreator? atomicWorksheetCreator = null)
    {
        _createProject = createProject;
        _renameProject = renameProject;
        _createWorksheet = createWorksheet;
        _renameWorksheet = renameWorksheet;
        _addWorksheetColumn = addWorksheetColumn;
        _clipboard = clipboard;
        _clipboardWriter = clipboardWriter;
        _planner = planner;
        _pasteExecution = pasteExecution;
        _worksheetColumns = worksheetColumns;
        _dataQuery = dataQuery;
        _deleteWorksheetColumns = deleteWorksheetColumns;
        _columnsExporter = columnsExporter;
        _metadataQuery = metadataQuery;
        _graphData = graphData;
        _filterValues = filterValues;
        _analysisData = analysisData;
        _projectId = projectId;
        _atomicWorksheetCreator = atomicWorksheetCreator;
    }

    // Raised on the calling context after a successful operation that changed the project: create or rename a project
    // or worksheet, add a column, paste (when something was pasted) and delete columns. Failed operations raise nothing.
    public event EventHandler? ProjectModified;

    // The stored metadata of the session's project (project, worksheets in order, their columns), read off the UI thread.
    // Null when the session has no stored project (the legacy in-memory composition).
    public async Task<ProjectMetadata?> LoadProjectAsync(CancellationToken cancellationToken)
    {
        if (_projectId is not { } projectId)
        {
            return null;
        }

        return await RunExclusiveAsync(
            () => Task.Run(() => _metadataQuery.LoadAsync(projectId, cancellationToken), cancellationToken),
            cancellationToken);
    }

    // The observations of a graph configuration (see GraphDataQueryService), read off the UI thread. The values stay in
    // the returned graph data: they never pass through a view model.
    public Task<GraphData> LoadGraphDataAsync(GraphConfiguration configuration, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(() => _graphData.LoadAsync(configuration, cancellationToken), cancellationToken),
            cancellationToken);

    // The values a filter condition of a graph or analysis can be chosen from (see FilterValuesQueryService), read off the
    // UI thread: the distinct values of one column, at most ValueSetCondition.MaximumDistinctValues of them - never its
    // rows.
    public Task<FilterValues> LoadFilterValuesAsync(Guid worksheetId, Guid columnId, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(() => _filterValues.LoadAsync(worksheetId, columnId, cancellationToken), cancellationToken),
            cancellationToken);

    // The worksheet rows of an analysis configuration (see AnalysisDataQueryService), read off the UI thread. The
    // values stay in the returned analysis data: they never pass through a view model.
    public Task<AnalysisData> LoadAnalysisDataAsync(AnalysisConfiguration configuration, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(() => _analysisData.LoadAsync(configuration, cancellationToken), cancellationToken),
            cancellationToken);

    // The column metadata of one worksheet (Id, Index, Name, DataType, ...), read off the UI thread. Never raw values.
    public Task<IReadOnlyList<WorksheetColumn>> LoadWorksheetColumnsAsync(Guid worksheetId, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(() => _metadataQuery.LoadWorksheetColumnsAsync(worksheetId, cancellationToken), cancellationToken),
            cancellationToken);

    public Task<Project> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken) =>
        RunModifyingAsync(() => _createProject.HandleAsync(command, cancellationToken), cancellationToken);

    // Renames go through the Application handlers, which validate the name and store a renamed copy; the returned entity
    // is the stored state the UI should show.
    public Task<Project> RenameProjectAsync(RenameProjectCommand command, CancellationToken cancellationToken) =>
        RunModifyingAsync(() => _renameProject.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<Worksheet> CreateWorksheetAsync(CreateWorksheetCommand command, CancellationToken cancellationToken) =>
        RunModifyingAsync(() => _createWorksheet.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<Worksheet> RenameWorksheetAsync(RenameWorksheetCommand command, CancellationToken cancellationToken) =>
        RunModifyingAsync(() => _renameWorksheet.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<WorksheetColumn> AddWorksheetColumnAsync(AddWorksheetColumnCommand command, CancellationToken cancellationToken) =>
        RunModifyingAsync(() => _addWorksheetColumn.HandleAsync(command, cancellationToken), cancellationToken);

    // Atomically creates a new worksheet with streamed rows inside a single storage transaction, off the UI thread.
    // Internal to YAT.App composition/import controllers so MainWindowSession's public UI surface never exposes
    // persistence interfaces (IRawDataRowWriter). Failed or cancelled imports roll back completely and never raise
    // ProjectModified. UI publication and worksheet activation belong to FT-03 / FT-04 after receiving the committed
    // CreatedWorksheetResult.
    internal Task<CreatedWorksheetResult> CreateWorksheetWithStreamingDataAsync(
        string worksheetName,
        IReadOnlyList<WorksheetColumnDefinition> columns,
        Action<IRawDataRowWriter> writeRows,
        CancellationToken cancellationToken = default) =>
        CreateWorksheetWithStreamingDataCoreAsync(
            worksheetName,
            columns,
            writeRows,
            onPostCommitNotificationError: null,
            cancellationToken);

    private async Task<CreatedWorksheetResult> CreateWorksheetWithStreamingDataCoreAsync(
        string worksheetName,
        IReadOnlyList<WorksheetColumnDefinition> columns,
        Action<IRawDataRowWriter> writeRows,
        Action<Exception>? onPostCommitNotificationError,
        CancellationToken cancellationToken)
    {
        if (_projectId is not { } projectId || _atomicWorksheetCreator is null)
        {
            throw new InvalidOperationException("Atomic streaming worksheet creation requires a persistent project session.");
        }

        var request = new AtomicWorksheetCreateRequest(projectId, worksheetName, columns);
        var committed = await RunExclusiveAsync(
            () => Task.Run(
                () => _atomicWorksheetCreator.CreateWorksheetAsync(request, writeRows, cancellationToken),
                cancellationToken),
            cancellationToken);

        try
        {
            ProjectModified?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (onPostCommitNotificationError is not null)
        {
            onPostCommitNotificationError(ex);
        }

        return committed;
    }

    // Atomically imports all selected FT datalog files from a prepared FT-03.2 plan into one new Worksheet,
    // off the UI thread, reusing CreateWorksheetWithStreamingDataCoreAsync for busy-state gating and commit-only
    // ProjectModified semantics. Internal to YAT.App import controllers so MainWindowSession's public UI surface
    // stays limited to UI commands and Domain metadata. UI publication and worksheet activation belong to FT-04.
    // Once the Worksheet transaction commits, post-commit notification failures never misreport it as uncommitted.
    internal async Task<FtStreamingImportResult> ImportFtDatalogAsync(
        string worksheetName,
        FtPreparedImportPlan preparedPlan,
        IProgress<FtImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Exception? notificationError = null;
        var result = await _ftStreamingImport.ImportAsync(
            (name, cols, writeRows, ct) => CreateWorksheetWithStreamingDataCoreAsync(
                name,
                cols,
                writeRows,
                ex => notificationError = ex,
                ct),
            worksheetName,
            preparedPlan,
            progress,
            cancellationToken);

        if (result.IsSuccess && notificationError is not null)
        {
            var warning = new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Warning,
                FtImportDiagnosticCode.ProgressObserverFailed,
                $"Post-commit ProjectModified handler threw {notificationError.GetType().Name}: {notificationError.Message}");
            var warnings = result.Warnings.Concat([warning]).ToArray();
            var all = result.AllDiagnostics.Concat([warning]).ToArray();
            return result with
            {
                Warnings = Array.AsReadOnly(warnings),
                AllDiagnostics = Array.AsReadOnly(all)
            };
        }

        return result;
    }

    // Pastes clipboard text into the worksheet column-wise, starting at the active column's index, or after the last
    // existing column when there is no active column. An empty clipboard is a no-op. Parsing, planning and storage
    // run off the UI thread.
    public async Task<ClipboardPasteResult> PasteFromClipboardAsync(
        Guid worksheetId,
        int? activeColumnIndex,
        CancellationToken cancellationToken)
    {
        // The clipboard is read on the calling (UI) thread.
        var text = await _clipboard.ReadTextAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return ClipboardPasteResult.NothingToPaste;
        }

        return await RunModifyingAsync(
            () => Task.Run(() => PasteTextAsync(worksheetId, activeColumnIndex, text, cancellationToken), cancellationToken),
            cancellationToken);
    }

    // Copies whole columns (header + values, in worksheet Index order) to the clipboard as tab-separated text that
    // spreadsheets paste as columns. The text is built off the UI thread from chunked raw reads; the clipboard is
    // written on the calling (UI) thread, and only once the whole text has been built.
    public async Task CopyColumnsToClipboardAsync(Guid worksheetId, IReadOnlyList<Guid> columnIds, CancellationToken cancellationToken)
    {
        var text = await RunExclusiveAsync(
            () => Task.Run(() => _columnsExporter.ExportAsync(worksheetId, columnIds, cancellationToken), cancellationToken),
            cancellationToken);

        await _clipboardWriter.WriteTextAsync(text, cancellationToken);
    }

    // Deletes the given columns as a whole, in one batch (one raw delete, metadata deletes, one reindex), off the UI
    // thread, and returns the worksheet's remaining column metadata ordered by Index.
    public Task<IReadOnlyList<WorksheetColumn>> DeleteColumnsAsync(
        Guid worksheetId,
        IReadOnlyList<Guid> columnIds,
        CancellationToken cancellationToken) =>
        RunModifyingAsync(
            () => Task.Run(
                () => _deleteWorksheetColumns.HandleAsync(new DeleteWorksheetColumnsCommand(worksheetId, columnIds), cancellationToken),
                cancellationToken),
            cancellationToken);

    // Loads one grid page (at most GridPageSize rows) starting at the zero-based rowOffset, off the UI thread.
    public Task<WorksheetGridPage> LoadWorksheetPageAsync(Guid worksheetId, long rowOffset, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(
                async () => WorksheetGridPage.FromDataPage(
                    await _dataQuery.GetPageAsync(worksheetId, rowOffset, GridPageSize, cancellationToken)),
                cancellationToken),
            cancellationToken);

    // Loads page metadata (columns in Index order and row counts) without reading raw cell values, off the UI thread.
    public Task<WorksheetPageMetadata> LoadWorksheetPageMetadataAsync(
        Guid worksheetId,
        long rowOffset,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(
                () => _dataQuery.GetPageMetadataAsync(worksheetId, rowOffset, GridPageSize, cancellationToken),
                cancellationToken),
            cancellationToken);

    // Loads a bounded column window (at most MaxWindowColumnCount columns) for one row page, off the UI thread,
    // returning typed raw columns without string formatting.
    public Task<WorksheetColumnWindow> LoadWorksheetColumnWindowAsync(
        Guid worksheetId,
        long rowOffset,
        IReadOnlyList<Guid> columnIds,
        CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(
                () => _dataQuery.GetColumnWindowAsync(worksheetId, rowOffset, GridPageSize, columnIds, cancellationToken),
                cancellationToken),
            cancellationToken);

    private async Task<ClipboardPasteResult> PasteTextAsync(
        Guid worksheetId,
        int? activeColumnIndex,
        string text,
        CancellationToken cancellationToken)
    {
        var existingColumns = await _worksheetColumns.GetByWorksheetIdAsync(worksheetId, cancellationToken);
        var startColumnIndex = activeColumnIndex ?? NextColumnIndex(existingColumns);
        var plan = _planner.PlanFromText(worksheetId, existingColumns, startColumnIndex, text, cancellationToken);

        await _pasteExecution.ExecuteFromTextAsync(plan, text, cancellationToken);

        // The paste is persisted at this point, so the reload is not cancellable: the UI always gets current metadata.
        var columns = await _worksheetColumns.GetByWorksheetIdAsync(worksheetId, CancellationToken.None);
        return new ClipboardPasteResult(isPasted: true, columns);
    }

    // Lifecycle coordination (ProjectLifecycleController). SuspendAsync waits until no operation runs and then holds the
    // session, so later operations wait. Resume lets them continue; Retire closes the session for good, after which
    // every waiting and later operation fails with ProjectSessionClosedException. Resume and Retire may only follow a
    // successful SuspendAsync.
    public async Task SuspendAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        if (_retired)
        {
            _gate.Release();
            throw new ProjectSessionClosedException();
        }
    }

    public void Resume() => _gate.Release();

    public void Retire()
    {
        _retired = true;
        _gate.Release();
    }

    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_retired)
            {
                throw new ProjectSessionClosedException();
            }

            return await operation();
        }
        finally
        {
            _gate.Release();
        }
    }

    // An operation that changes the project: ProjectModified is raised once it has completed successfully.
    private async Task<T> RunModifyingAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        var result = await RunExclusiveAsync(operation, cancellationToken);
        ProjectModified?.Invoke(this, EventArgs.Empty);
        return result;
    }

    // 0 for a worksheet without columns, otherwise one past the highest existing index.
    private static int NextColumnIndex(IReadOnlyList<WorksheetColumn> existingColumns)
    {
        if (existingColumns.Count == 0)
        {
            return 0;
        }

        var lastIndex = existingColumns.Max(column => column.Index);
        if (lastIndex == int.MaxValue)
        {
            throw new ValidationException("The worksheet has no free column position to paste into.");
        }

        return lastIndex + 1;
    }
}
