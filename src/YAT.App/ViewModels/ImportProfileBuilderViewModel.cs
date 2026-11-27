using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;

namespace YAT.app.ViewModels;

public enum DelimiterPreset
{
    Comma,
    Tab,
    Semicolon,
    Custom
}

// ViewModel for the Visual FT Import Profile Builder (FT-02.2 / FT-02.3).
// Operates strictly on an isolated draft; uses ImportProfileValidator as the sole validation authority.
public sealed partial class ImportProfileBuilderViewModel : ObservableObject, IDisposable
{
    private readonly ImportProfileValidator _validator = new();
    private bool _suppressAutoRefresh;

    public ImportProfileBuilderViewModel(
        ImportProfileEditorRequest request,
        Func<Guid>? newId = null,
        DatalogSamplePreviewService? previewService = null,
        Action<Action>? previewUiDispatch = null,
        TimeSpan? previewDebounce = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        Mode = request.Mode;
        if (request.Mode == ImportProfileEditorMode.Edit && request.ExistingProfile is null)
        {
            throw new ArgumentException("Edit mode requires an ExistingProfile.", nameof(request));
        }

        ProfileId = request.Mode == ImportProfileEditorMode.Edit
            ? request.ExistingProfile!.Id
            : (newId?.Invoke() ?? Guid.NewGuid());
        SchemaVersion = request.ExistingProfile?.SchemaVersion ?? ImportProfile.CurrentSchemaVersion;

        AddExtensionCommand = new RelayCommand(AddExtension);
        RemoveExtensionCommand = new RelayCommand<string>(RemoveExtension);
        AddIgnoredColumnCommand = new RelayCommand(AddIgnoredColumn);
        RemoveIgnoredColumnCommand = new RelayCommand<ColumnSelectorDraft>(RemoveIgnoredColumn);
        AddCompositeFieldCommand = new RelayCommand(AddCompositeField);
        RemoveCompositeFieldCommand = new RelayCommand<CompositeFieldDraft>(RemoveCompositeField);
        AddGoodBinCommand = new RelayCommand(AddGoodBin);
        RemoveGoodBinCommand = new RelayCommand<string>(RemoveGoodBin);
        AddNonRetestFailBinCommand = new RelayCommand(AddNonRetestFailBin);
        RemoveNonRetestFailBinCommand = new RelayCommand<string>(RemoveNonRetestFailBin);
        AddMissingMarkerCommand = new RelayCommand(AddMissingMarker);
        AddPresetMissingMarkerCommand = new RelayCommand<string>(AddPresetMissingMarker);
        RemoveMissingMarkerCommand = new RelayCommand<string>(RemoveMissingMarker);

        if (request.ExistingProfile is { } existing)
        {
            LoadFromProfile(existing);
        }
        else
        {
            InitializeNewDraft();
        }

        Preview = new ImportProfileSamplePreviewViewModel(this, previewService, previewUiDispatch, previewDebounce);
        SubscribeDraftChanges();
    }

    public ImportProfileSamplePreviewViewModel Preview { get; }

    public ImportProfileEditorMode Mode { get; }

    public string WindowTitle => Mode == ImportProfileEditorMode.Edit
        ? "Edit FT Import Profile"
        : "New FT Import Profile";

    public Guid ProfileId { get; }

    public string ProfileIdText => ProfileId.ToString("D");

    public int SchemaVersion { get; }

    public string SchemaVersionText => $"v{SchemaVersion}";

    public string ValidationScopeNote =>
        "Configuration validation checks rule consistency. Sample validation inspects a bounded sample only (not a full-file guarantee).";

    public string MissingPolicyNote =>
        "The selected policy applies to missing measurement cells in present columns. Columns absent from a source file always remain missing (never zero-filled).";

    public ImportProfile? SavedProfile { get; private set; }

    // ---- 1. General ----

    [ObservableProperty]
    public partial string ProfileName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string NewExtensionText { get; set; } = string.Empty;

    public ObservableCollection<string> AcceptedExtensions { get; } = [];

    // ---- 2. Filename Rules ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStructuredMode))]
    [NotifyPropertyChangedFor(nameof(IsRegexMode))]
    public partial FilenameRuleMode FilenameMode { get; set; } = FilenameRuleMode.Structured;

    public bool IsStructuredMode => FilenameMode == FilenameRuleMode.Structured;

    public bool IsRegexMode => FilenameMode == FilenameRuleMode.Regex;

    // Each character in this string is a segment delimiter (e.g. "_" or "._").
    [ObservableProperty]
    public partial string SegmentDelimitersText { get; set; } = "_";

    public FilenameFieldDraft LotIdField { get; } = new("Lot ID", isRequired: true);

    public FilenameFieldDraft TestStageField { get; } = new("Test Stage", isRequired: true);

    public FilenameFieldDraft RetestRoleField { get; } = new("Retest Role Field (Optional)", isRequired: false);

    [ObservableProperty]
    public partial string OriginalToken { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RetestMarker { get; set; } = string.Empty;

    public FilenameFieldDraft RetestRoundField { get; } = new("Retest Round Field (Optional)", isRequired: false);

    public FilenameFieldDraft FilePartField { get; } = new("File Part / Sequence (Optional)", isRequired: false);

    [ObservableProperty]
    public partial string AdvancedRegexPattern { get; set; } = string.Empty;

    // ---- 3. Datalog Structure ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCustomDelimiter))]
    public partial DelimiterPreset DelimiterPreset { get; set; } = DelimiterPreset.Comma;

    [ObservableProperty]
    public partial string CustomDelimiter { get; set; } = string.Empty;

    public bool ShowCustomDelimiter => DelimiterPreset == DelimiterPreset.Custom;

    public RowIdentifierDraft HeaderRow { get; } = new("Header Row (Required)", isRequired: true);

    public RowIdentifierDraft DataStartRow { get; } = new("Data Start Row (Required)", isRequired: true);

    public RowIdentifierDraft UnitRow { get; } = new("Unit Row (Optional)", isRequired: false);

    public RowIdentifierDraft TargetRow { get; } = new("Target Row (Optional)", isRequired: false);

    public RowIdentifierDraft LslRow { get; } = new("LSL Row (Optional)", isRequired: false);

    public RowIdentifierDraft UslRow { get; } = new("USL Row (Optional)", isRequired: false);

    // ---- 4. Column & Composite Rules ----

    public ColumnSelectorDraft BinColumn { get; } = new("Bin Column (Required)", isRequired: true);

    public ColumnSelectorDraft SiteColumn { get; } = new("Site Column (Optional)", isRequired: false);

    public ObservableCollection<ColumnSelectorDraft> IgnoredColumns { get; } = [];

    public ObservableCollection<CompositeFieldDraft> CompositeFields { get; } = [];

    // ---- 5. Bin & Missing Rules ----

    [ObservableProperty]
    public partial string NewGoodBinText { get; set; } = string.Empty;

    public ObservableCollection<string> GoodBins { get; } = [];

    [ObservableProperty]
    public partial string NewNonRetestFailBinText { get; set; } = string.Empty;

    public ObservableCollection<string> NonRetestFailBins { get; } = [];

    // Explicit checkbox so the blank ("") missing marker is never silently discarded by list editing.
    [ObservableProperty]
    public partial bool IncludeBlankMissingMarker { get; set; } = true;

    [ObservableProperty]
    public partial string NewMissingMarkerText { get; set; } = string.Empty;

    public ObservableCollection<string> CustomMissingMarkers { get; } = [];

    [ObservableProperty]
    public partial MissingValuePolicy MissingPolicy { get; set; } = MissingValuePolicy.UseMissing;

    // ---- Validation State ----

    public ObservableCollection<ImportProfileValidationError> ValidationErrors { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasValidationErrors))]
    public partial string? ValidationSummary { get; private set; }

    [ObservableProperty]
    public partial string? GeneralErrors { get; private set; }

    [ObservableProperty]
    public partial string? FilenameErrors { get; private set; }

    [ObservableProperty]
    public partial string? StructureErrors { get; private set; }

    [ObservableProperty]
    public partial string? ColumnsErrors { get; private set; }

    [ObservableProperty]
    public partial string? BinMissingErrors { get; private set; }

    public bool HasValidationErrors => ValidationErrors.Count > 0;

    // ---- Commands ----

    public IRelayCommand AddExtensionCommand { get; }
    public IRelayCommand<string> RemoveExtensionCommand { get; }
    public IRelayCommand AddIgnoredColumnCommand { get; }
    public IRelayCommand<ColumnSelectorDraft> RemoveIgnoredColumnCommand { get; }
    public IRelayCommand AddCompositeFieldCommand { get; }
    public IRelayCommand<CompositeFieldDraft> RemoveCompositeFieldCommand { get; }
    public IRelayCommand AddGoodBinCommand { get; }
    public IRelayCommand<string> RemoveGoodBinCommand { get; }
    public IRelayCommand AddNonRetestFailBinCommand { get; }
    public IRelayCommand<string> RemoveNonRetestFailBinCommand { get; }
    public IRelayCommand AddMissingMarkerCommand { get; }
    public IRelayCommand<string> AddPresetMissingMarkerCommand { get; }
    public IRelayCommand<string> RemoveMissingMarkerCommand { get; }

    public ImportProfile BuildProfile()
    {
        var delimiters = SegmentDelimitersText
            .Distinct()
            .ToArray();

        var missingMarkers = new List<string>();
        if (IncludeBlankMissingMarker)
        {
            missingMarkers.Add(string.Empty);
        }

        foreach (var marker in CustomMissingMarkers)
        {
            if (marker.Length > 0 && !missingMarkers.Contains(marker, StringComparer.Ordinal))
            {
                missingMarkers.Add(marker);
            }
        }

        return new ImportProfile
        {
            Id = ProfileId,
            Name = ProfileName,
            SchemaVersion = SchemaVersion,
            FilenameRules = new FilenameRuleSet
            {
                AcceptedExtensions = AcceptedExtensions.ToArray(),
                Mode = FilenameMode,
                SegmentDelimiters = delimiters,
                LotId = LotIdField.ToModel()!,
                TestStage = TestStageField.ToModel()!,
                Retest = new FilenameRetestRule
                {
                    RoleField = RetestRoleField.ToModel(),
                    OriginalToken = string.IsNullOrEmpty(OriginalToken) ? null : OriginalToken,
                    RetestMarker = string.IsNullOrEmpty(RetestMarker) ? null : RetestMarker,
                    RetestRoundField = RetestRoundField.ToModel()
                },
                FilePart = FilePartField.ToModel(),
                AdvancedRegexPattern = string.IsNullOrWhiteSpace(AdvancedRegexPattern) ? null : AdvancedRegexPattern
            },
            StructureRules = new DatalogStructureRuleSet
            {
                Delimiter = ResolveDelimiter(),
                CompositeFields = CompositeFields.Select(c => c.ToModel()).ToArray(),
                HeaderRow = HeaderRow.ToModel()!,
                UnitRow = UnitRow.ToModel(),
                TargetRow = TargetRow.ToModel(),
                LslRow = LslRow.ToModel(),
                UslRow = UslRow.ToModel(),
                DataStartRow = DataStartRow.ToModel()!,
                BinColumn = BinColumn.ToModel()!,
                SiteColumn = SiteColumn.ToModel(),
                IgnoredColumns = IgnoredColumns.Select(c => c.ToModel()!).ToArray()
            },
            BinRules = new BinRuleSet
            {
                GoodBins = GoodBins.ToArray(),
                NonRetestFailBins = NonRetestFailBins.ToArray()
            },
            MissingValueRules = new MissingValueRuleSet
            {
                Markers = missingMarkers,
                Policy = MissingPolicy
            }
        };
    }

    public ImportProfileValidationResult Validate()
    {
        var profile = BuildProfile();
        var result = _validator.Validate(profile);

        ValidationErrors.Clear();
        foreach (var err in result.Errors)
        {
            ValidationErrors.Add(err);
        }

        if (result.IsValid)
        {
            ValidationSummary = null;
            GeneralErrors = null;
            FilenameErrors = null;
            StructureErrors = null;
            ColumnsErrors = null;
            BinMissingErrors = null;
        }
        else
        {
            ValidationSummary = $"Configuration has {result.Errors.Count} validation issue(s): {result.Errors[0].Message}";
            GeneralErrors = JoinErrors(result.Errors, e =>
                e.Code is ImportProfileValidationCode.InvalidIdentity or ImportProfileValidationCode.UnsupportedSchemaVersion
                || e.Field == nameof(FilenameRuleSet.AcceptedExtensions));
            FilenameErrors = JoinErrors(result.Errors, e =>
                e.Code == ImportProfileValidationCode.InvalidFilenameRules && e.Field != nameof(FilenameRuleSet.AcceptedExtensions));
            StructureErrors = JoinErrors(result.Errors, e =>
                e.Code is ImportProfileValidationCode.InvalidStructureRules or ImportProfileValidationCode.ConflictingRowRules);
            ColumnsErrors = JoinErrors(result.Errors, e =>
                e.Code is ImportProfileValidationCode.InvalidColumnSelector or ImportProfileValidationCode.ConflictingColumnRules);
            BinMissingErrors = JoinErrors(result.Errors, e =>
                e.Code is ImportProfileValidationCode.InvalidBinRules or ImportProfileValidationCode.ConflictingBinRules or ImportProfileValidationCode.InvalidMissingValueRules);
        }

        OnPropertyChanged(nameof(HasValidationErrors));
        return result;
    }

    public bool TrySave()
    {
        var profile = BuildProfile();
        var result = Validate();
        if (!result.IsValid)
        {
            SavedProfile = null;
            return false;
        }

        SavedProfile = profile;
        return true;
    }

    internal void SetInputError(string message)
    {
        ValidationSummary = message;
        SavedProfile = null;
    }

    internal void RunSuppressed(Action action)
    {
        _suppressAutoRefresh = true;
        try
        {
            action();
        }
        finally
        {
            _suppressAutoRefresh = false;
        }
    }

    public void Dispose() => Preview.Dispose();

    private void SubscribeDraftChanges()
    {
        PropertyChanged += OnDraftPropertyChanged;
        AcceptedExtensions.CollectionChanged += OnDraftCollectionChanged;
        IgnoredColumns.CollectionChanged += OnIgnoredColumnsChanged;
        CompositeFields.CollectionChanged += OnCompositeFieldsChanged;
        GoodBins.CollectionChanged += OnDraftCollectionChanged;
        NonRetestFailBins.CollectionChanged += OnDraftCollectionChanged;
        CustomMissingMarkers.CollectionChanged += OnDraftCollectionChanged;

        foreach (var child in new INotifyPropertyChanged[]
        {
            LotIdField, TestStageField, RetestRoleField, RetestRoundField, FilePartField,
            HeaderRow, DataStartRow, UnitRow, TargetRow, LslRow, UslRow,
            BinColumn, SiteColumn
        })
        {
            child.PropertyChanged += OnDraftPropertyChanged;
        }

        foreach (var col in IgnoredColumns)
        {
            col.PropertyChanged += OnDraftPropertyChanged;
        }

        foreach (var comp in CompositeFields)
        {
            HookCompositeDraft(comp);
        }
    }

    private void HookCompositeDraft(CompositeFieldDraft comp)
    {
        comp.PropertyChanged += OnDraftPropertyChanged;
        comp.SourceColumn.PropertyChanged += OnDraftPropertyChanged;
        comp.OutputNames.CollectionChanged += OnDraftCollectionChanged;
    }

    private void OnIgnoredColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (ColumnSelectorDraft col in e.NewItems)
            {
                col.PropertyChanged += OnDraftPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (ColumnSelectorDraft col in e.OldItems)
            {
                col.PropertyChanged -= OnDraftPropertyChanged;
            }
        }

        TriggerPreviewRefresh();
    }

    private void OnCompositeFieldsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (CompositeFieldDraft comp in e.NewItems)
            {
                HookCompositeDraft(comp);
            }
        }

        TriggerPreviewRefresh();
    }

    private void OnDraftPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ValidationSummary) or nameof(HasValidationErrors)
            or nameof(GeneralErrors) or nameof(FilenameErrors) or nameof(StructureErrors)
            or nameof(ColumnsErrors) or nameof(BinMissingErrors)
            or nameof(NewExtensionText) or nameof(NewGoodBinText) or nameof(NewNonRetestFailBinText)
            or nameof(NewMissingMarkerText) or nameof(CompositeFieldDraft.NewOutputName))
        {
            return;
        }

        TriggerPreviewRefresh();
    }

    private void OnDraftCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => TriggerPreviewRefresh();

    private void TriggerPreviewRefresh()
    {
        if (_suppressAutoRefresh)
        {
            return;
        }

        Preview.RequestRefresh(immediate: false);
    }

    private void InitializeNewDraft()
    {
        ProfileName = string.Empty;
        AcceptedExtensions.Add(".csv");

        FilenameMode = FilenameRuleMode.Structured;
        SegmentDelimitersText = "_";
        LotIdField.Kind = FilenameFieldKind.WholeSegment;
        LotIdField.SegmentPosition = 1;
        TestStageField.Kind = FilenameFieldKind.WholeSegment;
        TestStageField.SegmentPosition = 2;
        RetestRoleField.IsEnabled = false;
        OriginalToken = string.Empty;
        RetestMarker = "R";
        RetestRoundField.IsEnabled = false;
        FilePartField.IsEnabled = false;
        AdvancedRegexPattern = string.Empty;

        DelimiterPreset = DelimiterPreset.Comma;
        HeaderRow.Kind = RowMatchKind.FixedRowIndex;
        HeaderRow.RowNumber = 1;
        DataStartRow.Kind = RowMatchKind.FixedRowIndex;
        DataStartRow.RowNumber = 2;
        UnitRow.IsEnabled = false;
        TargetRow.IsEnabled = false;
        LslRow.IsEnabled = false;
        UslRow.IsEnabled = false;

        BinColumn.Kind = ColumnSelectorKind.HeaderName;
        BinColumn.HeaderName = "BIN";
        SiteColumn.IsEnabled = false;

        GoodBins.Add("1");
        IncludeBlankMissingMarker = true;
        MissingPolicy = MissingValuePolicy.UseMissing;
    }

    private void LoadFromProfile(ImportProfile profile)
    {
        ProfileName = profile.Name;
        AcceptedExtensions.Clear();
        foreach (var ext in profile.FilenameRules.AcceptedExtensions)
        {
            AcceptedExtensions.Add(ext);
        }

        FilenameMode = profile.FilenameRules.Mode;
        SegmentDelimitersText = new string(profile.FilenameRules.SegmentDelimiters.ToArray());
        LotIdField.LoadFrom(profile.FilenameRules.LotId);
        TestStageField.LoadFrom(profile.FilenameRules.TestStage);
        RetestRoleField.LoadFrom(profile.FilenameRules.Retest.RoleField);
        OriginalToken = profile.FilenameRules.Retest.OriginalToken ?? string.Empty;
        RetestMarker = profile.FilenameRules.Retest.RetestMarker ?? string.Empty;
        RetestRoundField.LoadFrom(profile.FilenameRules.Retest.RetestRoundField);
        FilePartField.LoadFrom(profile.FilenameRules.FilePart);
        AdvancedRegexPattern = profile.FilenameRules.AdvancedRegexPattern ?? string.Empty;

        LoadDelimiter(profile.StructureRules.Delimiter);
        HeaderRow.LoadFrom(profile.StructureRules.HeaderRow);
        DataStartRow.LoadFrom(profile.StructureRules.DataStartRow);
        UnitRow.LoadFrom(profile.StructureRules.UnitRow);
        TargetRow.LoadFrom(profile.StructureRules.TargetRow);
        LslRow.LoadFrom(profile.StructureRules.LslRow);
        UslRow.LoadFrom(profile.StructureRules.UslRow);

        BinColumn.LoadFrom(profile.StructureRules.BinColumn);
        SiteColumn.LoadFrom(profile.StructureRules.SiteColumn);

        IgnoredColumns.Clear();
        foreach (var ignored in profile.StructureRules.IgnoredColumns)
        {
            var draft = new ColumnSelectorDraft("Ignored Column", isRequired: true);
            draft.LoadFrom(ignored);
            IgnoredColumns.Add(draft);
        }

        CompositeFields.Clear();
        foreach (var composite in profile.StructureRules.CompositeFields)
        {
            var draft = new CompositeFieldDraft();
            draft.LoadFrom(composite);
            CompositeFields.Add(draft);
        }

        GoodBins.Clear();
        foreach (var bin in profile.BinRules.GoodBins)
        {
            GoodBins.Add(bin);
        }

        NonRetestFailBins.Clear();
        foreach (var bin in profile.BinRules.NonRetestFailBins)
        {
            NonRetestFailBins.Add(bin);
        }

        IncludeBlankMissingMarker = profile.MissingValueRules.Markers.Contains(string.Empty, StringComparer.Ordinal);
        CustomMissingMarkers.Clear();
        foreach (var marker in profile.MissingValueRules.Markers)
        {
            if (marker.Length > 0)
            {
                CustomMissingMarkers.Add(marker);
            }
        }

        MissingPolicy = profile.MissingValueRules.Policy;
    }

    private void LoadDelimiter(string delimiter)
    {
        switch (delimiter)
        {
            case ",":
                DelimiterPreset = DelimiterPreset.Comma;
                CustomDelimiter = string.Empty;
                break;
            case "\t":
                DelimiterPreset = DelimiterPreset.Tab;
                CustomDelimiter = string.Empty;
                break;
            case ";":
                DelimiterPreset = DelimiterPreset.Semicolon;
                CustomDelimiter = string.Empty;
                break;
            default:
                DelimiterPreset = DelimiterPreset.Custom;
                CustomDelimiter = delimiter;
                break;
        }
    }

    private string ResolveDelimiter() => DelimiterPreset switch
    {
        DelimiterPreset.Comma => ",",
        DelimiterPreset.Tab => "\t",
        DelimiterPreset.Semicolon => ";",
        _ => CustomDelimiter
    };

    private static string? JoinErrors(
        IReadOnlyList<ImportProfileValidationError> errors,
        Func<ImportProfileValidationError, bool> predicate)
    {
        var matched = errors.Where(predicate).Select(e => e.Message).ToArray();
        return matched.Length == 0 ? null : string.Join(Environment.NewLine, matched);
    }

    private void AddExtension()
    {
        var value = NewExtensionText.Trim();
        if (value.Length == 0)
        {
            return;
        }

        if (!value.StartsWith('.'))
        {
            value = "." + value;
        }

        if (!AcceptedExtensions.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            AcceptedExtensions.Add(value);
        }

        NewExtensionText = string.Empty;
    }

    private void RemoveExtension(string? ext)
    {
        if (ext is not null)
        {
            AcceptedExtensions.Remove(ext);
        }
    }

    private void AddIgnoredColumn()
    {
        IgnoredColumns.Add(new ColumnSelectorDraft("Ignored Column", isRequired: true));
    }

    private void RemoveIgnoredColumn(ColumnSelectorDraft? draft)
    {
        if (draft is not null)
        {
            IgnoredColumns.Remove(draft);
        }
    }

    private void AddCompositeField()
    {
        CompositeFields.Add(new CompositeFieldDraft());
    }

    private void RemoveCompositeField(CompositeFieldDraft? draft)
    {
        if (draft is not null)
        {
            CompositeFields.Remove(draft);
        }
    }

    private void AddGoodBin()
    {
        // Preserve exact string including leading zeros ("1", "01", "001" remain distinct).
        var value = NewGoodBinText;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!GoodBins.Contains(value, StringComparer.Ordinal))
        {
            GoodBins.Add(value);
        }

        NewGoodBinText = string.Empty;
    }

    private void RemoveGoodBin(string? bin)
    {
        if (bin is not null)
        {
            GoodBins.Remove(bin);
        }
    }

    private void AddNonRetestFailBin()
    {
        var value = NewNonRetestFailBinText;
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!NonRetestFailBins.Contains(value, StringComparer.Ordinal))
        {
            NonRetestFailBins.Add(value);
        }

        NewNonRetestFailBinText = string.Empty;
    }

    private void RemoveNonRetestFailBin(string? bin)
    {
        if (bin is not null)
        {
            NonRetestFailBins.Remove(bin);
        }
    }

    private void AddMissingMarker()
    {
        var value = NewMissingMarkerText;
        if (value.Length == 0)
        {
            IncludeBlankMissingMarker = true;
            return;
        }

        if (!CustomMissingMarkers.Contains(value, StringComparer.Ordinal))
        {
            CustomMissingMarkers.Add(value);
        }

        NewMissingMarkerText = string.Empty;
    }

    private void AddPresetMissingMarker(string? marker)
    {
        if (string.IsNullOrEmpty(marker))
        {
            IncludeBlankMissingMarker = true;
            return;
        }

        if (!CustomMissingMarkers.Contains(marker, StringComparer.Ordinal))
        {
            CustomMissingMarkers.Add(marker);
        }
    }

    private void RemoveMissingMarker(string? marker)
    {
        if (marker is not null)
        {
            CustomMissingMarkers.Remove(marker);
        }
    }
}
