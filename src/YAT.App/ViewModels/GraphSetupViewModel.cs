using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Graphs;
using YAT.Domain.Entities;

namespace YAT.app.ViewModels;

// One choice in a role's list: a worksheet column, or "(None)" for an optional role.
public sealed record GraphColumnOption(Guid? WorksheetColumnId, string Name, string DataTypeName)
{
    public static readonly GraphColumnOption None = new(null, "(None)", string.Empty);

    public bool IsNone => WorksheetColumnId is null;

    public override string ToString() => Name;
}

// One role of the graph being set up, with the columns that may be assigned to it.
public sealed partial class GraphRoleViewModel : ObservableObject
{
    internal GraphRoleViewModel(GraphRoleDefinition definition, IReadOnlyList<GraphColumnOption> options)
    {
        Definition = definition;
        Options = options;
    }

    internal GraphRoleDefinition Definition { get; }

    public GraphVariableRole Role => Definition.Role;

    public string DisplayName => Definition.DisplayName;

    public bool IsRequired => Definition.IsRequired;

    // Only columns whose data type the role allows; an optional role also offers "(None)" first.
    public IReadOnlyList<GraphColumnOption> Options { get; }

    [ObservableProperty]
    public partial GraphColumnOption? SelectedOption { get; set; }

    public Guid? SelectedColumnId => SelectedOption?.WorksheetColumnId;
}

// The graph setup dialog's state: the worksheet's columns, the roles of the chosen graph type, and the configuration
// that the assignments produce. Roles come from the graph specification, so no graph type is special-cased here.
// Metadata only: no worksheet values are read.
public sealed partial class GraphSetupViewModel : ViewModelBase
{
    private static readonly GraphConfigurationValidator Validator = new();

    private readonly GraphTypeDefinition _definition;
    private readonly IReadOnlyList<WorksheetColumn> _columns;

    public GraphSetupViewModel(GraphTypeDefinition definition, Worksheet worksheet, IReadOnlyList<WorksheetColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(worksheet);
        ArgumentNullException.ThrowIfNull(columns);

        _definition = definition;
        _columns = columns;
        WorksheetId = worksheet.Id;
        WorksheetName = worksheet.Name;
        AvailableColumns = [.. columns.Select(Option)];

        Roles =
        [
            .. definition.Roles.Select(role => new GraphRoleViewModel(
                role,
                [
                    .. role.IsRequired ? Array.Empty<GraphColumnOption>() : [GraphColumnOption.None],
                    .. columns.Where(column => role.Allows(column.DataType)).Select(Option)
                ]))
        ];

        foreach (var role in Roles)
        {
            // Required roles start unassigned: the user chooses, nothing is guessed.
            role.SelectedOption = role.IsRequired ? null : GraphColumnOption.None;
            role.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(GraphRoleViewModel.SelectedOption))
                {
                    OnSelectionChanged();
                }
            };
        }

        OnSelectionChanged();
    }

    public string Title => _definition.DisplayName;

    public GraphType GraphType => _definition.GraphType;

    public Guid WorksheetId { get; }

    public string WorksheetName { get; }

    // All columns of the worksheet, for the dialog's column list.
    public IReadOnlyList<GraphColumnOption> AvailableColumns { get; }

    public IReadOnlyList<GraphRoleViewModel> Roles { get; }

    // False while a required role is unassigned or an assignment is not valid.
    [ObservableProperty]
    public partial bool CanConfirm { get; private set; }

    // Why the current assignments cannot be confirmed; null when they can.
    [ObservableProperty]
    public partial string? ValidationMessage { get; private set; }

    // The configuration of the current assignments, or null when they are not valid (ValidationMessage says why).
    public GraphConfiguration? Confirm()
    {
        var configuration = BuildConfiguration();
        var result = Validator.Validate(configuration, _columns);
        ValidationMessage = GraphValidationMessages.For(result, _definition);
        CanConfirm = result.IsValid;
        return result.IsValid ? configuration : null;
    }

    private GraphConfiguration BuildConfiguration() =>
        new(_definition.GraphType, WorksheetId,
        [
            .. Roles.Where(role => role.SelectedColumnId is not null)
                .Select(role => new GraphColumnAssignment(role.Role, role.SelectedColumnId!.Value))
        ]);

    private void OnSelectionChanged()
    {
        var result = Validator.Validate(BuildConfiguration(), _columns);
        CanConfirm = result.IsValid;
        ValidationMessage = null;
    }

    private static GraphColumnOption Option(WorksheetColumn column) => new(column.Id, column.Name, column.DataType.ToString());
}
