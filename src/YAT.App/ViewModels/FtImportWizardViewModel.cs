using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.Composition;
using YAT.app.FtImport;

namespace YAT.app.ViewModels;

public enum FtImportWizardStep
{
    ProfileAndFiles = 0,
    TestItems = 1,
    Import = 2
}

public sealed partial class FtWizardFileItem : ObservableObject
{
    private readonly Action<Guid, bool> _onSelectionChanged;
    private bool _suppressCallback;

    public FtWizardFileItem(
        FtDiscoveredFile file,
        bool isSelected,
        Action<Guid, bool> onSelectionChanged)
    {
        File = file;
        _isSelected = isSelected;
        _onSelectionChanged = onSelectionChanged;
    }

    public FtDiscoveredFile File { get; }

    public Guid FileId => File.Id;

    public string FileName => File.FileName;

    public string SourcePath => File.SourcePath;

    public bool IsIdentified => File.IsIdentified;

    public bool HasLogicalCollision => File.HasLogicalCollision;

    public string TestStage => File.Match?.TestStage ?? "—";

    public string RoleDisplay => File.Match is null
        ? "Unidentified"
        : File.Match.TestRole == FtTestRole.Original ? "Original" : "Retest";

    public string RoundDisplay => File.Match?.RetestRound is { } round ? $"R{round}" : "—";

    public string PartDisplay => File.Match?.FilePart is { } part ? $"Part {part}" : "—";

    public string? DiagnosticMessage => File.IdentificationDiagnostic?.Message;

    [ObservableProperty]
    private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (_suppressCallback)
        {
            return;
        }

        _onSelectionChanged(FileId, value);
    }

    internal void SetSelectedSilently(bool value)
    {
        _suppressCallback = true;
        try
        {
            IsSelected = value;
        }
        finally
        {
            _suppressCallback = false;
        }
    }
}

public sealed partial class FtWizardLotGroupItem : ObservableObject
{
    private readonly Action<string, bool> _onLotSelectionChanged;
    private bool _suppressCallback;

    public FtWizardLotGroupItem(
        string lotId,
        bool? selectionState,
        IEnumerable<FtWizardFileItem> files,
        Action<string, bool> onLotSelectionChanged)
    {
        LotId = lotId;
        _selectionState = selectionState;
        _onLotSelectionChanged = onLotSelectionChanged;
        Files = new ObservableCollection<FtWizardFileItem>(files);
    }

    public string LotId { get; }

    public ObservableCollection<FtWizardFileItem> Files { get; }

    public string FileCountText => Files.Count == 1 ? "1 file" : $"{Files.Count} files";

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool? _selectionState;

    partial void OnSelectionStateChanged(bool? value)
    {
        if (_suppressCallback)
        {
            return;
        }

        _onLotSelectionChanged(LotId, value == true);
    }

    internal void SetSelectionStateSilently(bool? state)
    {
        _suppressCallback = true;
        try
        {
            SelectionState = state;
        }
        finally
        {
            _suppressCallback = false;
        }
    }
}

public sealed partial class FtImportWizardViewModel : ViewModelBase, IDisposable
{
    private readonly IImportProfileStore _profileStore;
    private readonly IFtImportWizardDialogs _dialogs;
    private readonly MainWindowSession _session;
    private readonly IReadOnlyList<string> _existingWorksheetNames;
    private readonly FtFileDiscoveryService _discoveryService;
    private readonly FtSchemaPreparationService _schemaService;

    private readonly List<string> _explicitFilePaths = [];
    private readonly List<string> _folderPaths = [];
    private readonly HashSet<string> _normalizedExplicitPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _normalizedFolderPaths = new(StringComparer.OrdinalIgnoreCase);

    private FtImportSelectionModel? _fileSelectionModel;
    private FtImportPlanResult? _currentPlanResult;
    private FtUnionSchema? _unionSchema;
    private FtTestItemSelectionModel? _testItemSelectionModel;
    private FtPreparedImportPlanResult? _preparedPlanResult;

    private CancellationTokenSource? _backgroundCts;
    private CancellationTokenSource? _importCts;
    private long _discoveryGeneration;
    private long _scanGeneration;
    private bool _suppressStageCallback;
    private bool _suppressProfileCallback;
    private bool _disposed;

    public FtImportWizardViewModel(
        IImportProfileStore profileStore,
        IFtImportWizardDialogs dialogs,
        MainWindowSession session,
        IReadOnlyList<string> existingWorksheetNames,
        FtFileDiscoveryService? discoveryService = null,
        FtSchemaPreparationService? schemaService = null)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(existingWorksheetNames);

        _profileStore = profileStore;
        _dialogs = dialogs;
        _session = session;
        _existingWorksheetNames = existingWorksheetNames;
        _discoveryService = discoveryService ?? new FtFileDiscoveryService();
        _schemaService = schemaService ?? new FtSchemaPreparationService();

        Step1Diagnostics.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStep1Diagnostics));
        Step2Diagnostics.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStep2Diagnostics));
        Step3Diagnostics.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasStep3Diagnostics));
    }

    public event Action<FtStreamingImportResult?>? RequestClose;

    public ObservableCollection<ImportProfile> Profiles { get; } = [];
    public ObservableCollection<string> AvailableStages { get; } = [];
    public ObservableCollection<FtWizardLotGroupItem> LotGroups { get; } = [];
    public ObservableCollection<FtImportDiagnostic> Step1Diagnostics { get; } = [];

    public ObservableCollection<string> FilteredAvailableHeaders { get; } = [];
    public ObservableCollection<string> FilteredSelectedHeaders { get; } = [];
    public ObservableCollection<string> SelectedAvailableHighlights { get; } = [];
    public ObservableCollection<string> SelectedChosenHighlights { get; } = [];
    public ObservableCollection<FtImportDiagnostic> Step2Diagnostics { get; } = [];

    public ObservableCollection<FtImportDiagnostic> Step3Diagnostics { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    [NotifyPropertyChangedFor(nameof(IsStep3))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyPropertyChangedFor(nameof(ShowNextButton))]
    [NotifyPropertyChangedFor(nameof(ShowImportButton))]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    private FtImportWizardStep _currentStep = FtImportWizardStep.ProfileAndFiles;

    [ObservableProperty]
    private ImportProfile? _selectedProfile;

    [ObservableProperty]
    private string? _selectedStage;

    [ObservableProperty]
    private FtWizardLotGroupItem? _selectedLotGroup;

    [ObservableProperty]
    private FtWizardFileItem? _selectedFileItem;

    [ObservableProperty]
    private string _availableSearchText = string.Empty;

    [ObservableProperty]
    private string _selectedSearchText = string.Empty;

    [ObservableProperty]
    private int _availableCount;

    [ObservableProperty]
    private int _selectedCount;

    [ObservableProperty]
    private int _totalUnionCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    private string _worksheetName = string.Empty;

    [ObservableProperty]
    private string? _worksheetNameError;

    [ObservableProperty]
    private string _summaryProfileName = "—";

    [ObservableProperty]
    private string _summaryStage = "—";

    [ObservableProperty]
    private int _summaryLotCount;

    [ObservableProperty]
    private int _summaryFileCount;

    [ObservableProperty]
    private int _summarySelectedMeasurementCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    [NotifyCanExecuteChangedFor(nameof(AddFilesCommand))]
    [NotifyCanExecuteChangedFor(nameof(AddFolderCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveSelectedSourcesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ClearSourcesCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    [NotifyCanExecuteChangedFor(nameof(NextCommand))]
    [NotifyCanExecuteChangedFor(nameof(RescanSchemaCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCloseDialog))]
    [NotifyCanExecuteChangedFor(nameof(CancelImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(StartImportCommand))]
    [NotifyCanExecuteChangedFor(nameof(BackCommand))]
    private bool _isImporting;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    [ObservableProperty]
    private string _progressPhaseText = "Ready to import.";

    [ObservableProperty]
    private string _progressFileText = "—";

    [ObservableProperty]
    private long _processedRowCount;

    [ObservableProperty]
    private long _retainedRowCount;

    [ObservableProperty]
    private long _filteredRowCount;

    public bool IsStep1 => CurrentStep == FtImportWizardStep.ProfileAndFiles;
    public bool IsStep2 => CurrentStep == FtImportWizardStep.TestItems;
    public bool IsStep3 => CurrentStep == FtImportWizardStep.Import;

    public bool ShowNextButton => CurrentStep is FtImportWizardStep.ProfileAndFiles or FtImportWizardStep.TestItems;
    public bool ShowImportButton => CurrentStep == FtImportWizardStep.Import;
    public bool CanCloseDialog => !IsImporting;

    public bool CanGoBack => !IsBusy && !IsImporting && CurrentStep != FtImportWizardStep.ProfileAndFiles;

    public bool CanGoNext => CurrentStep switch
    {
        FtImportWizardStep.ProfileAndFiles => !IsBusy && _currentPlanResult is { IsValid: true, Plan: not null },
        FtImportWizardStep.TestItems => !IsBusy && !IsScanning && _preparedPlanResult is { IsValid: true, Plan: not null },
        _ => false
    };

    public string StepTitle => CurrentStep switch
    {
        FtImportWizardStep.ProfileAndFiles => "Step 1 of 3 — Select Profile & Datalog Files",
        FtImportWizardStep.TestItems => "Step 2 of 3 — Select Test Items",
        FtImportWizardStep.Import => "Step 3 of 3 — Confirm & Import",
        _ => "FT Datalog Import"
    };

    public FtStreamingImportResult? CommittedResult { get; private set; }

    public bool HasStep1Diagnostics => Step1Diagnostics.Count > 0;
    public bool HasStep2Diagnostics => Step2Diagnostics.Count > 0;
    public bool HasStep3Diagnostics => Step3Diagnostics.Count > 0;
    public bool HasSources => _explicitFilePaths.Count > 0 || _folderPaths.Count > 0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        IsBusy = true;
        StatusMessage = "Loading import profiles...";
        try
        {
            var result = await Task.Run(() => _profileStore.Enumerate(), cancellationToken);
            Profiles.Clear();
            Step1Diagnostics.Clear();

            foreach (var profile in result.Profiles)
            {
                Profiles.Add(profile);
            }

            foreach (var issue in result.Issues)
            {
                Step1Diagnostics.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Warning,
                    FtImportDiagnosticCode.InvalidProfile,
                    $"{issue.FileName}: {issue.Detail}"));
            }

            OnPropertyChanged(nameof(HasStep1Diagnostics));

            _suppressProfileCallback = true;
            try
            {
                SelectedProfile = Profiles.FirstOrDefault();
            }
            finally
            {
                _suppressProfileCallback = false;
            }

            StatusMessage = Profiles.Count == 0
                ? "No import profiles found. Create one in File > FT Profile Manager..."
                : "Select files or a folder to discover FT datalogs.";
        }
        finally
        {
            IsBusy = false;
            NotifyStepStateChanged();
        }
    }

    partial void OnSelectedProfileChanged(ImportProfile? value)
    {
        if (_suppressProfileCallback)
        {
            return;
        }

        InvalidateDownstreamFromStep1();
        if (value is not null && HasSources)
        {
            _ = RunDiscoveryAsync();
        }
        else
        {
            ClearDiscoveryPresentation();
        }
    }

    partial void OnSelectedStageChanged(string? value)
    {
        if (_suppressStageCallback || _fileSelectionModel is null)
        {
            return;
        }

        InvalidateDownstreamFromStep2();
        if (!string.IsNullOrWhiteSpace(value))
        {
            _fileSelectionModel.SelectOnlyStage(value);
            SyncSelectionPresentation();
            RebuildFilePlan();
        }
    }

    partial void OnAvailableSearchTextChanged(string value) => RefreshTestItemLists();

    partial void OnSelectedSearchTextChanged(string value) => RefreshTestItemLists();

    partial void OnWorksheetNameChanged(string value)
    {
        ValidateWorksheetName();
        StartImportCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanEditStep1Sources))]
    private async Task AddFilesAsync()
    {
        var files = await _dialogs.PickDatalogFilesAsync();
        if (files.Count > 0)
        {
            await AddFilesFromPathsAsync(files);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditStep1Sources))]
    private async Task AddFolderAsync()
    {
        var folder = await _dialogs.PickDatalogFolderAsync();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            await AddFolderFromPathAsync(folder);
        }
    }

    private bool CanEditStep1Sources() => !IsBusy && !IsImporting;

    public async Task AddFilesFromPathsAsync(IEnumerable<string> filePaths)
    {
        ArgumentNullException.ThrowIfNull(filePaths);
        var added = false;
        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var full = Path.GetFullPath(path);
            if (_normalizedExplicitPaths.Add(full))
            {
                _explicitFilePaths.Add(full);
                added = true;
            }
        }

        if (added)
        {
            OnPropertyChanged(nameof(HasSources));
            InvalidateDownstreamFromStep1();
            await RunDiscoveryAsync();
        }
    }

    public async Task AddFolderFromPathAsync(string folderPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        var full = Path.GetFullPath(folderPath);
        if (_normalizedFolderPaths.Add(full))
        {
            _folderPaths.Add(full);
            OnPropertyChanged(nameof(HasSources));
            InvalidateDownstreamFromStep1();
            await RunDiscoveryAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedSources))]
    private async Task RemoveSelectedSourcesAsync()
    {
        var removed = false;
        if (SelectedFileItem is not null)
        {
            var full = Path.GetFullPath(SelectedFileItem.SourcePath);
            if (_normalizedExplicitPaths.Remove(full))
            {
                _explicitFilePaths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
                removed = true;
            }
            else if (SelectedFileItem.File.IsFromFolder)
            {
                var dir = Path.GetDirectoryName(full);
                if (dir is not null && _normalizedFolderPaths.Remove(dir))
                {
                    _folderPaths.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
                    removed = true;
                }
            }
        }
        else if (SelectedLotGroup is not null)
        {
            foreach (var file in SelectedLotGroup.Files)
            {
                var full = Path.GetFullPath(file.SourcePath);
                if (_normalizedExplicitPaths.Remove(full))
                {
                    _explicitFilePaths.RemoveAll(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
                    removed = true;
                }
                else if (file.File.IsFromFolder)
                {
                    var dir = Path.GetDirectoryName(full);
                    if (dir is not null && _normalizedFolderPaths.Remove(dir))
                    {
                        _folderPaths.RemoveAll(p => string.Equals(p, dir, StringComparison.OrdinalIgnoreCase));
                        removed = true;
                    }
                }
            }
        }

        if (removed)
        {
            OnPropertyChanged(nameof(HasSources));
            InvalidateDownstreamFromStep1();
            if (HasSources)
            {
                await RunDiscoveryAsync();
            }
            else
            {
                ClearDiscoveryPresentation();
            }
        }
    }

    private bool CanRemoveSelectedSources() => !IsBusy && !IsImporting && (SelectedFileItem is not null || SelectedLotGroup is not null);

    [RelayCommand(CanExecute = nameof(CanClearSources))]
    private void ClearSources()
    {
        CancelBackgroundOperations();
        _discoveryGeneration++;
        _explicitFilePaths.Clear();
        _folderPaths.Clear();
        _normalizedExplicitPaths.Clear();
        _normalizedFolderPaths.Clear();
        OnPropertyChanged(nameof(HasSources));
        InvalidateDownstreamFromStep1();
        ClearDiscoveryPresentation();
        StatusMessage = "Source selection cleared.";
    }

    private bool CanClearSources() => !IsBusy && !IsImporting && HasSources;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private void Back()
    {
        CancelBackgroundOperations();
        if (CurrentStep == FtImportWizardStep.Import)
        {
            CurrentStep = FtImportWizardStep.TestItems;
            Step3Diagnostics.Clear();
            OnPropertyChanged(nameof(HasStep3Diagnostics));
            StatusMessage = "Adjust selected Test Items or return to Step 1.";
        }
        else if (CurrentStep == FtImportWizardStep.TestItems)
        {
            CurrentStep = FtImportWizardStep.ProfileAndFiles;
            StatusMessage = "Adjust profile or file selection.";
        }

        NotifyStepStateChanged();
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextAsync()
    {
        if (CurrentStep == FtImportWizardStep.ProfileAndFiles)
        {
            if (_currentPlanResult is not { IsValid: true, Plan: not null })
            {
                return;
            }

            CurrentStep = FtImportWizardStep.TestItems;
            NotifyStepStateChanged();

            if (_unionSchema is null)
            {
                await RunSchemaScanAsync(_currentPlanResult.Plan);
            }
        }
        else if (CurrentStep == FtImportWizardStep.TestItems)
        {
            if (_preparedPlanResult is not { IsValid: true, Plan: { } prepared })
            {
                return;
            }

            SummaryProfileName = prepared.ProfileSnapshot.Name;
            SummaryStage = prepared.ImportPlan.TestStage;
            SummaryLotCount = prepared.ImportPlan.Lots.Count;
            SummaryFileCount = prepared.Sources.Count;
            SummarySelectedMeasurementCount = prepared.SelectedMeasurementHeaders.Count;

            if (string.IsNullOrWhiteSpace(WorksheetName))
            {
                WorksheetName = SuggestDefaultWorksheetName(prepared.ImportPlan.TestStage);
            }

            ValidateWorksheetName();
            Step3Diagnostics.Clear();
            OnPropertyChanged(nameof(HasStep3Diagnostics));
            ProgressPhaseText = "Ready to import.";
            ProgressFileText = "—";
            ProcessedRowCount = 0;
            RetainedRowCount = 0;
            FilteredRowCount = 0;

            CurrentStep = FtImportWizardStep.Import;
            NotifyStepStateChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRescanSchema))]
    private async Task RescanSchemaAsync()
    {
        if (_currentPlanResult is { IsValid: true, Plan: not null })
        {
            InvalidateDownstreamFromStep2();
            await RunSchemaScanAsync(_currentPlanResult.Plan);
        }
    }

    private bool CanRescanSchema() => !IsBusy && !IsImporting && _currentPlanResult is { IsValid: true, Plan: not null };

    [RelayCommand(CanExecute = nameof(CanAddSelectedItems))]
    private void AddSelectedItems()
    {
        if (_testItemSelectionModel is null || SelectedAvailableHighlights.Count == 0)
        {
            return;
        }

        var toAdd = SelectedAvailableHighlights.Where(h => !_testItemSelectionModel.IsSelected(h)).ToArray();
        if (toAdd.Length > 0)
        {
            _testItemSelectionModel.AddRange(toAdd);
            SelectedAvailableHighlights.Clear();
            RebuildPreparedPlan();
            RefreshTestItemLists();
        }
    }

    private bool CanAddSelectedItems() => !IsBusy && !IsScanning && _testItemSelectionModel is not null && SelectedAvailableHighlights.Count > 0;

    [RelayCommand(CanExecute = nameof(CanRemoveSelectedItems))]
    private void RemoveSelectedItems()
    {
        if (_testItemSelectionModel is null || SelectedChosenHighlights.Count == 0)
        {
            return;
        }

        var toRemove = SelectedChosenHighlights.Where(h => _testItemSelectionModel.IsSelected(h)).ToArray();
        if (toRemove.Length > 0)
        {
            _testItemSelectionModel.RemoveRange(toRemove);
            SelectedChosenHighlights.Clear();
            RebuildPreparedPlan();
            RefreshTestItemLists();
        }
    }

    private bool CanRemoveSelectedItems() => !IsBusy && !IsScanning && _testItemSelectionModel is not null && SelectedChosenHighlights.Count > 0;

    [RelayCommand(CanExecute = nameof(CanSelectAllItems))]
    private void SelectAllItems()
    {
        if (_testItemSelectionModel is null)
        {
            return;
        }

        _testItemSelectionModel.SelectAll();
        SelectedAvailableHighlights.Clear();
        RebuildPreparedPlan();
        RefreshTestItemLists();
    }

    private bool CanSelectAllItems() => !IsBusy && !IsScanning && _testItemSelectionModel is not null && _testItemSelectionModel.AvailableCount > _testItemSelectionModel.SelectedCount;

    [RelayCommand(CanExecute = nameof(CanClearAllItems))]
    private void ClearAllItems()
    {
        if (_testItemSelectionModel is null)
        {
            return;
        }

        _testItemSelectionModel.ClearAll();
        SelectedChosenHighlights.Clear();
        RebuildPreparedPlan();
        RefreshTestItemLists();
    }

    private bool CanClearAllItems() => !IsBusy && !IsScanning && _testItemSelectionModel is not null && _testItemSelectionModel.SelectedCount > 0;

    [RelayCommand(CanExecute = nameof(CanStartImport))]
    private async Task StartImportAsync()
    {
        if (IsImporting || _preparedPlanResult is not { IsValid: true, Plan: { } prepared })
        {
            return;
        }

        ValidateWorksheetName();
        if (!string.IsNullOrEmpty(WorksheetNameError))
        {
            return;
        }

        IsImporting = true;
        IsBusy = true;
        Step3Diagnostics.Clear();
        OnPropertyChanged(nameof(HasStep3Diagnostics));
        ProgressPhaseText = "Starting import...";
        ProgressFileText = $"0 of {prepared.Sources.Count}";
        ProcessedRowCount = 0;
        RetainedRowCount = 0;
        FilteredRowCount = 0;

        _importCts?.Dispose();
        _importCts = new CancellationTokenSource();
        var token = _importCts.Token;

        // Asynchronous Progress<T> posts updates to the UI synchronization context; never re-enter DB/session here.
        // Guard against late callbacks arriving after completion, cancellation, or Wizard disposal.
        var progress = new Progress<FtImportProgress>(p => ApplyProgressSnapshot(p));

        try
        {
            var result = await _session.ImportFtDatalogAsync(
                WorksheetName.Trim(),
                prepared,
                progress,
                token);

            if (result.IsSuccess && result.Worksheet is not null)
            {
                CommittedResult = result;
                ProgressPhaseText = "Import committed.";
                StatusMessage = $"Imported {result.TotalRetainedRowCount:N0} rows into '{result.Worksheet.Name}'.";
                RequestClose?.Invoke(result);
                return;
            }

            foreach (var diag in result.AllDiagnostics)
            {
                Step3Diagnostics.Add(diag);
            }

            OnPropertyChanged(nameof(HasStep3Diagnostics));
            ProgressPhaseText = "Import failed. No Worksheet was created.";
            StatusMessage = result.Errors.FirstOrDefault()?.Message ?? "Import failed.";
        }
        catch (OperationCanceledException) when (CommittedResult is null)
        {
            ProgressPhaseText = "Import cancelled. No Worksheet was created.";
            StatusMessage = "Import was cancelled before commit.";
        }
        catch (Exception ex)
        {
            Step3Diagnostics.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.WorksheetCreationFailed,
                ex.Message));
            OnPropertyChanged(nameof(HasStep3Diagnostics));
            ProgressPhaseText = "Import failed. No Worksheet was created.";
            StatusMessage = ex.Message;
        }
        finally
        {
            _importCts.Dispose();
            _importCts = null;
            IsImporting = false;
            IsBusy = false;
            NotifyStepStateChanged();
        }
    }

    private bool CanStartImport() =>
        !IsBusy
        && !IsImporting
        && CurrentStep == FtImportWizardStep.Import
        && _preparedPlanResult is { IsValid: true, Plan: not null }
        && string.IsNullOrEmpty(WorksheetNameError)
        && !string.IsNullOrWhiteSpace(WorksheetName);

    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    private void CancelImport()
    {
        _importCts?.Cancel();
        ProgressPhaseText = "Cancelling import...";
    }

    private bool CanCancelImport() => IsImporting && _importCts is not null;

    internal void ApplyProgressSnapshot(FtImportProgress p)
    {
        if (_disposed || !IsImporting || CommittedResult is not null)
        {
            return;
        }

        ProgressPhaseText = p.Phase switch
        {
            FtImportProgressPhase.Validating => "Validating source files...",
            FtImportProgressPhase.StreamingSource => "Streaming datalog rows...",
            FtImportProgressPhase.Committing => "Committing worksheet transaction...",
            FtImportProgressPhase.Completed => "Import committed.",
            _ => ProgressPhaseText
        };
        ProgressFileText = p.TotalFileCount > 0 && !string.IsNullOrEmpty(p.CurrentFileName)
            ? $"File {p.CurrentFileIndex} of {p.TotalFileCount}: {p.CurrentFileName}"
            : $"{p.CurrentFileIndex} of {p.TotalFileCount}";
        ProcessedRowCount = p.ProcessedRowCount;
        RetainedRowCount = p.RetainedRowCount;
        FilteredRowCount = p.FilteredRowCount;
    }

    public void NotifySelectionHighlightsChanged()
    {
        AddSelectedItemsCommand.NotifyCanExecuteChanged();
        RemoveSelectedItemsCommand.NotifyCanExecuteChanged();
    }

    public void NotifyStep1SelectionHighlightChanged()
    {
        RemoveSelectedSourcesCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelBackgroundOperations();
        _importCts?.Cancel();
        _importCts?.Dispose();
        _importCts = null;
    }

    private async Task RunDiscoveryAsync()
    {
        if (SelectedProfile is not { } profile || !HasSources)
        {
            return;
        }

        CancelBackgroundOperations();
        var generation = ++_discoveryGeneration;
        var cts = new CancellationTokenSource();
        _backgroundCts = cts;
        var explicitCopy = _explicitFilePaths.ToArray();
        var folderCopy = _folderPaths.ToArray();

        IsBusy = true;
        StatusMessage = "Discovering datalog files...";
        NotifyStepStateChanged();

        try
        {
            var discovery = await Task.Run(() =>
            {
                var mergedExplicit = explicitCopy.ToList();
                var primaryFolder = folderCopy.Length > 0 ? folderCopy[0] : null;
                for (var i = 1; i < folderCopy.Length; i++)
                {
                    if (Directory.Exists(folderCopy[i]))
                    {
                        mergedExplicit.AddRange(Directory.EnumerateFiles(folderCopy[i], "*", SearchOption.TopDirectoryOnly));
                    }
                }

                return _discoveryService.Discover(profile, mergedExplicit, primaryFolder, cts.Token);
            }, cts.Token);

            if (generation != _discoveryGeneration || cts.IsCancellationRequested)
            {
                return;
            }

            _fileSelectionModel = new FtImportSelectionModel(discovery);

            var stages = discovery.Files
                .Where(f => f.IsIdentified && f.Match is not null)
                .Select(f => f.Match!.TestStage)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            _suppressStageCallback = true;
            try
            {
                AvailableStages.Clear();
                foreach (var stage in stages)
                {
                    AvailableStages.Add(stage);
                }

                if (stages.Length == 1)
                {
                    SelectedStage = stages[0];
                    _fileSelectionModel.SelectOnlyStage(stages[0]);
                }
                else
                {
                    SelectedStage = null;
                }
            }
            finally
            {
                _suppressStageCallback = false;
            }

            PopulateLotGroups(discovery);
            RebuildFilePlan();

            StatusMessage = discovery.Files.Count == 0
                ? "No supported datalog files were found."
                : $"Discovered {discovery.Files.Count} file(s) across {discovery.LotGroups.Count} lot(s).";
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer discovery or user navigation.
        }
        finally
        {
            if (generation == _discoveryGeneration)
            {
                IsBusy = false;
                NotifyStepStateChanged();
            }
        }
    }

    private async Task RunSchemaScanAsync(FtImportPlan plan)
    {
        CancelBackgroundOperations();
        var generation = ++_scanGeneration;
        var cts = new CancellationTokenSource();
        _backgroundCts = cts;

        IsBusy = true;
        IsScanning = true;
        Step2Diagnostics.Clear();
        OnPropertyChanged(nameof(HasStep2Diagnostics));
        StatusMessage = $"Scanning structure of {plan.OrderedFiles.Count} selected source file(s)...";
        NotifyStepStateChanged();

        try
        {
            var scanResult = await Task.Run(
                () => _schemaService.ScanUnionSchema(plan, cts.Token),
                cts.Token);

            if (generation != _scanGeneration || cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var diag in scanResult.AllDiagnostics)
            {
                Step2Diagnostics.Add(diag);
            }

            OnPropertyChanged(nameof(HasStep2Diagnostics));

            if (!scanResult.IsSuccess || scanResult.UnionSchema is null)
            {
                _unionSchema = null;
                _testItemSelectionModel = null;
                _preparedPlanResult = null;
                RefreshTestItemLists();
                StatusMessage = scanResult.Errors.FirstOrDefault()?.Message ?? "Structure scan failed.";
                return;
            }

            _unionSchema = scanResult.UnionSchema;
            _testItemSelectionModel = new FtTestItemSelectionModel(_unionSchema, selectAllByDefault: false);
            RebuildPreparedPlan();
            RefreshTestItemLists();
            StatusMessage = $"Found {_unionSchema.UnionMeasurementHeaders.Count} measurement header(s) across {_unionSchema.ScannedFiles.Count} source file(s).";
        }
        catch (OperationCanceledException)
        {
            // Superseded or navigated back.
        }
        finally
        {
            if (generation == _scanGeneration)
            {
                IsScanning = false;
                IsBusy = false;
                NotifyStepStateChanged();
            }
        }
    }

    private void PopulateLotGroups(FtFileDiscoveryResult discovery)
    {
        LotGroups.Clear();
        if (_fileSelectionModel is null)
        {
            return;
        }

        foreach (var lot in discovery.LotGroups)
        {
            var files = lot.GetAllFiles()
                .Select(f => new FtWizardFileItem(f, _fileSelectionModel.IsFileSelected(f.Id), OnFileItemSelectionChanged))
                .ToArray();
            var state = ToThreeState(_fileSelectionModel.GetLotSelectionState(lot.LotId));
            LotGroups.Add(new FtWizardLotGroupItem(lot.LotId, state, files, OnLotItemSelectionChanged));
        }

        var unidentified = discovery.Files.Where(f => !f.IsIdentified).ToArray();
        if (unidentified.Length > 0)
        {
            var unidItems = unidentified
                .Select(f => new FtWizardFileItem(f, _fileSelectionModel.IsFileSelected(f.Id), OnFileItemSelectionChanged))
                .ToArray();
            LotGroups.Add(new FtWizardLotGroupItem("Unidentified Files", false, unidItems, (_, _) => { }));
        }
    }

    private void OnFileItemSelectionChanged(Guid fileId, bool isSelected)
    {
        if (_fileSelectionModel is null)
        {
            return;
        }

        InvalidateDownstreamFromStep2();
        _fileSelectionModel.SetFileSelected(fileId, isSelected);
        SyncSelectionPresentation();
        RebuildFilePlan();
    }

    private void OnLotItemSelectionChanged(string lotId, bool isSelected)
    {
        if (_fileSelectionModel is null)
        {
            return;
        }

        InvalidateDownstreamFromStep2();
        _fileSelectionModel.SetLotSelected(lotId, isSelected);
        SyncSelectionPresentation();
        RebuildFilePlan();
    }

    private void SyncSelectionPresentation()
    {
        if (_fileSelectionModel is null)
        {
            return;
        }

        foreach (var group in LotGroups)
        {
            if (group.LotId == "Unidentified Files")
            {
                continue;
            }

            group.SetSelectionStateSilently(ToThreeState(_fileSelectionModel.GetLotSelectionState(group.LotId)));
            foreach (var item in group.Files)
            {
                item.SetSelectedSilently(_fileSelectionModel.IsFileSelected(item.FileId));
            }
        }
    }

    private void RebuildFilePlan()
    {
        Step1Diagnostics.Clear();
        if (_fileSelectionModel is null)
        {
            _currentPlanResult = null;
            OnPropertyChanged(nameof(HasStep1Diagnostics));
            NotifyStepStateChanged();
            return;
        }

        foreach (var diag in _fileSelectionModel.Discovery.Diagnostics)
        {
            Step1Diagnostics.Add(diag);
        }

        _currentPlanResult = _fileSelectionModel.BuildImportPlan();
        foreach (var diag in _currentPlanResult.AllDiagnostics)
        {
            Step1Diagnostics.Add(diag);
        }

        OnPropertyChanged(nameof(HasStep1Diagnostics));
        NotifyStepStateChanged();
    }

    private void RebuildPreparedPlan()
    {
        if (_unionSchema is null || _testItemSelectionModel is null)
        {
            _preparedPlanResult = null;
            NotifyStepStateChanged();
            return;
        }

        _preparedPlanResult = _schemaService.PrepareImportPlan(_testItemSelectionModel);
        Step2Diagnostics.Clear();
        foreach (var diag in _preparedPlanResult.AllDiagnostics)
        {
            Step2Diagnostics.Add(diag);
        }

        OnPropertyChanged(nameof(HasStep2Diagnostics));
        NotifyStepStateChanged();
    }

    private void RefreshTestItemLists()
    {
        FilteredAvailableHeaders.Clear();
        FilteredSelectedHeaders.Clear();

        if (_testItemSelectionModel is null)
        {
            AvailableCount = 0;
            SelectedCount = 0;
            TotalUnionCount = 0;
            NotifyTestItemCommands();
            return;
        }

        var unselected = _testItemSelectionModel.GetUnselectedHeaders();
        var selected = _testItemSelectionModel.GetSelectedHeaders();

        TotalUnionCount = _testItemSelectionModel.AvailableCount;
        AvailableCount = unselected.Count;
        SelectedCount = selected.Count;

        var availFilter = AvailableSearchText?.Trim() ?? string.Empty;
        foreach (var h in unselected)
        {
            if (availFilter.Length == 0 || h.Contains(availFilter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredAvailableHeaders.Add(h);
            }
        }

        var selFilter = SelectedSearchText?.Trim() ?? string.Empty;
        foreach (var h in selected)
        {
            if (selFilter.Length == 0 || h.Contains(selFilter, StringComparison.OrdinalIgnoreCase))
            {
                FilteredSelectedHeaders.Add(h);
            }
        }

        NotifyTestItemCommands();
    }

    private void ValidateWorksheetName()
    {
        var trimmed = WorksheetName?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            WorksheetNameError = "Worksheet name must not be empty.";
            return;
        }

        if (_existingWorksheetNames.Any(n => string.Equals(n, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            WorksheetNameError = $"A worksheet named '{trimmed}' already exists in this project.";
            return;
        }

        WorksheetNameError = null;
    }

    private string SuggestDefaultWorksheetName(string testStage)
    {
        var baseName = string.IsNullOrWhiteSpace(testStage) ? "FT_Import" : $"FT_{testStage.Trim()}";
        if (!_existingWorksheetNames.Any(n => string.Equals(n, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        var suffix = 2;
        while (_existingWorksheetNames.Any(n => string.Equals(n, $"{baseName}_{suffix}", StringComparison.OrdinalIgnoreCase)))
        {
            suffix++;
        }

        return $"{baseName}_{suffix}";
    }

    private void ClearDiscoveryPresentation()
    {
        _fileSelectionModel = null;
        _currentPlanResult = null;
        _suppressStageCallback = true;
        try
        {
            AvailableStages.Clear();
            SelectedStage = null;
        }
        finally
        {
            _suppressStageCallback = false;
        }

        LotGroups.Clear();
        Step1Diagnostics.Clear();
        OnPropertyChanged(nameof(HasStep1Diagnostics));
        NotifyStepStateChanged();
    }

    private void InvalidateDownstreamFromStep1()
    {
        _currentPlanResult = null;
        InvalidateDownstreamFromStep2();
    }

    private void InvalidateDownstreamFromStep2()
    {
        _scanGeneration++;
        _unionSchema = null;
        _testItemSelectionModel = null;
        _preparedPlanResult = null;
        FilteredAvailableHeaders.Clear();
        FilteredSelectedHeaders.Clear();
        SelectedAvailableHighlights.Clear();
        SelectedChosenHighlights.Clear();
        Step2Diagnostics.Clear();
        OnPropertyChanged(nameof(HasStep2Diagnostics));
        AvailableCount = 0;
        SelectedCount = 0;
        TotalUnionCount = 0;
        NotifyTestItemCommands();
        NotifyStepStateChanged();
    }

    private void CancelBackgroundOperations()
    {
        if (_backgroundCts is not null)
        {
            _backgroundCts.Cancel();
            _backgroundCts.Dispose();
            _backgroundCts = null;
        }
    }

    private void NotifyTestItemCommands()
    {
        AddSelectedItemsCommand.NotifyCanExecuteChanged();
        RemoveSelectedItemsCommand.NotifyCanExecuteChanged();
        SelectAllItemsCommand.NotifyCanExecuteChanged();
        ClearAllItemsCommand.NotifyCanExecuteChanged();
    }

    private void NotifyStepStateChanged()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoNext));
        BackCommand.NotifyCanExecuteChanged();
        NextCommand.NotifyCanExecuteChanged();
        RescanSchemaCommand.NotifyCanExecuteChanged();
        StartImportCommand.NotifyCanExecuteChanged();
        CancelImportCommand.NotifyCanExecuteChanged();
    }

    private static bool? ToThreeState(FtSelectionState state) => state switch
    {
        FtSelectionState.Selected => true,
        FtSelectionState.PartiallySelected => null,
        _ => false
    };
}
