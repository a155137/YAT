using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Exceptions;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.Application.Features.Worksheets.CreateWorksheet;
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

    private readonly CreateProjectHandler _createProject;
    private readonly CreateWorksheetHandler _createWorksheet;
    private readonly AddWorksheetColumnHandler _addWorksheetColumn;

    // Column metadata added in this UI session, keyed by Worksheet Id. Never row data.
    // Needed because IWorksheetColumnRepository has no read API yet.
    private readonly Dictionary<Guid, ObservableCollection<WorksheetColumn>> _columnsByWorksheet = [];

    public MainWindowViewModel(
        CreateProjectHandler createProject,
        CreateWorksheetHandler createWorksheet,
        AddWorksheetColumnHandler addWorksheetColumn)
    {
        _createProject = createProject;
        _createWorksheet = createWorksheet;
        _addWorksheetColumn = addWorksheetColumn;
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
    public partial Worksheet? SelectedWorksheet { get; set; }

    // The selected Worksheet's column collection; its Count is the visible column count
    // (Worksheet.ColumnCount is intentionally not updated by AddWorksheetColumnHandler).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedWorksheetSummary))]
    public partial ObservableCollection<WorksheetColumn>? SelectedWorksheetColumns { get; private set; }

    // e.g. "0 rows · 1 column"; rows from Worksheet.RowCount, columns from the visible column list.
    public string? SelectedWorksheetSummary => SelectedWorksheet is null
        ? null
        : $"{CountLabel(SelectedWorksheet.RowCount, "row")} · {CountLabel(SelectedWorksheetColumns?.Count ?? 0, "column")}";

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
    public partial bool IsBusy { get; private set; }

    partial void OnSelectedWorksheetChanged(Worksheet? value)
    {
        // An error raised for the previously selected Worksheet no longer applies.
        ErrorMessage = null;

        if (value is null)
        {
            SelectedWorksheetColumns = null;
            return;
        }

        if (!_columnsByWorksheet.TryGetValue(value.Id, out var columns))
        {
            columns = [];
            _columnsByWorksheet[value.Id] = columns;
        }

        SelectedWorksheetColumns = columns;
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
            var project = await _createProject.HandleAsync(
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
            var worksheet = await _createWorksheet.HandleAsync(
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
            var column = await _addWorksheetColumn.HandleAsync(
                new AddWorksheetColumnCommand(
                    worksheet.Id, columns.Count, ColumnName, SelectedColumnDataType, SelectedColumnSemanticType, unit),
                cancellationToken);

            columns.Add(column);
            ColumnName = string.Empty;
            ColumnUnit = string.Empty;
            SelectedColumnSemanticType = null;
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

    [RelayCommand]
    private void ToggleProjectPanel() => IsProjectPanelVisible = !IsProjectPanelVisible;
}
