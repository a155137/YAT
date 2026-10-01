using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.app.ViewModels;

// One value of the filter column, offered with a check box. The value keeps the column's own type; Label is only how it
// is shown, so two values that would print alike are still two choices.
public sealed partial class GraphFilterValueOption : ObservableObject
{
    internal GraphFilterValueOption(double number)
    {
        Number = number;
        Label = GraphFilterEditorViewModel.Format(number);
    }

    internal GraphFilterValueOption(string text)
    {
        Text = text;
        Label = GraphFilterEditorViewModel.Format(text);
    }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    internal double Number { get; }

    // Null for a value of a Numeric column.
    internal string? Text { get; }
}

// What the Filter dialog confirmed: the filter the graph is to use - null for every row. A cancelled dialog returns no
// GraphFilterEdit at all, so "cancelled" and "every row" are never confused.
public sealed record GraphFilterEdit(GraphValueFilter? Filter);

// The graph setup's Filter dialog (Task #049): which column a graph's rows are selected by, and which of its values -
// typed values, as the column has them, and Missing on its own. Metadata and the column's distinct values only: no
// worksheet rows pass through here.
//
// The values are read off the UI thread whenever the column changes, at most GraphValueFilter.MaximumDistinctValues of
// them; a read still running for a column the user has left is cancelled and its result ignored. A column with more
// values than that is no category: the dialog says so and cannot be confirmed with it - its values are never offered
// as if they were all of them.
//
// A new column starts with every value and Missing selected, which is every row. Editing a filter shows its own column
// with its own values and Missing selected, once. Apply gives the canonical filter: every value (and Missing, where the
// column has it) selected is no filter at all.
public sealed partial class GraphFilterEditorViewModel : ObservableObject
{
    public const string MissingLabel = "(Missing)";

    private readonly Func<Guid, CancellationToken, Task<GraphFilterValues>> _loadValues;
    private GraphValueFilter? _restore;
    private GraphFilterValues? _loaded;
    private CancellationTokenSource? _loading;
    private bool _updatingSelection;

    // columns: the worksheet's columns (only the Numeric and String ones are offered). current: the filter being edited,
    // or null. groupColumnId: the graph's group column, the first column offered when there is no filter yet.
    public GraphFilterEditorViewModel(
        IReadOnlyList<WorksheetColumn> columns,
        GraphValueFilter? current,
        Guid? groupColumnId,
        Func<Guid, CancellationToken, Task<GraphFilterValues>> loadValues)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(loadValues);

        _loadValues = loadValues;
        Columns =
        [
            .. columns
                .Where(column => column.DataType is WorksheetDataType.Numeric or WorksheetDataType.String)
                .OrderBy(column => column.Index)
                .Select(column => new GraphColumnOption(column.Id, column.Name, column.DataType.ToString()))
        ];

        SelectAllCommand = new RelayCommand(SelectAll, () => Values.Count > 0 || HasMissingOption);
        ClearCommand = new RelayCommand(Clear, () => Values.Count > 0 || HasMissingOption);

        _restore = current;
        SelectedColumn = Find(current?.ColumnId) ?? Find(groupColumnId);
        if (SelectedColumn is null)
        {
            Refresh();
        }
    }

    // The columns a filter can use: every Numeric and String column of the worksheet, in worksheet order.
    public IReadOnlyList<GraphColumnOption> Columns { get; }

    [ObservableProperty]
    public partial GraphColumnOption? SelectedColumn { get; set; }

    // The column's values, in the order they first occur in the worksheet. Empty while they are read, and for a column
    // with too many of them.
    public ObservableCollection<GraphFilterValueOption> Values { get; } = [];

    // Whether the column has rows without a value, offered as (Missing).
    [ObservableProperty]
    public partial bool HasMissingOption { get; private set; }

    [ObservableProperty]
    public partial bool IncludeMissing { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    // The column has more distinct values than can be offered: no filter can be made with it.
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

    // The read of the current column's values, for whoever needs to wait for it (tests). Completed when there is none.
    public Task Loading { get; private set; } = Task.CompletedTask;

    public static string TooManyValuesMessage =>
        $"This column has more than {GraphValueFilter.MaximumDistinctValues.ToString("N0", CultureInfo.InvariantCulture)} " +
        "unique values: too many to filter by. Choose another column.";

    public const string ReadFailedMessage = "The values of this column could not be read.";

    // How a value is shown: a number in full (so two different numbers never look alike), text as it is - empty text
    // in quotes.
    public static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Format(string value) => value.Length == 0 ? "\"\"" : value;

    // The filter the dialog describes, canonical - null when it keeps every row - or false when there is nothing that
    // can be applied: no column, values still being read or not offered, or nothing selected.
    public bool TryApply(out GraphFilterEdit? edit)
    {
        edit = null;
        if (!CanApply || SelectedColumn?.WorksheetColumnId is not { } columnId || _loaded is not { } loaded
            || loaded.ColumnId != columnId)
        {
            return false;
        }

        GraphValueFilter filter = loaded.DataType == WorksheetDataType.Numeric
            ? new NumericValueFilter(columnId, [.. Values.Where(value => value.IsSelected).Select(value => value.Number)], IncludeMissing && HasMissingOption)
            : new TextValueFilter(columnId, [.. Values.Where(value => value.IsSelected).Select(value => value.Text!)], IncludeMissing && HasMissingOption);

        if (filter.IsEmpty)
        {
            return false;
        }

        edit = new GraphFilterEdit(GraphValueFilter.Canonicalize(filter, loaded));
        return true;
    }

    // Stops a read still running (the dialog is closing).
    public void Cancel()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    // A one-line description of a graph's filter for the setup: "All rows" without one, otherwise the column and the
    // values it keeps - the first few of them when there are many.
    public static string Describe(GraphValueFilter? filter, string? columnName)
    {
        if (filter is null)
        {
            return "All rows";
        }

        const int shown = 4;
        IReadOnlyList<string> labels = filter switch
        {
            NumericValueFilter numeric => [.. numeric.Values.Select(Format)],
            TextValueFilter text => [.. text.Values.Select(Format)],
            _ => []
        };

        var parts = labels.Take(shown).ToList();
        if (labels.Count > shown)
        {
            parts.Add($"+{labels.Count - shown} more");
        }

        if (filter.IncludeMissing)
        {
            parts.Add(MissingLabel);
        }

        return $"{columnName ?? "(column not available)"}: {string.Join(", ", parts)}";
    }

    partial void OnSelectedColumnChanged(GraphColumnOption? value) => Loading = LoadAsync(value);

    partial void OnIncludeMissingChanged(bool value) => Refresh();

    private GraphColumnOption? Find(Guid? columnId) =>
        columnId is { } id ? Columns.FirstOrDefault(column => column.WorksheetColumnId == id) : null;

    private async Task LoadAsync(GraphColumnOption? column)
    {
        Cancel();
        _loaded = null;
        ClearValues();
        HasMissingOption = false;
        HasTooManyValues = false;
        Message = null;

        if (column?.WorksheetColumnId is not { } columnId)
        {
            IsLoading = false;
            Refresh();
            return;
        }

        var cancellation = new CancellationTokenSource();
        _loading = cancellation;
        IsLoading = true;
        Refresh();

        GraphFilterValues values;
        try
        {
            values = await _loadValues(columnId, cancellation.Token);
        }
        catch (Exception)
        {
            // Cancelled, or failed after the user moved on: nothing to say. Failed for the column shown: say so.
            if (ReferenceEquals(_loading, cancellation) && !cancellation.IsCancellationRequested)
            {
                IsLoading = false;
                Message = ReadFailedMessage;
                Refresh();
            }

            return;
        }

        // A read for a column the user has already left, or of a closed dialog, changes nothing.
        if (!ReferenceEquals(_loading, cancellation) || cancellation.IsCancellationRequested)
        {
            return;
        }

        IsLoading = false;
        Show(values);
    }

    private void Show(GraphFilterValues values)
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

        // Editing a filter: its own values and Missing, once. A new column: everything, which is every row.
        var restore = _restore is { } filter && filter.ColumnId == values.ColumnId ? filter : null;
        _restore = null;

        _updatingSelection = true;
        try
        {
            IEnumerable<GraphFilterValueOption> options = values.DataType == WorksheetDataType.Numeric
                ? values.Numbers.Select(number => new GraphFilterValueOption(number)
                {
                    IsSelected = restore is NumericValueFilter numeric ? numeric.Contains(number) : restore is null
                })
                : values.Texts.Select(text => new GraphFilterValueOption(text)
                {
                    IsSelected = restore is TextValueFilter texts ? texts.Contains(text) : restore is null
                });

            foreach (var option in options)
            {
                option.PropertyChanged += OnValueChanged;
                Values.Add(option);
            }

            HasMissingOption = values.HasMissing;
            IncludeMissing = values.HasMissing && (restore?.IncludeMissing ?? true);
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

    private void ClearValues()
    {
        foreach (var value in Values)
        {
            value.PropertyChanged -= OnValueChanged;
        }

        Values.Clear();
    }

    private void OnValueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GraphFilterValueOption.IsSelected))
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

        if (SelectedColumn is null)
        {
            Summary = "Choose the column to select rows by.";
            CanApply = false;
        }
        else if (IsLoading)
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
            Summary = all
                ? "Every value selected: all rows."
                : $"{selected} of {Values.Count} values selected" + (missing ? ", and (Missing)." : ".");
            CanApply = true;
        }
    }
}
