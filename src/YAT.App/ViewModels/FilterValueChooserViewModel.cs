using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Filtering;
using YAT.Domain.Enums;

namespace YAT.app.ViewModels;

// A worksheet column a row filter condition can read: a Numeric or String column, shown with its data type.
public sealed record FilterColumnOption(Guid Id, string Name, WorksheetDataType DataType)
{
    public string DataTypeName => DataType.ToString();

    public override string ToString() => Name;
}

// One value of the column, offered with a check box. The value keeps the column's own type; Label is only how it is
// shown, so two values that would print alike are still two choices.
public sealed partial class FilterValueOption : ObservableObject
{
    internal FilterValueOption(double number)
    {
        Number = number;
        Label = FilterValueChooserViewModel.Format(number);
    }

    internal FilterValueOption(string text)
    {
        Text = text;
        Label = FilterValueChooserViewModel.Format(text);
    }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    internal double Number { get; }

    // Null for a value of a Numeric column.
    internal string? Text { get; }
}

// What the Choose Values dialog confirmed for one "is any of" / "is not any of" condition: its values, and the list of
// the column's values they were chosen from (which says whether that list was complete). A cancelled dialog returns no
// FilterValueChoice at all.
public sealed record FilterValueChoice(ValueSetCondition Condition, FilterValues Available);

// The values of one column a value-set condition selects (Task #049's value picker, a sub-dialog of the Filter dialog
// since #053): the column's distinct values - typed values, as the column has them - and Missing on its own. The column's
// distinct values only: no worksheet rows pass through here.
//
// The values are read off the UI thread when the chooser opens, at most ValueSetCondition.MaximumDistinctValues of them.
// A column with more values than that is no category: the chooser says so and cannot be confirmed - its values are never
// offered as if they were all of them, and selecting every value shown can never mean "every row".
//
// Editing a condition shows its own values (and Missing) selected. A new "is any of" starts with every value and Missing
// selected; a new "is not any of" with none.
public sealed partial class FilterValueChooserViewModel : ObservableObject
{
    public const string MissingLabel = "(Missing)";

    private readonly Func<Guid, CancellationToken, Task<FilterValues>> _loadValues;
    private readonly bool _exclude;
    private ValueSetCondition? _restore;
    private FilterValues? _loaded;
    private CancellationTokenSource? _loading;
    private bool _updatingSelection;

    // column: the condition's column. current: the condition being edited (of this column), or null. exclude: whether
    // the condition is "is not any of".
    public FilterValueChooserViewModel(
        FilterColumnOption column,
        ValueSetCondition? current,
        bool exclude,
        Func<Guid, CancellationToken, Task<FilterValues>> loadValues)
    {
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(loadValues);

        Column = column;
        _exclude = exclude;
        _loadValues = loadValues;
        _restore = current?.ColumnId == column.Id ? current : null;
        SelectAllCommand = new RelayCommand(SelectAll, () => Values.Count > 0 || HasMissingOption);
        ClearCommand = new RelayCommand(Clear, () => Values.Count > 0 || HasMissingOption);
        Loading = LoadAsync();
    }

    public FilterColumnOption Column { get; }

    // "Choose Values - Lot": the dialog's title.
    public string Title => $"Choose Values - {Column.Name}";

    // The column's values, in the order they first occur in the worksheet. Empty while they are read, and for a column
    // with too many of them.
    public ObservableCollection<FilterValueOption> Values { get; } = [];

    // Whether the column has rows without a value, offered as (Missing).
    [ObservableProperty]
    public partial bool HasMissingOption { get; private set; }

    [ObservableProperty]
    public partial bool IncludeMissing { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    // The column has more distinct values than can be offered: no value set can be chosen from it.
    [ObservableProperty]
    public partial bool HasTooManyValues { get; private set; }

    // Why the values are not offered - too many of them, or they could not be read - or null.
    [ObservableProperty]
    public partial string? Message { get; private set; }

    // What is selected, in a few words; or why nothing can be applied.
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanApply { get; private set; }

    public IRelayCommand SelectAllCommand { get; }

    public IRelayCommand ClearCommand { get; }

    // The read of the column's values, for whoever needs to wait for it (tests).
    public Task Loading { get; }

    public static string TooManyValuesMessage =>
        $"This column has more than {ValueSetCondition.MaximumDistinctValues.ToString("N0", CultureInfo.InvariantCulture)} " +
        "unique values: too many to choose from. Use a comparison instead.";

    public const string ReadFailedMessage = "The values of this column could not be read.";

    // How a value is shown: a number in full (so two different numbers never look alike), text as it is - empty text
    // in quotes.
    public static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Format(string value) => value.Length == 0 ? "\"\"" : value;

    // The values chosen, as a condition of the chooser's kind, with the list they were chosen from - or false when there
    // is nothing that can be applied: values still being read or not offered, or nothing selected.
    public bool TryApply(out FilterValueChoice? choice)
    {
        choice = null;
        if (!CanApply || _loaded is not { } loaded || loaded.ColumnId != Column.Id)
        {
            return false;
        }

        var missing = IncludeMissing && HasMissingOption;
        ValueSetCondition condition = loaded.DataType == WorksheetDataType.Numeric
            ? new NumericValueSetCondition(Column.Id, [.. Values.Where(value => value.IsSelected).Select(value => value.Number)], missing, _exclude)
            : new TextValueSetCondition(Column.Id, [.. Values.Where(value => value.IsSelected).Select(value => value.Text!)], missing, _exclude);

        if (condition.IsEmpty)
        {
            return false;
        }

        choice = new FilterValueChoice(condition, loaded);
        return true;
    }

    // Stops a read still running (the dialog is closing).
    public void Cancel()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    // A value set in a few words: the first few values it selects, and (Missing).
    public static string Describe(ValueSetCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        const int shown = 4;
        IReadOnlyList<string> labels = condition switch
        {
            NumericValueSetCondition numeric => [.. numeric.Values.Select(Format)],
            TextValueSetCondition text => [.. text.Values.Select(Format)],
            _ => []
        };

        var parts = labels.Take(shown).ToList();
        if (labels.Count > shown)
        {
            parts.Add($"+{labels.Count - shown} more");
        }

        if (condition.IncludeMissing)
        {
            parts.Add(MissingLabel);
        }

        return string.Join(", ", parts);
    }

    partial void OnIncludeMissingChanged(bool value) => Refresh();

    private async Task LoadAsync()
    {
        var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        IsLoading = true;
        Refresh();

        FilterValues values;
        try
        {
            values = await _loadValues(Column.Id, cancellation.Token);
        }
        catch (Exception)
        {
            // Cancelled (the dialog closed): nothing to say. Failed: say so.
            if (ReferenceEquals(_loading, cancellation) && !cancellation.IsCancellationRequested)
            {
                IsLoading = false;
                Message = ReadFailedMessage;
                Refresh();
            }

            return;
        }

        if (!ReferenceEquals(_loading, cancellation) || cancellation.IsCancellationRequested)
        {
            return;
        }

        IsLoading = false;
        Show(values);
    }

    private void Show(FilterValues values)
    {
        _loaded = values;
        if (values.HasMore)
        {
            HasTooManyValues = true;
            Message = TooManyValuesMessage;
            _restore = null;
            Refresh();
            return;
        }

        // Editing a condition: its own values and Missing. A new one: everything for "is any of" (every row), nothing
        // for "is not any of".
        var restore = _restore;
        _restore = null;
        var selectedWhenNew = !_exclude;

        _updatingSelection = true;
        try
        {
            IEnumerable<FilterValueOption> options = values.DataType == WorksheetDataType.Numeric
                ? values.Numbers.Select(number => new FilterValueOption(number)
                {
                    IsSelected = restore is NumericValueSetCondition numeric ? numeric.Contains(number) : selectedWhenNew
                })
                : values.Texts.Select(text => new FilterValueOption(text)
                {
                    IsSelected = restore is TextValueSetCondition texts ? texts.Contains(text) : selectedWhenNew
                });

            foreach (var option in options)
            {
                option.PropertyChanged += OnValueChanged;
                Values.Add(option);
            }

            HasMissingOption = values.HasMissing;
            IncludeMissing = values.HasMissing && (restore?.IncludeMissing ?? selectedWhenNew);
        }
        finally
        {
            _updatingSelection = false;
        }

        Refresh();
    }

    private void SelectAll() => SetAll(true);

    private void Clear() => SetAll(false);

    private void SetAll(bool selected)
    {
        _updatingSelection = true;
        try
        {
            foreach (var value in Values)
            {
                value.IsSelected = selected;
            }

            IncludeMissing = selected && HasMissingOption;
        }
        finally
        {
            _updatingSelection = false;
        }

        Refresh();
    }

    private void OnValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FilterValueOption.IsSelected))
        {
            Refresh();
        }
    }

    // The summary and whether Apply is available, after anything changed.
    private void Refresh()
    {
        if (_updatingSelection)
        {
            return;
        }

        SelectAllCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();

        var selected = Values.Count(value => value.IsSelected);
        var missing = IncludeMissing && HasMissingOption;

        if (IsLoading)
        {
            Summary = "Reading the values of this column...";
            CanApply = false;
        }
        else if (Message is not null || _loaded is null)
        {
            Summary = Message ?? string.Empty;
            CanApply = false;
        }
        else if (selected == 0 && !missing)
        {
            Summary = "Select at least one value, or (Missing).";
            CanApply = false;
        }
        else
        {
            var all = selected == Values.Count && (missing || !HasMissingOption);
            Summary = all && !_exclude
                ? "Every value selected: this condition keeps every row."
                : $"{selected} of {Values.Count} values selected" + (missing ? ", and (Missing)." : ".");
            CanApply = true;
        }
    }
}
