using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Analyses;
using YAT.Application.Graphs;
using YAT.app.Analyses;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// The range of one axis of a drawn graph as the user edits it in its Edit X Scale / Edit Y Scale dialog (Task #052):
// a minimum and a maximum, each Auto or a typed value. It is the #043 range of that axis - a GraphAxisRangeOption, Auto
// being null - read with the same parser and refused by the same rules in the same words (GraphConfigurationValidator,
// GraphValidationMessages), and by the same fit to the graph's automatic ends (GraphAxisViewportBuilder.Conflicts).
// The other axis's range is kept as it is, and checked with it.
//
// A field under Auto shows the graph's automatic value there; unchecking Auto starts the field from that value. Reset to
// Auto checks both: nothing is applied until OK.
public sealed partial class GraphAxisScaleEditorViewModel : ObservableObject
{
    private readonly GraphTypeDefinition _definition;
    private readonly GraphAxisRangeOptions _current;
    private readonly GraphRenderModel _autoFrame;

    public GraphAxisScaleEditorViewModel(
        GraphTypeDefinition definition,
        GraphAxisField axis,
        GraphAxisRangeOptions current,
        GraphRenderModel autoFrame)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(autoFrame);
        if (!definition.SupportsAxisRange(axis))
        {
            throw new ArgumentException($"A {definition.DisplayName} has no range to choose for its {axis} axis.", nameof(axis));
        }

        _definition = definition;
        _current = current;
        _autoFrame = autoFrame;
        Axis = axis;

        var (minimum, maximum) = GraphAxisViewportBuilder.AutoRange(axis == GraphAxisField.X ? autoFrame.XAxis : autoFrame.YAxis);
        AutoMinimumText = AnalysisNumberFormat.Statistic(minimum);
        AutoMaximumText = AnalysisNumberFormat.Statistic(maximum);

        var option = current.For(axis);
        MinimumText = option.Minimum is { } chosenMinimum ? TextOf(chosenMinimum) : AutoMinimumText;
        MaximumText = option.Maximum is { } chosenMaximum ? TextOf(chosenMaximum) : AutoMaximumText;
        MinimumIsAuto = option.Minimum is null;
        MaximumIsAuto = option.Maximum is null;
    }

    public GraphAxisField Axis { get; }

    // "Edit X Scale", "Edit Y Scale".
    public string Title => Axis == GraphAxisField.X ? "Edit X Scale" : "Edit Y Scale";

    // The axis as the Edit Axes dialog names it: "Y axis (%)" for a probability axis, typed in the percent it shows.
    public string AxisLabel => GraphAxesEditorViewModel.AxisLabel(_definition, Axis);

    // "%" beside the fields of an axis read in percent (a probability plot's or an empirical CDF's Y), else null.
    public string? Unit => _definition.AxisKind(Axis) is GraphAxisKind.Percent or GraphAxisKind.ProbabilityPercent ? "%" : null;

    // The graph's automatic ends, as the Edit Axes dialog shows them.
    public string AutoMinimumText { get; }

    public string AutoMaximumText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial bool MinimumIsAuto { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial bool MaximumIsAuto { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial string MinimumText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial string MaximumText { get; set; }

    // The range the dialog describes for its axis: Auto where Auto is checked, the typed value elsewhere (null, and so
    // Auto, when it is not a number - which Errors then reports).
    public GraphAxisRangeOption Option => new(MinimumIsAuto ? null : Parse(MinimumText), MaximumIsAuto ? null : Parse(MaximumText));

    // Both axes' ranges as they would be: this axis's from the dialog, the other's as it is.
    public GraphAxisRangeOptions Options => Axis == GraphAxisField.X ? _current with { X = Option } : _current with { Y = Option };

    // A field that is not Auto must hold a number; then the rules of #043 for the range.
    public IReadOnlyList<GraphValidationError> Errors
    {
        get
        {
            var errors = new List<GraphValidationError>();
            AddParseError(errors, GraphAxisBound.Minimum, MinimumIsAuto, MinimumText);
            AddParseError(errors, GraphAxisBound.Maximum, MaximumIsAuto, MaximumText);
            errors.AddRange(GraphConfigurationValidator.AxisRangeErrors(Options, _definition).Where(error => error.Axis == Axis));
            return errors;
        }
    }

    // Why the range cannot be used, in the setup's words - including a range that does not fit the graph's automatic
    // ends - or null when it can.
    public string? ValidationMessage =>
        Errors is [var error, ..]
            ? GraphValidationMessages.For(error, _definition)
            : GraphAxisViewportBuilder.Conflicts(_autoFrame, _definition, Options).FirstOrDefault();

    public bool IsValid => ValidationMessage is null;

    // Reset to Auto: both ends Auto again, in the dialog only.
    public void ResetToAuto()
    {
        MinimumIsAuto = true;
        MaximumIsAuto = true;
    }

    // Under Auto a field shows the automatic value - which is where it starts when Auto is unchecked.
    partial void OnMinimumIsAutoChanged(bool value)
    {
        if (value)
        {
            MinimumText = AutoMinimumText;
        }
    }

    partial void OnMaximumIsAutoChanged(bool value)
    {
        if (value)
        {
            MaximumText = AutoMaximumText;
        }
    }

    private void AddParseError(List<GraphValidationError> errors, GraphAxisBound bound, bool isAuto, string? text)
    {
        if (!isAuto && Parse(text) is null)
        {
            errors.Add(new GraphValidationError(GraphValidationReason.AxisRangeValueNotNumeric, Axis: Axis, Bound: bound));
        }
    }

    // A typed value, read as everywhere in YAT (invariant culture); blank and not-a-number (NaN, infinity) are null.
    private static double? Parse(string? text) => SpecificationLimitParser.TryParse(text, out var value) ? value : null;

    // A chosen value as it is typed back: the shortest text that reads as the same number.
    private static string TextOf(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
