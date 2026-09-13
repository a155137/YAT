using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
using YAT.Application.Ingestion;
using YAT.app.Clipboard;
using YAT.Domain.Entities;

namespace YAT.app.Composition;

// The operations the main window currently needs, backed by Application services that CompositionRoot wires over
// the ProjectSession's repositories. Its public surface is Application commands, Domain metadata and metadata-only
// results: no repositories, raw data store, parsed cells or raw values. It does not own or dispose the ProjectSession.
public sealed class MainWindowSession
{
    private readonly CreateProjectHandler _createProject;
    private readonly CreateWorksheetHandler _createWorksheet;
    private readonly AddWorksheetColumnHandler _addWorksheetColumn;
    private readonly IClipboardTextReader _clipboard;
    private readonly TabularTextParser _parser;
    private readonly WorksheetPastePlanner _planner;
    private readonly PasteExecutionService _pasteExecution;
    private readonly IWorksheetColumnRepository _worksheetColumns;

    internal MainWindowSession(
        CreateProjectHandler createProject,
        CreateWorksheetHandler createWorksheet,
        AddWorksheetColumnHandler addWorksheetColumn,
        IClipboardTextReader clipboard,
        TabularTextParser parser,
        WorksheetPastePlanner planner,
        PasteExecutionService pasteExecution,
        IWorksheetColumnRepository worksheetColumns)
    {
        _createProject = createProject;
        _createWorksheet = createWorksheet;
        _addWorksheetColumn = addWorksheetColumn;
        _clipboard = clipboard;
        _parser = parser;
        _planner = planner;
        _pasteExecution = pasteExecution;
        _worksheetColumns = worksheetColumns;
    }

    public Task<Project> CreateProjectAsync(CreateProjectCommand command, CancellationToken cancellationToken) =>
        _createProject.HandleAsync(command, cancellationToken);

    public Task<Worksheet> CreateWorksheetAsync(CreateWorksheetCommand command, CancellationToken cancellationToken) =>
        _createWorksheet.HandleAsync(command, cancellationToken);

    public Task<WorksheetColumn> AddWorksheetColumnAsync(AddWorksheetColumnCommand command, CancellationToken cancellationToken) =>
        _addWorksheetColumn.HandleAsync(command, cancellationToken);

    // Pastes clipboard text into the worksheet column-wise, starting at selectedColumnIndex, or after the last existing
    // column when no column is selected. An empty clipboard is a no-op. The caller must not run other worksheet
    // operations concurrently: parsing, planning and storage run off the UI thread against the session repositories.
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

        return await Task.Run(() => PasteTextAsync(worksheetId, selectedColumnIndex, text, cancellationToken), cancellationToken);
    }

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
