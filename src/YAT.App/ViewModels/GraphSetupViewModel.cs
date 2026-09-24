using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Analyses;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.Domain.Entities;

namespace YAT.app.ViewModels;

// One entry of an option list in the setup: the value it stands for and the text the list shows.
public sealed record SetupChoice<T>(T Value, string Name)
{
    public override string ToString() => Name;
}

// One choice in a role's list: a worksheet column, or "(None)" for an optional role.
public sealed record GraphColumnOption(Guid? WorksheetColumnId, string Name, string DataTypeName)
{
    public static readonly GraphColumnOption None = new(null, "(None)", string.Empty);

    public bool IsNone => WorksheetColumnId is null;

    public override string ToString() => Name;
}

// One role of the graph being set up, with the columns that may be assigned to it.
//
// A role takes either one column (SelectedOption) or several (SelectedOptions); which of the two is the role's own
// business, not the graph type's, so the dialog reads AllowsMultiple and nothing else.
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

    // True when the role takes more than one column (a box plot's graph variables).
    public bool AllowsMultiple => Definition.AllowsMultiple;

    // Only columns whose data type the role allows; an optional role also offers "(None)" first.
    public IReadOnlyList<GraphColumnOption> Options { get; }

    [ObservableProperty]
    public partial GraphColumnOption? SelectedOption { get; set; }

    // The columns picked for a role that takes several. The order they were picked in is not kept: the configuration
    // reads them in the order the worksheet lists them (see SelectedColumnIds).
    public ObservableCollection<GraphColumnOption> SelectedOptions { get; } = [];

    public Guid? SelectedColumnId => SelectedOption?.WorksheetColumnId;

    // Every column this role is assigned, in worksheet order.
    public IReadOnlyList<Guid> SelectedColumnIds => AllowsMultiple
        ? [.. Options.Where(option => !option.IsNone && SelectedOptions.Contains(option))
            .Select(option => option.WorksheetColumnId!.Value)]
        : SelectedColumnId is { } columnId ? [columnId] : [];
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

            role.SelectedOptions.CollectionChanged += (_, _) => OnSelectionChanged();
        }

        // Statistics are shown unless the user turns them off (only offered where the graph type has the panel).
        ShowStatistics = GraphPresentationOptions.Default.ShowStatistics;

        // The fitted line is shown unless the user turns it off (only offered where the graph type draws one).
        ShowFittedLine = ProbabilityPlotOptions.Default.ShowFittedLine;

        // Several variables are drawn together unless the user asks for a graph of each.
        Layout = GraphVariableLayout.Together;

        // A specification starts empty: nothing is drawn until the user enters a value.
        LowerLimitText = string.Empty;
        TargetText = string.Empty;
        UpperLimitText = string.Empty;

        // A histogram starts as it always was: frequency over automatic bins, with nothing typed for the other modes,
        // and no normal fit.
        SelectedYScale = YScaleChoices[0];
        SelectedBinning = BinningChoices[0];
        BinCountText = string.Empty;
        BinWidthText = string.Empty;
        BinStartText = string.Empty;
        ShowNormalFit = HistogramOptions.Default.ShowNormalFit;

        // Every label starts as the graph type gives it, with nothing typed; whatever changes in the labels is checked
        // with the rest of the setup.
        Labels = new GraphLabelsEditorViewModel();
        Labels.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphLabelsEditorViewModel.Options))
            {
                OnSelectionChanged();
            }
        };

        // The legend starts as the graph type gives it, on the right.
        Legend = new GraphLegendEditorViewModel();

        // Every axis starts Auto, with nothing typed; the ranges are checked with the rest of the setup, too.
        Axes = new GraphAxesEditorViewModel(definition);
        Axes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphAxesEditorViewModel.Options))
            {
                OnSelectionChanged();
            }
        };

        OnSelectionChanged();
    }

    // What the histogram's bars can measure, in the order the setup offers them; the first is the default.
    public IReadOnlyList<SetupChoice<HistogramYScale>> YScaleChoices { get; } =
    [
        new(HistogramYScale.Frequency, "Frequency"),
        new(HistogramYScale.Percent, "Percent"),
        new(HistogramYScale.Density, "Density")
    ];

    // How the histogram's bins can be chosen; the first is the default.
    public IReadOnlyList<SetupChoice<HistogramBinningMode>> BinningChoices { get; } =
    [
        new(HistogramBinningMode.Auto, "Auto"),
        new(HistogramBinningMode.Count, "Number of bins"),
        new(HistogramBinningMode.WidthAndStart, "Bin width and start")
    ];

    // Whether the graph type can draw several variables together or each in a graph of its own; the setup offers the
    // choice only when it can.
    public bool SupportsVariableLayout => _definition.Supports(GraphCapability.VariableLayout);

    // How several variables are drawn: together in one graph (the default) or each in a graph of its own. It means
    // something only once two or more variables are selected.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTogether), nameof(IsSeparate))]
    public partial GraphVariableLayout Layout { get; set; }

    public bool IsTogether
    {
        get => Layout == GraphVariableLayout.Together;
        set
        {
            if (value)
            {
                Layout = GraphVariableLayout.Together;
            }
        }
    }

    public bool IsSeparate
    {
        get => Layout == GraphVariableLayout.Separate;
        set
        {
            if (value)
            {
                Layout = GraphVariableLayout.Separate;
            }
        }
    }

    // Whether the layout choice does anything: only with two or more variables selected.
    public bool IsLayoutEnabled => SelectedVariableCount >= 2;

    private int SelectedVariableCount =>
        Roles.Where(role => role.Role == GraphVariableRole.Variable).Sum(role => role.SelectedColumnIds.Count);

    // Whether the graph type has the histogram's Y scale and bins; the setup shows them only when it does.
    public bool SupportsHistogramControls => _definition.Supports(GraphCapability.HistogramControls);

    [ObservableProperty]
    public partial SetupChoice<HistogramYScale> SelectedYScale { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBinCountEnabled), nameof(IsBinWidthAndStartEnabled))]
    public partial SetupChoice<HistogramBinningMode> SelectedBinning { get; set; }

    // The number of bins, and the bin width and start, as typed. Only the fields of the chosen mode are read: text in
    // the others never stops the setup from being confirmed.
    [ObservableProperty]
    public partial string BinCountText { get; set; }

    [ObservableProperty]
    public partial string BinWidthText { get; set; }

    [ObservableProperty]
    public partial string BinStartText { get; set; }

    public bool IsBinCountEnabled => SelectedBinning?.Value == HistogramBinningMode.Count;

    public bool IsBinWidthAndStartEnabled => SelectedBinning?.Value == HistogramBinningMode.WidthAndStart;

    // Show normal fit: one of the histogram's own options, so it is offered with them. Unchecked to begin with.
    [ObservableProperty]
    public partial bool ShowNormalFit { get; set; }

    // Whether the graph type has a title and axis titles the user may change; the setup shows the labels only when it
    // does.
    public bool SupportsLabels => _definition.Supports(GraphCapability.Labels);

    // The graph title and the axis titles, edited the way the Edit Labels dialog of a drawn graph edits them.
    public GraphLabelsEditorViewModel Labels { get; }

    // Whether the graph type lets the user choose an axis range; the setup shows the ranges only when it does.
    public bool SupportsAxisRanges => Axes.SupportsAxisRanges;

    // The axis ranges, edited the way the Edit Axes dialog of a drawn graph edits them. Blank is Auto.
    public GraphAxesEditorViewModel Axes { get; }

    // Whether the graph type can have a legend; the setup shows the legend options only when it can.
    public bool SupportsLegend => _definition.Supports(GraphCapability.Legend);

    // The legend options, edited the way the Edit Legend dialog of a drawn graph edits them.
    public GraphLegendEditorViewModel Legend { get; }

    public string Title => _definition.DisplayName;

    // Whether the graph type offers a statistics panel; the setup shows the option only when it does.
    public bool SupportsStatisticsPanel => _definition.Supports(GraphCapability.StatisticsPanel);

    // Show statistics: checked to begin with.
    [ObservableProperty]
    public partial bool ShowStatistics { get; set; }

    // Whether the graph type draws a fitted line the user may hide; the setup shows the option only when it does.
    public bool SupportsFittedLine => _definition.Supports(GraphCapability.FittedLine);

    // Show fitted line: checked to begin with.
    [ObservableProperty]
    public partial bool ShowFittedLine { get; set; }

    // Whether the graph type draws a specification; the setup shows the LSL, Target and USL fields only when it does.
    public bool SupportsSpecificationLines => _definition.Supports(GraphCapability.SpecificationLines);

    // The specification as typed. Blank means "no value"; anything else must be a number (see SpecificationLimitParser).
    [ObservableProperty]
    public partial string LowerLimitText { get; set; }

    [ObservableProperty]
    public partial string TargetText { get; set; }

    [ObservableProperty]
    public partial string UpperLimitText { get; set; }

    public GraphType GraphType => _definition.GraphType;

    public Guid WorksheetId { get; }

    public string WorksheetName { get; }

    // All columns of the worksheet, for the dialog's column list.
    public IReadOnlyList<GraphColumnOption> AvailableColumns { get; }

    public IReadOnlyList<GraphRoleViewModel> Roles { get; }

    // False while a required role is unassigned, an assignment is not valid or the specification is not.
    [ObservableProperty]
    public partial bool CanConfirm { get; private set; }

    // Why the current assignments cannot be confirmed; null when they can.
    [ObservableProperty]
    public partial string? ValidationMessage { get; private set; }

    // What the setup asks for when it is confirmed - the configuration and how its variables are drawn - or null when
    // the configuration is not valid (ValidationMessage says why).
    public GraphSetupRequest? ConfirmRequest() =>
        Confirm() is { } configuration
            ? new GraphSetupRequest(configuration, SupportsVariableLayout ? Layout : GraphVariableLayout.Together)
            : null;

    // The configuration of the current assignments, or null when they are not valid (ValidationMessage says why).
    public GraphConfiguration? Confirm()
    {
        var (configuration, result) = Build();
        ValidationMessage = GraphValidationMessages.For(result, _definition);
        CanConfirm = result.IsValid;
        return result.IsValid ? configuration : null;
    }

    partial void OnLowerLimitTextChanged(string value) => OnSelectionChanged();

    partial void OnTargetTextChanged(string value) => OnSelectionChanged();

    partial void OnUpperLimitTextChanged(string value) => OnSelectionChanged();

    // The configuration the dialog describes, and everything that is wrong with it, in the order the dialog reads:
    // the column assignments first, then specification text that is not a number, then specification values that do
    // not fit together, then the labels, which the dialog shows last. A value that is not a number is left out of the
    // specification, so it is reported once, as text, and not again by the rules.
    private (GraphConfiguration Configuration, GraphValidationResult Result) Build()
    {
        var parseErrors = new List<GraphValidationError>();
        var specification = SupportsSpecificationLines
            ? new Specification(
                Parse(parseErrors, LowerLimitText, SpecificationField.LowerLimit),
                Parse(parseErrors, TargetText, SpecificationField.Target),
                Parse(parseErrors, UpperLimitText, SpecificationField.UpperLimit))
            : Specification.None;

        // One assignment per column a role is given: a role that takes several columns contributes one assignment
        // each, in worksheet order.
        var configuration = new GraphConfiguration(_definition.GraphType, WorksheetId,
        [
            .. Roles.SelectMany(role => role.SelectedColumnIds.Select(columnId => new GraphColumnAssignment(role.Role, columnId)))
        ])
        {
            PresentationOptions = new GraphPresentationOptions(ShowStatistics),

            // Carried whatever the graph type; only one that declares FittedLine reads it.
            ProbabilityPlotOptions = new ProbabilityPlotOptions(ShowFittedLine),
            HistogramOptions = SupportsHistogramControls ? HistogramOptionsFromFields() : HistogramOptions.Default,
            Specification = specification,
            LabelOptions = SupportsLabels ? Labels.Options : GraphLabelOptions.Default,
            AxisRangeOptions = SupportsAxisRanges ? Axes.Options : GraphAxisRangeOptions.Default,
            LegendOptions = SupportsLegend ? Legend.Options : GraphLegendOptions.Default
        };

        var validation = Validator.Validate(configuration, _columns).Errors;
        IReadOnlyList<GraphValidationError> errors =
        [
            .. validation.Where(error => error.Field is null && error.LabelField is null && error.Axis is null),
            .. parseErrors,
            .. validation.Where(error => error.Field is not null),
            .. validation.Where(error => error.LabelField is not null),
            .. SupportsAxisRanges ? Axes.ParseErrors : Array.Empty<GraphValidationError>(),
            .. validation.Where(error => error.Axis is not null)
        ];

        return (configuration, errors.Count == 0 ? GraphValidationResult.Valid : new GraphValidationResult(errors));
    }

    // The value typed into one specification field, or null when it is blank - or when it is not a number, which is
    // then reported for that field. The parser is the one capability limits are read with.
    private static double? Parse(List<GraphValidationError> errors, string? text, SpecificationField field)
    {
        if (SpecificationLimitParser.TryParse(text, out var value))
        {
            return value;
        }

        errors.Add(new GraphValidationError(GraphValidationReason.SpecificationValueNotNumeric, Field: field));
        return null;
    }

    // The histogram options the fields describe. Only the chosen mode's fields are read; text that is not a usable
    // number is left out, and the validator then says which value is missing - one message per field, whether it was
    // blank or not a number. Numbers are read as everywhere in YAT: invariant culture, whole numbers for a count.
    private HistogramOptions HistogramOptionsFromFields()
    {
        var mode = SelectedBinning.Value;
        return new HistogramOptions(
            SelectedYScale.Value,
            mode,
            BinCount: mode == HistogramBinningMode.Count ? ParseCount(BinCountText) : null,
            BinWidth: mode == HistogramBinningMode.WidthAndStart ? ParseNumber(BinWidthText) : null,
            BinStart: mode == HistogramBinningMode.WidthAndStart ? ParseNumber(BinStartText) : null,
            ShowNormalFit: ShowNormalFit);
    }

    private static int? ParseCount(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) ? count : null;

    private static double? ParseNumber(string? text) =>
        SpecificationLimitParser.TryParse(text, out var value) ? value : null;

    partial void OnSelectedYScaleChanged(SetupChoice<HistogramYScale> value) => OnSelectionChanged();

    partial void OnSelectedBinningChanged(SetupChoice<HistogramBinningMode> value) => OnSelectionChanged();

    partial void OnBinCountTextChanged(string value) => OnSelectionChanged();

    partial void OnBinWidthTextChanged(string value) => OnSelectionChanged();

    partial void OnBinStartTextChanged(string value) => OnSelectionChanged();

    private void OnSelectionChanged()
    {
        // The constructor sets the histogram choices and the labels after other properties whose change handlers land
        // here.
        if (SelectedYScale is null || SelectedBinning is null || Labels is null || Axes is null)
        {
            return;
        }

        CanConfirm = Build().Result.IsValid;
        OnPropertyChanged(nameof(IsLayoutEnabled));
        ValidationMessage = null;
    }

    private static GraphColumnOption Option(WorksheetColumn column) => new(column.Id, column.Name, column.DataType.ToString());
}
