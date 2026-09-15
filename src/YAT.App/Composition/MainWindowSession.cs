using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Projects.RenameProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Features.Worksheets.DeleteWorksheetColumns;
using YAT.Application.Features.Worksheets.RenameWorksheet;
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

    private readonly CreateProjectHandler _createProject;
    private readonly RenameProjectHandler _renameProject;
    private readonly CreateWorksheetHandler _createWorksheet;
    private readonly RenameWorksheetHandler _renameWorksheet;
    private readonly AddWorksheetColumnHandler _addWorksheetColumn;
    private readonly IClipboardTextReader _clipboard;
    private readonly IClipboardTextWriter _clipboardWriter;
    private readonly TabularTextParser _parser;
    private readonly WorksheetPastePlanner _planner;
    private readonly PasteExecutionService _pasteExecution;
    private readonly IWorksheetColumnRepository _worksheetColumns;
    private readonly WorksheetDataQueryService _dataQuery;
    private readonly DeleteWorksheetColumnsHandler _deleteWorksheetColumns;
    private readonly WorksheetColumnsTsvExporter _columnsExporter;
    private readonly ProjectMetadataQueryService _metadataQuery;
    private readonly Guid? _projectId;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal MainWindowSession(
        CreateProjectHandler createProject,
        RenameProjectHandler renameProject,
        CreateWorksheetHandler createWorksheet,
        RenameWorksheetHandler renameWorksheet,
        AddWorksheetColumnHandler addWorksheetColumn,
        IClipboardTextReader clipboard,
        IClipboardTextWriter clipboardWriter,
        TabularTextParser parser,
        WorksheetPastePlanner planner,
        PasteExecutionService pasteExecution,
        IWorksheetColumnRepository worksheetColumns,
        WorksheetDataQueryService dataQuery,
        DeleteWorksheetColumnsHandler deleteWorksheetColumns,
        WorksheetColumnsTsvExporter columnsExporter,
        ProjectMetadataQueryService metadataQuery,
        Guid? projectId)
    {
        _createProject = createProject;
        _renameProject = renameProject;
        _createWorksheet = createWorksheet;
        _renameWorksheet = renameWorksheet;
        _addWorksheetColumn = addWorksheetColumn;
        _clipboard = clipboard;
        _clipboardWriter = clipboardWriter;
        _parser = parser;
        _planner = planner;
        _pasteExecution = pasteExecution;
        _worksheetColumns = worksheetColumns;
        _dataQuery = dataQuery;
        _deleteWorksheetColumns = deleteWorksheetColumns;
        _columnsExporter = columnsExporter;
        _metadataQuery = metadataQuery;
        _projectId = projectId;
    }

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

    public Task<Project> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _createProject.HandleAsync(command, cancellationToken), cancellationToken);

    // Renames go through the Application handlers, which validate the name and store a renamed copy; the returned entity
    // is the stored state the UI should show.
    public Task<Project> RenameProjectAsync(RenameProjectCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _renameProject.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<Worksheet> CreateWorksheetAsync(CreateWorksheetCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _createWorksheet.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<Worksheet> RenameWorksheetAsync(RenameWorksheetCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _renameWorksheet.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<WorksheetColumn> AddWorksheetColumnAsync(AddWorksheetColumnCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _addWorksheetColumn.HandleAsync(command, cancellationToken), cancellationToken);

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

        return await RunExclusiveAsync(
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
        RunExclusiveAsync(
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

    private async Task<ClipboardPasteResult> PasteTextAsync(
        Guid worksheetId,
        int? activeColumnIndex,
        string text,
        CancellationToken cancellationToken)
    {
        var data = _parser.Parse(text);
        var existingColumns = await _worksheetColumns.GetByWorksheetIdAsync(worksheetId, cancellationToken);
        var startColumnIndex = activeColumnIndex ?? NextColumnIndex(existingColumns);
        var plan = _planner.Plan(worksheetId, existingColumns, startColumnIndex, data);

        await _pasteExecution.ExecuteAsync(plan, data, cancellationToken);

        // The paste is persisted at this point, so the reload is not cancellable: the UI always gets current metadata.
        var columns = await _worksheetColumns.GetByWorksheetIdAsync(worksheetId, CancellationToken.None);
        return new ClipboardPasteResult(isPasted: true, columns);
    }

    private async Task<T> RunExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await operation();
        }
        finally
        {
            _gate.Release();
        }
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
