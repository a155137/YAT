using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.ImportProfiles;

namespace YAT.app.ViewModels;

// Editable UI draft for FilenameFieldRule.
// Uses 1-based user-facing positions (SegmentPosition, StartPosition) and converts explicitly to 0-based model indices.
public sealed partial class FilenameFieldDraft : ObservableObject
{
    public FilenameFieldDraft(string label, bool isRequired)
    {
        Label = label;
        IsRequired = isRequired;
        IsEnabled = isRequired;
    }

    public string Label { get; }

    public bool IsRequired { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSegmentPosition))]
    [NotifyPropertyChangedFor(nameof(ShowMarker))]
    [NotifyPropertyChangedFor(nameof(ShowFixedRange))]
    [NotifyPropertyChangedFor(nameof(ShowRegexCapture))]
    public partial FilenameFieldKind Kind { get; set; } = FilenameFieldKind.WholeSegment;

    // 1-based segment position in UI; maps to 0-based FilenameFieldRule.SegmentIndex (SegmentPosition - 1).
    [ObservableProperty]
    public partial int SegmentPosition { get; set; } = 1;

    [ObservableProperty]
    public partial string Marker { get; set; } = string.Empty;

    // 1-based marker occurrence in both UI and model.
    [ObservableProperty]
    public partial int MarkerOccurrence { get; set; } = 1;

    // 1-based character start position in UI; maps to 0-based FilenameFieldRule.StartIndex (StartPosition - 1).
    [ObservableProperty]
    public partial int StartPosition { get; set; } = 1;

    [ObservableProperty]
    public partial int Length { get; set; } = 1;

    [ObservableProperty]
    public partial string Pattern { get; set; } = string.Empty;

    public bool ShowSegmentPosition => Kind != FilenameFieldKind.RegexCapture;

    public bool ShowMarker => Kind is FilenameFieldKind.MarkerBefore or FilenameFieldKind.MarkerAfter;

    public bool ShowFixedRange => Kind == FilenameFieldKind.FixedRange;

    public bool ShowRegexCapture => Kind == FilenameFieldKind.RegexCapture;

    public void LoadFrom(FilenameFieldRule? rule)
    {
        if (rule is null)
        {
            IsEnabled = IsRequired;
            return;
        }

        IsEnabled = true;
        Kind = rule.Kind;
        SegmentPosition = rule.SegmentIndex >= 0 ? rule.SegmentIndex + 1 : 1;
        Marker = rule.Marker ?? string.Empty;
        MarkerOccurrence = rule.MarkerOccurrence >= 1 ? rule.MarkerOccurrence : 1;
        StartPosition = rule.StartIndex is >= 0 ? rule.StartIndex.Value + 1 : 1;
        Length = rule.Length is > 0 ? rule.Length.Value : 1;
        Pattern = rule.Pattern ?? string.Empty;
    }

    public FilenameFieldRule? ToModel()
    {
        if (!IsRequired && !IsEnabled)
        {
            return null;
        }

        return new FilenameFieldRule
        {
            Kind = Kind,
            SegmentIndex = Kind == FilenameFieldKind.RegexCapture ? -1 : SegmentPosition - 1,
            Marker = ShowMarker ? Marker : null,
            MarkerOccurrence = ShowMarker ? MarkerOccurrence : 1,
            StartIndex = ShowFixedRange ? StartPosition - 1 : null,
            Length = ShowFixedRange ? Length : null,
            Pattern = ShowRegexCapture ? Pattern : null
        };
    }
}

// Editable UI draft for RowIdentifier.
// RowNumber is 1-based in both UI and model. ColumnPosition is 1-based in UI when not searching any column (maps to 0-based ColumnIndex or -1).
public sealed partial class RowIdentifierDraft : ObservableObject
{
    public RowIdentifierDraft(string label, bool isRequired)
    {
        Label = label;
        IsRequired = isRequired;
        IsEnabled = isRequired;
    }

    public string Label { get; }

    public bool IsRequired { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFixedRow))]
    [NotifyPropertyChangedFor(nameof(ShowKeyword))]
    public partial RowMatchKind Kind { get; set; } = RowMatchKind.FixedRowIndex;

    // 1-based source row number.
    [ObservableProperty]
    public partial int RowNumber { get; set; } = 1;

    [ObservableProperty]
    public partial string Keyword { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowColumnPosition))]
    public partial bool SearchAnyColumn { get; set; }

    // 1-based column position in UI; maps to 0-based RowIdentifier.ColumnIndex (ColumnPosition - 1) when SearchAnyColumn is false.
    [ObservableProperty]
    public partial int ColumnPosition { get; set; } = 1;

    [ObservableProperty]
    public partial KeywordMatchMode MatchMode { get; set; } = KeywordMatchMode.Exact;

    public bool ShowFixedRow => Kind == RowMatchKind.FixedRowIndex;

    public bool ShowKeyword => Kind == RowMatchKind.KeywordMatch;

    public bool ShowColumnPosition => ShowKeyword && !SearchAnyColumn;

    public void LoadFrom(RowIdentifier? row)
    {
        if (row is null)
        {
            IsEnabled = IsRequired;
            return;
        }

        IsEnabled = true;
        Kind = row.Kind;
        RowNumber = row.RowNumber is > 0 ? row.RowNumber.Value : 1;
        Keyword = row.Keyword ?? string.Empty;
        SearchAnyColumn = row.ColumnIndex < 0;
        ColumnPosition = row.ColumnIndex >= 0 ? row.ColumnIndex + 1 : 1;
        MatchMode = row.MatchMode;
    }

    public RowIdentifier? ToModel()
    {
        if (!IsRequired && !IsEnabled)
        {
            return null;
        }

        return new RowIdentifier
        {
            Kind = Kind,
            RowNumber = ShowFixedRow ? RowNumber : null,
            Keyword = ShowKeyword ? Keyword : null,
            ColumnIndex = ShowKeyword ? (SearchAnyColumn ? -1 : ColumnPosition - 1) : 0,
            MatchMode = MatchMode
        };
    }
}

// Editable UI draft for ColumnSelector.
// Uses 1-based ColumnPosition in UI when Kind == Index; converts explicitly to 0-based ColumnSelector.ColumnIndex.
public sealed partial class ColumnSelectorDraft : ObservableObject
{
    public ColumnSelectorDraft(string label, bool isRequired = true)
    {
        Label = label;
        IsRequired = isRequired;
        IsEnabled = isRequired;
    }

    public string Label { get; }

    public bool IsRequired { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowHeaderName))]
    [NotifyPropertyChangedFor(nameof(ShowColumnPosition))]
    public partial ColumnSelectorKind Kind { get; set; } = ColumnSelectorKind.HeaderName;

    [ObservableProperty]
    public partial string HeaderName { get; set; } = string.Empty;

    // 1-based column position in UI; maps to 0-based ColumnSelector.ColumnIndex (ColumnPosition - 1).
    [ObservableProperty]
    public partial int ColumnPosition { get; set; } = 1;

    public bool ShowHeaderName => Kind == ColumnSelectorKind.HeaderName;

    public bool ShowColumnPosition => Kind == ColumnSelectorKind.Index;

    public void LoadFrom(ColumnSelector? selector)
    {
        if (selector is null)
        {
            IsEnabled = IsRequired;
            return;
        }

        IsEnabled = true;
        Kind = selector.Kind;
        HeaderName = selector.HeaderName ?? string.Empty;
        ColumnPosition = selector.ColumnIndex is >= 0 ? selector.ColumnIndex.Value + 1 : 1;
    }

    public ColumnSelector? ToModel()
    {
        if (!IsRequired && !IsEnabled)
        {
            return null;
        }

        return new ColumnSelector
        {
            Kind = Kind,
            HeaderName = ShowHeaderName ? HeaderName : null,
            ColumnIndex = ShowColumnPosition ? ColumnPosition - 1 : null
        };
    }
}

public sealed partial class CompositeOutputNameItem : ObservableObject
{
    public CompositeOutputNameItem(string name) => Name = name;

    [ObservableProperty]
    public partial string Name { get; set; }
}

// Editable UI draft for CompositeFieldRule preserving source selector, separator, and exact output name ordering.
public sealed partial class CompositeFieldDraft : ObservableObject
{
    public CompositeFieldDraft()
    {
        AddOutputNameCommand = new RelayCommand(AddOutputName);
        RemoveOutputNameCommand = new RelayCommand<CompositeOutputNameItem>(RemoveOutputName);
        MoveOutputUpCommand = new RelayCommand<CompositeOutputNameItem>(MoveOutputUp);
        MoveOutputDownCommand = new RelayCommand<CompositeOutputNameItem>(MoveOutputDown);
    }

    public ColumnSelectorDraft SourceColumn { get; } = new("Source Column", isRequired: true);

    [ObservableProperty]
    public partial string Separator { get; set; } = "/";

    [ObservableProperty]
    public partial string NewOutputName { get; set; } = string.Empty;

    public ObservableCollection<CompositeOutputNameItem> OutputNames { get; } = [];

    public IRelayCommand AddOutputNameCommand { get; }

    public IRelayCommand<CompositeOutputNameItem> RemoveOutputNameCommand { get; }

    public IRelayCommand<CompositeOutputNameItem> MoveOutputUpCommand { get; }

    public IRelayCommand<CompositeOutputNameItem> MoveOutputDownCommand { get; }

    public void LoadFrom(CompositeFieldRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        SourceColumn.LoadFrom(rule.Column);
        Separator = rule.Separator;
        OutputNames.Clear();
        foreach (var name in rule.OutputNames)
        {
            OutputNames.Add(new CompositeOutputNameItem(name));
        }
    }

    public CompositeFieldRule ToModel() => new(
        SourceColumn.ToModel()!,
        Separator,
        OutputNames.Select(o => o.Name).ToArray());

    private void AddOutputName()
    {
        if (string.IsNullOrWhiteSpace(NewOutputName))
        {
            return;
        }

        OutputNames.Add(new CompositeOutputNameItem(NewOutputName.Trim()));
        NewOutputName = string.Empty;
    }

    private void RemoveOutputName(CompositeOutputNameItem? item)
    {
        if (item is not null)
        {
            OutputNames.Remove(item);
        }
    }

    private void MoveOutputUp(CompositeOutputNameItem? item)
    {
        if (item is null)
        {
            return;
        }

        var idx = OutputNames.IndexOf(item);
        if (idx > 0)
        {
            OutputNames.Move(idx, idx - 1);
        }
    }

    private void MoveOutputDown(CompositeOutputNameItem? item)
    {
        if (item is null)
        {
            return;
        }

        var idx = OutputNames.IndexOf(item);
        if (idx >= 0 && idx < OutputNames.Count - 1)
        {
            OutputNames.Move(idx, idx + 1);
        }
    }
}
