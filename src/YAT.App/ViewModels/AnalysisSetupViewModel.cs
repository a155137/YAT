using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Analyses;
using YAT.Domain.Entities;

namespace YAT.app.ViewModels;

// One choice in the grouping list: a worksheet column, or "(None)" because grouping is optional.
public sealed record AnalysisColumnOption(Guid? WorksheetColumnId, string Name, string DataTypeName)
{
    public static readonly AnalysisColumnOption None = new(null, "(None)", string.Empty);

    public bool IsNone => WorksheetColumnId is null;

    public override string ToString() => Name;
}

// One column the analysis may summarise, and whether the user picked it. Several may be picked at once.
public sealed partial class AnalysisVariableViewModel : ObservableObject
{
    internal AnalysisVariableViewModel(Guid worksheetColumnId, string name, string dataTypeName)
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
}

// The analysis setup dialog's state: the worksheet's columns, the variables the user picked and the optional grouping
// column, and the configuration they produce. Metadata only: no worksheet values are read.
//
// It is not tied to descriptive statistics - the title is given - so an analysis that needs the same "variables plus
// an optional grouping column" setup reuses it.
public sealed partial class AnalysisSetupViewModel : ViewModelBase
{
    private static readonly AnalysisConfigurationValidator Validator = new();

    private readonly IReadOnlyList<WorksheetColumn> _columns;

    public AnalysisSetupViewModel(string title, Worksheet worksheet, IReadOnlyList<WorksheetColumn> columns)
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
                .Select(column => new AnalysisVariableViewModel(column.Id, column.Name, column.DataType.ToString()))
        ];

        GroupOptions =
        [
            AnalysisColumnOption.None,
            .. columns
                .Where(column => AnalysisColumnRoles.Allows(AnalysisColumnRole.Group, column.DataType))
                .Select(column => new AnalysisColumnOption(column.Id, column.Name, column.DataType.ToString()))
        ];

        foreach (var variable in Variables)
        {
            variable.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AnalysisVariableViewModel.IsSelected))
                {
                    OnSelectionChanged();
                }
            };
        }

        // Nothing is guessed: the user picks the variables, and grouping starts at "(None)".
        SelectedGroup = AnalysisColumnOption.None;
        OnSelectionChanged();
    }

    public string Title { get; }

    public Guid WorksheetId { get; }

    public string WorksheetName { get; }

    // The worksheet's numeric columns, in worksheet order; the selected ones become the analysis variables in that
    // same order, whatever order they were picked in.
    public IReadOnlyList<AnalysisVariableViewModel> Variables { get; }

    // "(None)" first, then the columns that may group: Numeric or String.
    public IReadOnlyList<AnalysisColumnOption> GroupOptions { get; }

    [ObservableProperty]
    public partial AnalysisColumnOption? SelectedGroup { get; set; }

    // False while no variable is selected or a selection is not valid.
    [ObservableProperty]
    public partial bool CanConfirm { get; private set; }

    // Why the current selection cannot be confirmed; null when it can.
    [ObservableProperty]
    public partial string? ValidationMessage { get; private set; }

    public IReadOnlyList<Guid> SelectedVariableColumnIds =>
        [.. Variables.Where(variable => variable.IsSelected).Select(variable => variable.WorksheetColumnId)];

    // The configuration of the current selection, or null when it is not valid (ValidationMessage says why).
    public AnalysisConfiguration? Confirm()
    {
        var configuration = BuildConfiguration();
        var result = Validator.Validate(configuration, _columns);
        ValidationMessage = AnalysisValidationMessages.For(result);
        CanConfirm = result.IsValid;
        return result.IsValid ? configuration : null;
    }

    partial void OnSelectedGroupChanged(AnalysisColumnOption? value) => OnSelectionChanged();

    private AnalysisConfiguration BuildConfiguration() =>
        new(WorksheetId, SelectedVariableColumnIds, SelectedGroup?.WorksheetColumnId);

    private void OnSelectionChanged()
    {
        var result = Validator.Validate(BuildConfiguration(), _columns);
        CanConfirm = result.IsValid;
        ValidationMessage = null;
    }
}
