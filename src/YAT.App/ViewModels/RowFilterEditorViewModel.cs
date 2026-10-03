using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Analyses;
using YAT.Application.Filtering;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.app.ViewModels;

// The operators a condition can use (Task #053). Which of them a column offers depends on its data type.
public enum RowFilterOperator
{
    IsAnyOf,
    IsNotAnyOf,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Between,
    Is,
    IsNot
}

// An operator as the Filter dialog offers it: "is any of", "=", "between", "is not", ...
public sealed record RowFilterOperatorOption(RowFilterOperator Operator, string Label)
{
    public override string ToString() => Label;
}

// What the Filter dialog confirmed: the filter the graph or analysis is to use - null for every row. A cancelled dialog
// returns no RowFilterEdit at all, so "cancelled" and "every row" are never confused.
public sealed record RowFilterEdit(RowFilter? Filter);

// One condition as the Filter dialog edits it (Task #053): its column, its operator and its value(s) - values chosen
// from the column's own for "is any of" / "is not any of", a number for a comparison, two for between, text for "is" /
// "is not". Error says what is wrong with it (null when it can be applied).
public sealed partial class RowFilterConditionViewModel : ObservableObject
{
    private static readonly IReadOnlyList<RowFilterOperatorOption> NumericOperators =
    [
        new(RowFilterOperator.IsAnyOf, "is any of"),
        new(RowFilterOperator.IsNotAnyOf, "is not any of"),
        new(RowFilterOperator.Equal, "="),
        new(RowFilterOperator.NotEqual, "!="),
        new(RowFilterOperator.Less, "<"),
        new(RowFilterOperator.LessOrEqual, "<="),
        new(RowFilterOperator.Greater, ">"),
        new(RowFilterOperator.GreaterOrEqual, ">="),
        new(RowFilterOperator.Between, "between")
    ];

    private static readonly IReadOnlyList<RowFilterOperatorOption> TextOperators =
    [
        new(RowFilterOperator.IsAnyOf, "is any of"),
        new(RowFilterOperator.IsNotAnyOf, "is not any of"),
        new(RowFilterOperator.Is, "is"),
        new(RowFilterOperator.IsNot, "is not")
    ];

    private readonly RowFilterEditorViewModel _editor;
    private readonly Func<Guid, CancellationToken, Task<FilterValues>> _loadValues;
    private ValueSetCondition? _values;
    private FilterValues? _available;

    internal RowFilterConditionViewModel(
        RowFilterEditorViewModel editor,
        IReadOnlyList<FilterColumnOption> columns,
        Func<Guid, CancellationToken, Task<FilterValues>> loadValues,
        FilterColumnOption? column)
    {
        _editor = editor;
        _loadValues = loadValues;
        Columns = columns;
        RemoveCommand = new RelayCommand(() => _editor.Remove(this));
        SelectedColumn = column;
    }

    // The condition as it was given, shown again for editing. A condition on a column the worksheet no longer has (or
    // not of its type) keeps no column and says so.
    internal RowFilterConditionViewModel(
        RowFilterEditorViewModel editor,
        IReadOnlyList<FilterColumnOption> columns,
        Func<Guid, CancellationToken, Task<FilterValues>> loadValues,
        RowFilterCondition condition)
        : this(editor, columns, loadValues, columns.FirstOrDefault(column => column.Id == condition.ColumnId && column.DataType == condition.DataType))
    {
        if (SelectedColumn is null)
        {
            ColumnUnavailable = true;
            Refresh();
            return;
        }

        switch (condition)
        {
            case ValueSetCondition set:
                SelectedOperator = Find(set.Exclude ? RowFilterOperator.IsNotAnyOf : RowFilterOperator.IsAnyOf);
                _values = set;
                break;
            case NumericComparisonCondition comparison:
                SelectedOperator = Find(comparison.Comparison switch
                {
                    NumericComparison.Equal => RowFilterOperator.Equal,
                    NumericComparison.NotEqual => RowFilterOperator.NotEqual,
                    NumericComparison.Less => RowFilterOperator.Less,
                    NumericComparison.LessOrEqual => RowFilterOperator.LessOrEqual,
                    NumericComparison.Greater => RowFilterOperator.Greater,
                    _ => RowFilterOperator.GreaterOrEqual
                });
                ValueText = TextOf(comparison.Value);
                break;
            case NumericBetweenCondition between:
                SelectedOperator = Find(RowFilterOperator.Between);
                ValueText = TextOf(between.Lower);
                UpperText = TextOf(between.Upper);
                break;
            case TextComparisonCondition text:
                SelectedOperator = Find(text.Negated ? RowFilterOperator.IsNot : RowFilterOperator.Is);
                ValueText = text.Value;
                break;
        }

        Refresh();
    }

    // The worksheet's Numeric and String columns, in worksheet order.
    public IReadOnlyList<FilterColumnOption> Columns { get; }

    [ObservableProperty]
    public partial FilterColumnOption? SelectedColumn { get; set; }

    // The operators the column offers.
    [ObservableProperty]
    public partial IReadOnlyList<RowFilterOperatorOption> Operators { get; private set; } = [];

    [ObservableProperty]
    public partial RowFilterOperatorOption? SelectedOperator { get; set; }

    // The number of a comparison, the lower number of between, or the text of "is" / "is not".
    [ObservableProperty]
    public partial string ValueText { get; set; } = string.Empty;

    // The upper number of between.
    [ObservableProperty]
    public partial string UpperText { get; set; } = string.Empty;

    // The values an "is any of" / "is not any of" selects, in a few words; or what to do.
    [ObservableProperty]
    public partial string ValuesSummary { get; private set; } = string.Empty;

    // Why the condition cannot be applied, or null.
    [ObservableProperty]
    public partial string? Error { get; private set; }

    // The condition was given for a column the worksheet no longer has.
    public bool ColumnUnavailable { get; private set; }

    public bool ShowsValues => SelectedOperator?.Operator is RowFilterOperator.IsAnyOf or RowFilterOperator.IsNotAnyOf;

    public bool ShowsValue => SelectedOperator is not null && !ShowsValues;

    public bool ShowsUpper => SelectedOperator?.Operator == RowFilterOperator.Between;

    public IRelayCommand RemoveCommand { get; }

    public bool IsValid => Error is null;

    // The value chooser for this condition's column ("Choose..."), or null when there is none to choose from.
    public FilterValueChooserViewModel? CreateValueChooser() =>
        SelectedColumn is { } column && ShowsValues
            ? new FilterValueChooserViewModel(column, _values, SelectedOperator!.Operator == RowFilterOperator.IsNotAnyOf, _loadValues)
            : null;

    // The values chosen in the chooser.
    public void ApplyValues(FilterValueChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        _values = choice.Condition;
        _available = choice.Available;
        Refresh();
    }

    // The condition, or null when it is not valid - or when it is an "is any of" known (from the column's complete
    // list of values) to keep every row, which is no condition at all. Only a complete list can tell: a truncated
    // list never makes a condition "every row".
    internal bool TryBuild(out RowFilterCondition? condition)
    {
        condition = null;
        if (Build() is not { } built)
        {
            return false;
        }

        condition = built is ValueSetCondition set && _available is { } available ? ValueSetCondition.Canonicalize(set, available) : built;
        return true;
    }

    partial void OnSelectedColumnChanged(FilterColumnOption? value)
    {
        var previous = SelectedOperator?.Operator;
        Operators = value?.DataType == WorksheetDataType.String ? TextOperators : value is null ? [] : NumericOperators;
        _values = null;
        _available = null;
        SelectedOperator = previous is { } kept && Operators.FirstOrDefault(option => option.Operator == kept) is { } same
            ? same
            : Operators.FirstOrDefault();
        Refresh();
    }

    partial void OnSelectedOperatorChanged(RowFilterOperatorOption? value)
    {
        // A value set keeps its values from "is any of" to "is not any of" and back; Missing and the values stay chosen.
        if (_values is not null && value?.Operator is RowFilterOperator.IsAnyOf or RowFilterOperator.IsNotAnyOf)
        {
            _values = WithExclude(_values, value.Operator == RowFilterOperator.IsNotAnyOf);
        }

        OnPropertyChanged(nameof(ShowsValues));
        OnPropertyChanged(nameof(ShowsValue));
        OnPropertyChanged(nameof(ShowsUpper));
        Refresh();
    }

    partial void OnValueTextChanged(string value) => Refresh();

    partial void OnUpperTextChanged(string value) => Refresh();

    partial void OnErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(IsValid));
        _editor.Refresh();
    }

    private RowFilterOperatorOption? Find(RowFilterOperator op) => Operators.FirstOrDefault(option => option.Operator == op);

    private RowFilterCondition? Build()
    {
        if (SelectedColumn is not { } column || SelectedOperator is not { } option)
        {
            return null;
        }

        switch (option.Operator)
        {
            case RowFilterOperator.IsAnyOf or RowFilterOperator.IsNotAnyOf:
                return _values is { IsEmpty: false } values && values.ColumnId == column.Id ? values : null;
            case RowFilterOperator.Between:
                return Parse(ValueText) is { } lower && Parse(UpperText) is { } upper && lower <= upper
                    ? new NumericBetweenCondition(column.Id, lower, upper)
                    : null;
            case RowFilterOperator.Is or RowFilterOperator.IsNot:
                return string.IsNullOrWhiteSpace(ValueText) ? null : new TextComparisonCondition(column.Id, ValueText, option.Operator == RowFilterOperator.IsNot);
            default:
                return Parse(ValueText) is { } number
                    ? new NumericComparisonCondition(column.Id, option.Operator switch
                    {
                        RowFilterOperator.Equal => NumericComparison.Equal,
                        RowFilterOperator.NotEqual => NumericComparison.NotEqual,
                        RowFilterOperator.Less => NumericComparison.Less,
                        RowFilterOperator.LessOrEqual => NumericComparison.LessOrEqual,
                        RowFilterOperator.Greater => NumericComparison.Greater,
                        _ => NumericComparison.GreaterOrEqual
                    }, number)
                    : null;
        }
    }

    // The error and the values' summary, after anything changed.
    private void Refresh()
    {
        ValuesSummary = _values is { } values ? FilterValueChooserViewModel.Describe(values) : "No values chosen";
        Error = SelectedColumn is null
            ? ColumnUnavailable ? "The column of this condition is no longer available." : "Choose a column."
            : SelectedOperator is not { } option ? "Choose an operator."
            : option.Operator switch
            {
                RowFilterOperator.IsAnyOf or RowFilterOperator.IsNotAnyOf when _values is not { IsEmpty: false } => "Choose the values.",
                RowFilterOperator.Between when Parse(ValueText) is null || Parse(UpperText) is null => "Enter two numbers.",
                RowFilterOperator.Between when Parse(ValueText) > Parse(UpperText) => "The lower number must not be above the upper number.",
                RowFilterOperator.Is or RowFilterOperator.IsNot when string.IsNullOrWhiteSpace(ValueText) => "Enter the text to compare with.",
                RowFilterOperator.Equal or RowFilterOperator.NotEqual or RowFilterOperator.Less or RowFilterOperator.LessOrEqual
                    or RowFilterOperator.Greater or RowFilterOperator.GreaterOrEqual when Parse(ValueText) is null => "Enter a number.",
                _ => null
            };
    }

    private static ValueSetCondition WithExclude(ValueSetCondition values, bool exclude) => values switch
    {
        NumericValueSetCondition numeric => new NumericValueSetCondition(numeric.ColumnId, numeric.Values, numeric.IncludeMissing, exclude),
        TextValueSetCondition text => new TextValueSetCondition(text.ColumnId, text.Values, text.IncludeMissing, exclude),
        _ => values
    };

    // A typed number, read as everywhere in YAT (invariant culture, finite); blank and not-a-number are null.
    private static double? Parse(string? text) => SpecificationLimitParser.TryParse(text, out var value) ? value : null;

    private static string TextOf(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}

// The Filter dialog (Task #053): the conditions a graph's or an analysis's rows must all meet - one row each, with its
// column, operator and value(s), and a Remove button - with Add condition, Clear, Cancel and Apply. At most
// RowFilter.MaximumConditions conditions. Apply is available only while every condition can be applied; it gives the
// filter - none at all without conditions. Graph, Descriptive Statistics and Capability Analysis setups share it.
public sealed partial class RowFilterEditorViewModel : ObservableObject
{
    private readonly Func<Guid, CancellationToken, Task<FilterValues>> _loadValues;
    private readonly Guid? _suggestedColumnId;

    // columns: the worksheet's columns (only the Numeric and String ones are offered). current: the filter being edited,
    // or null. suggestedColumnId: the column a new condition starts on (a graph's group column), when there is one.
    public RowFilterEditorViewModel(
        IReadOnlyList<WorksheetColumn> columns,
        RowFilter? current,
        Func<Guid, CancellationToken, Task<FilterValues>> loadValues,
        Guid? suggestedColumnId = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(loadValues);

        _loadValues = loadValues;
        _suggestedColumnId = suggestedColumnId;
        Columns =
        [
            .. columns
                .Where(column => column.DataType is WorksheetDataType.Numeric or WorksheetDataType.String)
                .OrderBy(column => column.Index)
                .Select(column => new FilterColumnOption(column.Id, column.Name, column.DataType))
        ];

        AddConditionCommand = new RelayCommand(AddCondition, () => Conditions.Count < RowFilter.MaximumConditions && Columns.Count > 0);
        ClearCommand = new RelayCommand(() => Conditions.Clear(), () => Conditions.Count > 0);
        Conditions.CollectionChanged += (_, _) => Refresh();

        foreach (var condition in current?.Conditions ?? [])
        {
            Conditions.Add(new RowFilterConditionViewModel(this, Columns, loadValues, condition));
        }

        Refresh();
    }

    public IReadOnlyList<FilterColumnOption> Columns { get; }

    public ObservableCollection<RowFilterConditionViewModel> Conditions { get; } = [];

    public IRelayCommand AddConditionCommand { get; }

    public IRelayCommand ClearCommand { get; }

    [ObservableProperty]
    public partial bool CanApply { get; private set; }

    // "All rows: no conditions.", "3 conditions, all of which a row must meet.", or why Apply is not available.
    [ObservableProperty]
    public partial string Summary { get; private set; } = string.Empty;

    // The filter the dialog describes - null without conditions - or false while a condition cannot be applied.
    public bool TryApply(out RowFilterEdit? edit)
    {
        edit = null;
        if (!CanApply)
        {
            return false;
        }

        var conditions = new List<RowFilterCondition>();
        foreach (var condition in Conditions)
        {
            if (!condition.TryBuild(out var built))
            {
                return false;
            }

            if (built is not null)
            {
                conditions.Add(built);
            }
        }

        edit = new RowFilterEdit(RowFilter.Of(conditions));
        return true;
    }

    // A filter in a few words for a setup: "All rows", "1 condition", "3 conditions".
    public static string Describe(RowFilter? filter) => RowFilter.Describe(filter);

    internal void Remove(RowFilterConditionViewModel condition) => Conditions.Remove(condition);

    internal void Refresh()
    {
        AddConditionCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();

        var count = Conditions.Count;
        CanApply = count <= RowFilter.MaximumConditions && Conditions.All(condition => condition.IsValid);
        Summary = count == 0
            ? "No conditions: all rows are used."
            : count > RowFilter.MaximumConditions
                ? $"At most {RowFilter.MaximumConditions} conditions."
                : Conditions.All(condition => condition.IsValid)
                    ? (count == 1 ? "1 condition." : $"{count} conditions, all of which a row must meet.")
                    : "Complete or remove the conditions marked.";
    }

    private void AddCondition()
    {
        if (Conditions.Count >= RowFilter.MaximumConditions || Columns.Count == 0)
        {
            return;
        }

        var column = Columns.FirstOrDefault(option => option.Id == _suggestedColumnId) ?? Columns[0];
        Conditions.Add(new RowFilterConditionViewModel(this, Columns, _loadValues, column));
    }
}
