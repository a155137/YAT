using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.Domain.Entities;

namespace YAT.app.ViewModels;

// One column a capability analysis may measure, whether the user picked it, and the specification limits they typed
// for it. The limits are text here: what the user is typing is not a number until it parses, and an empty side is a
// one-sided specification, not a zero.
public sealed partial class CapabilityVariableViewModel : ObservableObject
{
    internal CapabilityVariableViewModel(Guid worksheetColumnId, string name, string dataTypeName)
    {
        WorksheetColumnId = worksheetColumnId;
        Name = name;
        DataTypeName = dataTypeName;
    }

    public Guid WorksheetColumnId { get; }

    public string Name { get; }

    public string DataTypeName { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string LowerSpecificationLimitText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string UpperSpecificationLimitText { get; set; } = string.Empty;
}

// One statistic the result table may show, and whether it is shown. Switching one off never stops it being computed.
public sealed partial class CapabilityStatisticViewModel : ObservableObject
{
    internal CapabilityStatisticViewModel(CapabilityStatistic statistic, bool isSelected)
    {
        Statistic = statistic;
        IsSelected = isSelected;
    }

    public CapabilityStatistic Statistic { get; }

    public string DisplayName => CapabilityAnalysisBuilder.Name(Statistic);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

// The capability setup dialog's state: the worksheet's numeric columns with their own specification limits, the
// optional grouping column, and the statistics the result should show. Metadata only: no worksheet values are read.
public sealed partial class CapabilityAnalysisSetupViewModel : ViewModelBase
{
    private static readonly CapabilityAnalysisValidator Validator = new();

    private readonly IReadOnlyList<WorksheetColumn> _columns;

    public CapabilityAnalysisSetupViewModel(string title, Worksheet worksheet, IReadOnlyList<WorksheetColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(columns);

        _columns = columns;
        Title = title;
        WorksheetId = worksheet.Id;
        WorksheetName = worksheet.Name;

        // Each role offers the columns whose data type it accepts, and the rule lives in the Application layer.
        Variables =
        [
            .. columns
                .Where(column => AnalysisColumnRoles.Allows(AnalysisColumnRole.Variable, column.DataType))
                .Select(column => new CapabilityVariableViewModel(column.Id, column.Name, column.DataType.ToString()))
        ];

        GroupOptions =
        [
            AnalysisColumnOption.None,
            .. columns
                .Where(column => AnalysisColumnRoles.Allows(AnalysisColumnRole.Group, column.DataType))
                .Select(column => new AnalysisColumnOption(column.Id, column.Name, column.DataType.ToString()))
        ];

        Statistics =
        [
            .. CapabilityAnalysisConfiguration.SelectableStatistics.Select(statistic => new CapabilityStatisticViewModel(
                statistic,
                CapabilityAnalysisConfiguration.DefaultDisplayStatistics.Contains(statistic)))
        ];

        foreach (var variable in Variables)
        {
            variable.PropertyChanged += (_, _) => OnSelectionChanged();
        }

        foreach (var statistic in Statistics)
        {
            statistic.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CapabilityStatisticViewModel.IsSelected))
                {
                    OnSelectionChanged();
                }
            };
        }

        // Nothing is guessed: the user picks the variables and their limits, and grouping starts at "(None)".
        SelectedGroup = AnalysisColumnOption.None;
        OnSelectionChanged();
    }

    public string Title { get; }

    public Guid WorksheetId { get; }

    public string WorksheetName { get; }

    // The worksheet's numeric columns, in worksheet order; the selected ones become the analysis variables in that
    // same order, whatever order they were picked in.
    public IReadOnlyList<CapabilityVariableViewModel> Variables { get; }

    // "(None)" first, then the columns that may group: Numeric or String.
    public IReadOnlyList<AnalysisColumnOption> GroupOptions { get; }

    // Every statistic a capability result can show, with N, Mean and Within StDev switched on to begin with.
    public IReadOnlyList<CapabilityStatisticViewModel> Statistics { get; }

    [ObservableProperty]
    public partial AnalysisColumnOption? SelectedGroup { get; set; }

    // False while the selection, the specifications or the display choices cannot produce a result.
    [ObservableProperty]
    public partial bool CanConfirm { get; private set; }

    // Why the current settings cannot be confirmed; null when they can.
    [ObservableProperty]
    public partial string? ValidationMessage { get; private set; }

    public IReadOnlyList<CapabilityVariableViewModel> SelectedVariables => [.. Variables.Where(variable => variable.IsSelected)];

    // The configuration of the current settings, or null when they are not valid (ValidationMessage says why).
    public CapabilityAnalysisConfiguration? Confirm()
    {
        var (configuration, errors) = Build();
        ValidationMessage = AnalysisValidationMessages.For(errors, ColumnName);
        CanConfirm = errors.Count == 0;
        return errors.Count == 0 ? configuration : null;
    }

    partial void OnSelectedGroupChanged(AnalysisColumnOption? value) => OnSelectionChanged();

    // The configuration the current settings describe, with everything that is wrong with it: the specification limits
    // the user typed are parsed here, and text that is not a number is reported for that variable and that side.
    private (CapabilityAnalysisConfiguration Configuration, IReadOnlyList<AnalysisValidationError> Errors) Build()
    {
        var errors = new List<AnalysisValidationError>();
        var variables = new List<CapabilityVariable>();

        foreach (var variable in Variables.Where(candidate => candidate.IsSelected))
        {
            var lower = Limit(errors, variable, variable.LowerSpecificationLimitText, AnalysisSpecificationField.LowerSpecificationLimit);
            var upper = Limit(errors, variable, variable.UpperSpecificationLimitText, AnalysisSpecificationField.UpperSpecificationLimit);
            variables.Add(new CapabilityVariable(variable.WorksheetColumnId, lower, upper));
        }

        var configuration = new CapabilityAnalysisConfiguration(
            WorksheetId,
            variables,
            SelectedGroup?.WorksheetColumnId,
            [.. Statistics.Where(statistic => statistic.IsSelected).Select(statistic => statistic.Statistic)]);

        errors.AddRange(Validator.Validate(configuration, _columns).Errors);
        return (configuration, errors);
    }

    private static double? Limit(
        List<AnalysisValidationError> errors,
        CapabilityVariableViewModel variable,
        string text,
        AnalysisSpecificationField field)
    {
        if (SpecificationLimitParser.TryParse(text, out var limit))
        {
            return limit;
        }

        errors.Add(new AnalysisValidationError(
            AnalysisValidationReason.SpecificationLimitNotNumeric, AnalysisColumnRole.Variable, variable.WorksheetColumnId, field));
        return null;
    }

    private void OnSelectionChanged()
    {
        CanConfirm = Build().Errors.Count == 0;
        ValidationMessage = null;
    }

    private string? ColumnName(Guid columnId) =>
        _columns.FirstOrDefault(column => column.Id == columnId)?.Name;
}
