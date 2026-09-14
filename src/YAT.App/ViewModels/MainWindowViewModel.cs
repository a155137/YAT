using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Exceptions;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.app.Composition;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using CreateProjectRequest = YAT.Application.Features.Projects.CreateProject.CreateProjectCommand;
using CreateWorksheetRequest = YAT.Application.Features.Worksheets.CreateWorksheet.CreateWorksheetCommand;

namespace YAT.app.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly IReadOnlyList<WorksheetDataType> DataTypes = Enum.GetValues<WorksheetDataType>();

    private static readonly IReadOnlyList<SemanticTypeOption> SemanticTypes =
    [
        new(null, "None"),
        .. Enum.GetValues<ColumnSemanticType>().Select(type => new SemanticTypeOption(type, type.ToString())),
    ];

    public const string DefaultProjectName = "Untitled Project";

    public const string DefaultWorksheetName = "Sheet1";

    private readonly MainWindowSession _session;

    // Column metadata added in this UI session, keyed by Worksheet Id. Never row data.
    // UI state only: columns are persisted through MainWindowSession, but selection does not re-read them yet.
    private readonly Dictionary<Guid, ObservableCollection<WorksheetColumn>> _columnsByWorksheet = [];

    // Identifies the latest grid load; results of superseded loads (e.g. after switching worksheets) are dropped.
    private int _gridLoadVersion;

    public MainWindowViewModel(MainWindowSession session)
    {
        _session = session;
    }

    // Startup workspace, like a new Excel workbook: an untitled Project with Sheet1 selected, ready for Ctrl+V.
    // Runs the same commands a user would, so the resulting state is identical to creating both by hand.
    // Does nothing once a Project exists.
    public async Task CreateDefaultWorkspaceAsync()
    {
        if (CurrentProject is not null)
        {
            return;
        }

        ProjectName = DefaultProjectName;
        ProjectDescription = string.Empty;
        await CreateProjectCommand.ExecuteAsync(null);

        if (CurrentProject is null)
        {
            return;
        }

        WorksheetName = DefaultWorksheetName;
        await CreateWorksheetCommand.ExecuteAsync(null);
    }

    [ObservableProperty]
    public partial string ProjectName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProjectDescription { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCurrentProject))]
    [NotifyCanExecuteChangedFor(nameof(CreateWorksheetCommand))]
    public partial Project? CurrentProject { get; private set; }

    public bool HasCurrentProject => CurrentProject is not null;

    [ObservableProperty]
    public partial string WorksheetName { get; set; } = string.Empty;

    // Worksheet metadata created in this session for the current Project only; never row data.
    public ObservableCollection<Worksheet> Worksheets { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedWorksheetSummary))]
    [NotifyCanExecuteChangedFor(nameof(AddColumnCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedColumnsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial Worksheet? SelectedWorksheet { get; set; }

    // Column selection is UI state only. ActiveColumn is the Ctrl+V paste start and the Shift+Click anchor; when it is
    // null, paste appends after the last column. SelectedColumns (ordered by Index) is the Delete target set; it
    // contains ActiveColumn whenever ActiveColumn is set, and is empty when it is not.
    [ObservableProperty]
    public partial WorksheetColumn? ActiveColumn { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteSelectedColumnsMenuText))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedColumnsCommand))]
    public partial IReadOnlyList<WorksheetColumn> SelectedColumns { get; private set; } = [];

    // Header context menu text: "Delete Column" or "Delete N Columns".
    public string DeleteSelectedColumnsMenuText =>
        SelectedColumns.Count <= 1 ? "Delete Column" : $"Delete {SelectedColumns.Count} Columns";

    [ObservableProperty]
    public partial bool IsPasteInProgress { get; private set; }

    // The selected Worksheet's column collection; its Count is the visible column count
    // (Worksheet.ColumnCount is intentionally not updated by AddWorksheetColumnHandler).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedWorksheetSummary))]
    public partial ObservableCollection<WorksheetColumn>? SelectedWorksheetColumns { get; private set; }

    // e.g. "3 rows · 1 column"; rows from the raw store's row count (TotalRowCount), columns from the visible column list.
    public string? SelectedWorksheetSummary => SelectedWorksheet is null
        ? null
        : $"{CountLabel(TotalRowCount, "row")} · {CountLabel(SelectedWorksheetColumns?.Count ?? 0, "column")}";

    // Grid state holds one bounded page only (at most MainWindowSession.GridPageSize rows of display text),
    // never the worksheet's whole dataset or typed raw columns.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGridColumns))]
    public partial IReadOnlyList<WorksheetGridColumn> GridColumns { get; private set; } = [];

    public bool HasGridColumns => GridColumns.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridPageSummary))]
    public partial IReadOnlyList<WorksheetGridRow> GridRows { get; private set; } = [];

    // Logical row count of the selected worksheet, from the raw data store.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedWorksheetSummary))]
    [NotifyPropertyChangedFor(nameof(GridPageSummary))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial long TotalRowCount { get; private set; }

    // Zero-based worksheet row of the first grid row.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GridPageSummary))]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial long GridRowOffset { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial bool IsGridLoading { get; private set; }

    // e.g. "Rows 1–500 of 1,234".
    public string GridPageSummary => GridRows.Count == 0
        ? "No rows"
        : $"Rows {GridRowOffset + 1:N0}–{GridRowOffset + GridRows.Count:N0} of {TotalRowCount:N0}";

    // The most recently started grid page load; completes once its page (or error) has been applied.
    public Task GridLoadTask { get; private set; } = Task.CompletedTask;

    // Manual column-creation inputs and AddColumnCommand are not exposed in the Worksheet UI
    // (columns are expected to come from pasted data); retained for future ingestion/schema wiring and tests.
    [ObservableProperty]
    public partial string ColumnName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial WorksheetDataType SelectedColumnDataType { get; set; } = WorksheetDataType.Numeric;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSemanticTypeOption))]
    public partial ColumnSemanticType? SelectedColumnSemanticType { get; set; }

    [ObservableProperty]
    public partial string ColumnUnit { get; set; } = string.Empty;

    public IReadOnlyList<WorksheetDataType> DataTypeOptions => DataTypes;

    public IReadOnlyList<SemanticTypeOption> SemanticTypeOptions => SemanticTypes;

    public SemanticTypeOption SelectedSemanticTypeOption
    {
        get => SemanticTypes.First(option => option.Value == SelectedColumnSemanticType);
        set => SelectedColumnSemanticType = value?.Value;
    }

    [ObservableProperty]
    public partial bool IsProjectPanelVisible { get; private set; } = true;

    [ObservableProperty]
    public partial string? ErrorMessage { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateWorksheetCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddColumnCommand))]
    [NotifyCanExecuteChangedFor(nameof(PasteCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedColumnsCommand))]
    public partial bool IsBusy { get; private set; }

    partial void OnSelectedWorksheetChanged(Worksheet? value)
    {
        // An error raised for the previously selected Worksheet no longer applies.
        ErrorMessage = null;
        ClearColumnSelection();

        // Never show the previous worksheet's page while the new one loads.
        ApplyGridPage(WorksheetGridPage.Empty);

        if (value is null)
        {
            SelectedWorksheetColumns = null;
            GridLoadTask = LoadGridPageAsync(0);
            return;
        }

        if (!_columnsByWorksheet.TryGetValue(value.Id, out var columns))
        {
            columns = [];
            _columnsByWorksheet[value.Id] = columns;
        }

        SelectedWorksheetColumns = columns;
        GridLoadTask = LoadGridPageAsync(0);
    }

    // Loads the page starting at rowOffset for the selected worksheet (or clears the grid when none is selected).
    // Load failures surface through ErrorMessage; storage details stay out of the UI.
    private async Task LoadGridPageAsync(long rowOffset)
    {
        var version = ++_gridLoadVersion;
        var worksheet = SelectedWorksheet;
        if (worksheet is null)
        {
            ApplyGridPage(WorksheetGridPage.Empty);
            IsGridLoading = false;
            return;
        }

        IsGridLoading = true;
        try
        {
            var page = await _session.LoadWorksheetPageAsync(worksheet.Id, rowOffset, CancellationToken.None);
            if (version == _gridLoadVersion)
            {
                ApplyGridPage(page);
            }
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException)
        {
            if (version == _gridLoadVersion)
            {
                ErrorMessage = ex.Message;
            }
        }
        catch (RawDataStorageException)
        {
            if (version == _gridLoadVersion)
            {
                ErrorMessage = "The worksheet data could not be loaded.";
            }
        }
        finally
        {
            if (version == _gridLoadVersion)
            {
                IsGridLoading = false;
            }
        }
    }

    private void ApplyGridPage(WorksheetGridPage page)
    {
        GridColumns = page.Columns;
        GridRows = page.Rows;
        TotalRowCount = page.TotalRowCount;
        GridRowOffset = page.RowOffset;
    }

    private Task ReloadGridFromFirstPageAsync()
    {
        GridLoadTask = LoadGridPageAsync(0);
        return GridLoadTask;
    }

    [RelayCommand(CanExecute = nameof(CanGoToPreviousPage))]
    private Task PreviousPageAsync()
    {
        GridLoadTask = LoadGridPageAsync(Math.Max(0, GridRowOffset - MainWindowSession.GridPageSize));
        return GridLoadTask;
    }

    private bool CanGoToPreviousPage() => !IsGridLoading && SelectedWorksheet is not null && GridRowOffset > 0;

    [RelayCommand(CanExecute = nameof(CanGoToNextPage))]
    private Task NextPageAsync()
    {
        GridLoadTask = LoadGridPageAsync(GridRowOffset + MainWindowSession.GridPageSize);
        return GridLoadTask;
    }

    private bool CanGoToNextPage() =>
        !IsGridLoading && SelectedWorksheet is not null && GridRowOffset + MainWindowSession.GridPageSize < TotalRowCount;

    // Header click: that column alone becomes selected and active.
    public void SelectColumn(Guid columnId)
    {
        if (FindColumn(columnId) is { } column)
        {
            SetColumnSelection([column], column);
        }
    }

    // Ctrl+Click: adds the column (it becomes active) or removes it. Removing the active column makes the nearest
    // remaining selected column to its right active, otherwise the nearest one to its left, or none.
    public void ToggleColumnSelection(Guid columnId)
    {
        if (FindColumn(columnId) is not { } column)
        {
            return;
        }

        if (!SelectedColumns.Any(selected => selected.Id == columnId))
        {
            SetColumnSelection([.. SelectedColumns, column], column);
            return;
        }

        var remaining = SelectedColumns.Where(selected => selected.Id != columnId).ToArray();
        var active = ActiveColumn is not null && ActiveColumn.Id != columnId
            ? ActiveColumn
            : remaining.FirstOrDefault(selected => selected.Index > column.Index) ?? remaining.LastOrDefault();
        SetColumnSelection(remaining, active);
    }

    // Shift+Click: selects every column whose Index lies between the active column and this one; the active column
    // stays the anchor. Without an active column it acts like a plain click.
    public void ExtendColumnSelection(Guid columnId)
    {
        if (FindColumn(columnId) is not { } column)
        {
            return;
        }

        if (ActiveColumn is not { } anchor)
        {
            SetColumnSelection([column], column);
            return;
        }

        var (first, last) = (Math.Min(anchor.Index, column.Index), Math.Max(anchor.Index, column.Index));
        SetColumnSelection(SelectedWorksheetColumns!.Where(candidate => candidate.Index >= first && candidate.Index <= last), anchor);
    }

    // Right-click on a header: an unselected column becomes the only (active) selection; a selected column keeps the
    // whole selection, so the context menu acts on it.
    public void SelectColumnForContextMenu(Guid columnId)
    {
        if (!SelectedColumns.Any(selected => selected.Id == columnId))
        {
            SelectColumn(columnId);
        }
    }

    public void ClearColumnSelection() => SetColumnSelection([], null);

    private WorksheetColumn? FindColumn(Guid columnId) =>
        SelectedWorksheetColumns?.FirstOrDefault(candidate => candidate.Id == columnId);

    private void SetColumnSelection(IEnumerable<WorksheetColumn> selected, WorksheetColumn? active)
    {
        var ordered = selected.DistinctBy(column => column.Id).OrderBy(column => column.Index).ToArray();
        ActiveColumn = active;
        SelectedColumns = Array.AsReadOnly(ordered);
    }

    // After the column collection is reloaded (new instances with the same Ids), keeps the selection that still exists.
    private void RestoreColumnSelection(Guid? activeColumnId, IReadOnlyCollection<Guid> selectedColumnIds)
    {
        var selected = selectedColumnIds.Select(FindColumn).OfType<WorksheetColumn>().ToArray();
        var active = activeColumnId is { } id ? selected.FirstOrDefault(column => column.Id == id) : null;
        SetColumnSelection(active is null ? [] : selected, active);
    }

    partial void OnSelectedWorksheetColumnsChanged(
        ObservableCollection<WorksheetColumn>? oldValue,
        ObservableCollection<WorksheetColumn>? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.CollectionChanged -= OnSelectedWorksheetColumnsCollectionChanged;
        }

        if (newValue is not null)
        {
            newValue.CollectionChanged += OnSelectedWorksheetColumnsCollectionChanged;
        }
    }

    private void OnSelectedWorksheetColumnsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(SelectedWorksheetSummary));

    private static string CountLabel(long count, string singular)
        => count == 1 ? $"{count} {singular}" : $"{count} {singular}s";

    [RelayCommand(CanExecute = nameof(CanCreateProject))]
    private async Task CreateProjectAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        IsBusy = true;
        try
        {
            var description = string.IsNullOrWhiteSpace(ProjectDescription) ? null : ProjectDescription;
            var project = await _session.CreateProjectAsync(
                new CreateProjectRequest(ProjectName, description), cancellationToken);

            // One active Project at a time: switching clears the session Worksheet list and column state.
            Worksheets.Clear();
            SelectedWorksheet = null;
            _columnsByWorksheet.Clear();
            CurrentProject = project;
            ProjectName = string.Empty;
            ProjectDescription = string.Empty;
        }
        catch (ValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreateProject() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCreateWorksheet))]
    private async Task CreateWorksheetAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        if (CurrentProject is null)
        {
            ErrorMessage = "Create a Project before adding Worksheets.";
            return;
        }

        IsBusy = true;
        try
        {
            var worksheet = await _session.CreateWorksheetAsync(
                new CreateWorksheetRequest(CurrentProject.Id, WorksheetName), cancellationToken);

            Worksheets.Add(worksheet);
            SelectedWorksheet = worksheet;
            WorksheetName = string.Empty;
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreateWorksheet() => !IsBusy && CurrentProject is not null;

    [RelayCommand(CanExecute = nameof(CanAddColumn))]
    private async Task AddColumnAsync(CancellationToken cancellationToken)
    {
        if (IsBusy)
        {
            return;
        }

        ErrorMessage = null;
        if (SelectedWorksheet is null || SelectedWorksheetColumns is null)
        {
            ErrorMessage = "Select a Worksheet before adding columns.";
            return;
        }

        // Capture the target so a selection change during the await cannot misroute the result.
        var worksheet = SelectedWorksheet;
        var columns = SelectedWorksheetColumns;

        IsBusy = true;
        try
        {
            // Temporary policy: indices are assigned sequentially from the visible column list.
            var unit = string.IsNullOrWhiteSpace(ColumnUnit) ? null : ColumnUnit;
            var column = await _session.AddWorksheetColumnAsync(
                new AddWorksheetColumnCommand(
                    worksheet.Id, columns.Count, ColumnName, SelectedColumnDataType, SelectedColumnSemanticType, unit),
                cancellationToken);

            columns.Add(column);
            ColumnName = string.Empty;
            ColumnUnit = string.Empty;
            SelectedColumnSemanticType = null;

            // Not awaited: the column add itself is complete; the grid page follows (observable through GridLoadTask).
            if (ReferenceEquals(SelectedWorksheet, worksheet))
            {
                GridLoadTask = LoadGridPageAsync(0);
            }
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAddColumn() => !IsBusy && SelectedWorksheet is not null;

    [RelayCommand(CanExecute = nameof(CanPaste))]
    private async Task PasteAsync(CancellationToken cancellationToken)
    {
        // One paste at a time, and never alongside another operation: a repeated Ctrl+V is ignored, not queued.
        if (IsBusy || SelectedWorksheet is null || SelectedWorksheetColumns is null)
        {
            return;
        }

        // Capture the target so a selection change during the await cannot misroute the result.
        var worksheet = SelectedWorksheet;
        var columns = SelectedWorksheetColumns;
        var activeColumn = ActiveColumn;
        var selectedColumnIds = SelectedColumns.Select(column => column.Id).ToArray();

        IsBusy = true;
        IsPasteInProgress = true;
        try
        {
            // Paste starts at the active column; the rest of the selection does not affect the pasted range.
            var result = await _session.PasteFromClipboardAsync(worksheet.Id, activeColumn?.Index, cancellationToken);
            if (!result.IsPasted)
            {
                return;
            }

            // Refresh from the metadata the session reloaded from the repository, never from pasted cells.
            columns.Clear();
            foreach (var column in result.WorksheetColumns)
            {
                columns.Add(column);
            }

            ErrorMessage = null;

            // The collection now holds new instances; restore the selection by Id, since pasting keeps column Ids.
            if (ReferenceEquals(SelectedWorksheetColumns, columns))
            {
                RestoreColumnSelection(activeColumn?.Id, selectedColumnIds);

                // Show the pasted values: grid columns, row count and the first page, all re-read from storage.
                await ReloadGridFromFirstPageAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an error; the UI state stays as it was.
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException)
        {
            ErrorMessage = ex.Message;
        }
        catch (RawDataStorageException)
        {
            // Storage details stay out of the UI.
            ErrorMessage = "The pasted data could not be stored.";
        }
        finally
        {
            IsPasteInProgress = false;
            IsBusy = false;
        }
    }

    private bool CanPaste() => !IsBusy && SelectedWorksheet is not null;

    // Deletes all selected columns together as one batch (values and metadata); the remaining columns close the gaps.
    // Used by both the Delete key and the header context menu. With no selection this is a no-op.
    [RelayCommand(CanExecute = nameof(CanDeleteSelectedColumns))]
    private async Task DeleteSelectedColumnsAsync(CancellationToken cancellationToken)
    {
        if (IsBusy || SelectedWorksheet is null || SelectedWorksheetColumns is null || SelectedColumns.Count == 0)
        {
            return;
        }

        // Capture the targets so a selection change during the await cannot misroute the result.
        var worksheet = SelectedWorksheet;
        var columns = SelectedWorksheetColumns;
        var deletedColumnIds = SelectedColumns.Select(column => column.Id).ToArray();

        IsBusy = true;
        try
        {
            var remaining = await _session.DeleteColumnsAsync(worksheet.Id, deletedColumnIds, cancellationToken);

            columns.Clear();
            foreach (var column in remaining)
            {
                columns.Add(column);
            }

            ErrorMessage = null;

            if (ReferenceEquals(SelectedWorksheetColumns, columns))
            {
                ClearColumnSelection();

                // Show the result once: grid columns, row count and the first page, all re-read from storage.
                await ReloadGridFromFirstPageAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation is not an error; the UI state stays as it was.
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException)
        {
            ErrorMessage = ex.Message;
        }
        catch (RawDataStorageException)
        {
            // Storage details stay out of the UI.
            ErrorMessage = deletedColumnIds.Length == 1 ? "The column could not be deleted." : "The columns could not be deleted.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanDeleteSelectedColumns() => !IsBusy && SelectedWorksheet is not null && SelectedColumns.Count > 0;

    [RelayCommand]
    private void ToggleProjectPanel() => IsProjectPanelVisible = !IsProjectPanelVisible;
}
