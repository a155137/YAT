using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Features.Worksheets.DeleteWorksheetColumn;
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
    private readonly CreateWorksheetHandler _createWorksheet;
    private readonly AddWorksheetColumnHandler _addWorksheetColumn;
    private readonly IClipboardTextReader _clipboard;
    private readonly TabularTextParser _parser;
    private readonly WorksheetPastePlanner _planner;
    private readonly PasteExecutionService _pasteExecution;
    private readonly IWorksheetColumnRepository _worksheetColumns;
    private readonly WorksheetDataQueryService _dataQuery;
    private readonly DeleteWorksheetColumnHandler _deleteWorksheetColumn;
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal MainWindowSession(
        CreateProjectHandler createProject,
        CreateWorksheetHandler createWorksheet,
        AddWorksheetColumnHandler addWorksheetColumn,
        IClipboardTextReader clipboard,
        TabularTextParser parser,
        WorksheetPastePlanner planner,
        PasteExecutionService pasteExecution,
        IWorksheetColumnRepository worksheetColumns,
        WorksheetDataQueryService dataQuery,
        DeleteWorksheetColumnHandler deleteWorksheetColumn)
    {
        _createProject = createProject;
        _createWorksheet = createWorksheet;
        _addWorksheetColumn = addWorksheetColumn;
        _clipboard = clipboard;
        _parser = parser;
        _planner = planner;
        _pasteExecution = pasteExecution;
        _worksheetColumns = worksheetColumns;
        _dataQuery = dataQuery;
        _deleteWorksheetColumn = deleteWorksheetColumn;
    }

    public Task<Project> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _createProject.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<Worksheet> CreateWorksheetAsync(CreateWorksheetCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _createWorksheet.HandleAsync(command, cancellationToken), cancellationToken);

    public Task<WorksheetColumn> AddWorksheetColumnAsync(AddWorksheetColumnCommand command, CancellationToken cancellationToken) =>
        RunExclusiveAsync(() => _addWorksheetColumn.HandleAsync(command, cancellationToken), cancellationToken);

    // Pastes clipboard text into the worksheet column-wise, starting at selectedColumnIndex, or after the last existing
    // column when no column is selected. An empty clipboard is a no-op. Parsing, planning and storage run off the UI thread.
    public async Task<ClipboardPasteResult> PasteFromClipboardAsync(
        Guid worksheetId,
        int? selectedColumnIndex,
        CancellationToken cancellationToken)
    {
        // The clipboard is read on the calling (UI) thread.
        var text = await _clipboard.ReadTextAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(text))
        {
            return ClipboardPasteResult.NothingToPaste;
        }

        return await RunExclusiveAsync(
            () => Task.Run(() => PasteTextAsync(worksheetId, selectedColumnIndex, text, cancellationToken), cancellationToken),
            cancellationToken);
    }

    // Deletes one column as a whole (raw values, metadata, contiguous reindex) off the UI thread and returns the
    // worksheet's remaining column metadata ordered by Index.
    public Task<IReadOnlyList<WorksheetColumn>> DeleteColumnAsync(Guid worksheetId, Guid columnId, CancellationToken cancellationToken) =>
        RunExclusiveAsync(
            () => Task.Run(
                () => _deleteWorksheetColumn.HandleAsync(new DeleteWorksheetColumnCommand(worksheetId, columnId), cancellationToken),
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
        int? selectedColumnIndex,
        string text,
        CancellationToken cancellationToken)
    {
        var data = _parser.Parse(text);
        var existingColumns = await _worksheetColumns.GetByWorksheetIdAsync(worksheetId, cancellationToken);
        var startColumnIndex = selectedColumnIndex ?? NextColumnIndex(existingColumns);
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
