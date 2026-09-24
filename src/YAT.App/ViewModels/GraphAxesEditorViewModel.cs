using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Analyses;
using YAT.Application.Graphs;
using YAT.app.Analyses;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// A graph's axis ranges as the user edits them (Task #043): a minimum and a maximum for each axis the graph type lets
// the user choose, typed as text; a blank field is Auto. The graph setup and the Edit Axes dialog of a drawn graph both
// edit ranges through this one editor, so both read the same text the same way and refuse the same ranges for the same
// reasons (GraphConfigurationValidator.AxisRangeErrors) in the same words.
//
// Opened on a drawn graph, the editor also knows the graph's automatic ranges (its frame before any range was chosen):
// it shows them beside every blank field, and it refuses a range that does not fit them - a minimum at or above the
// automatic maximum it would be shown against, for example - as the graph would (GraphAxisViewportBuilder.Conflicts).
// The setup, which has no graph yet, leaves that to the graph preparation.
public sealed partial class GraphAxesEditorViewModel : ObservableObject
{
    private readonly GraphTypeDefinition _definition;
    private readonly GraphRenderModel? _autoFrame;

    // Every axis Auto with nothing typed, with no graph to show automatic values from: how a new setup starts.
    public GraphAxesEditorViewModel(GraphTypeDefinition definition)
        : this(definition, GraphAxisRangeOptions.Default, autoFrame: null)
    {
    }

    // The ranges as they are now, ready to be changed. autoFrame is the drawn graph's frame before any range was
    // chosen.
    public GraphAxesEditorViewModel(
        GraphTypeDefinition definition,
        GraphAxisRangeOptions current,
        GraphRenderModel? autoFrame)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(current);

        _definition = definition;
        _autoFrame = autoFrame;
        XMinimumText = TextOf(current.X.Minimum);
        XMaximumText = TextOf(current.X.Maximum);
        YMinimumText = TextOf(current.Y.Minimum);
        YMaximumText = TextOf(current.Y.Maximum);
    }

    public bool SupportsXAxisRange => _definition.SupportsAxisRange(GraphAxisField.X);

    public bool SupportsYAxisRange => _definition.SupportsAxisRange(GraphAxisField.Y);

    public bool SupportsAxisRanges => SupportsXAxisRange || SupportsYAxisRange;

    // What each row is called. A probability axis is typed in the percent it shows.
    public string XAxisLabel => Label(GraphAxisField.X);

    public string YAxisLabel => Label(GraphAxisField.Y);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial string XMinimumText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial string XMaximumText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial string YMinimumText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Options), nameof(Errors), nameof(ValidationMessage), nameof(IsValid))]
    public partial string YMaximumText { get; set; }

    // What a blank field means: "Auto", or "Auto (14.88)" with the value the drawn graph shows there.
    public string XMinimumHint => Hint(GraphAxisField.X, GraphAxisBound.Minimum);

    public string XMaximumHint => Hint(GraphAxisField.X, GraphAxisBound.Maximum);

    public string YMinimumHint => Hint(GraphAxisField.Y, GraphAxisBound.Minimum);

    public string YMaximumHint => Hint(GraphAxisField.Y, GraphAxisBound.Maximum);

    // The ranges the fields describe. A blank field is Auto, and so is text that is not a number - which ParseErrors
    // then reports, so it is reported once, as text, and not again by the rules. An axis the graph type does not let
    // the user choose is always Auto.
    public GraphAxisRangeOptions Options => new(
        Option(GraphAxisField.X, XMinimumText, XMaximumText),
        Option(GraphAxisField.Y, YMinimumText, YMaximumText));

    // Text in a field of a range the user may choose that is not a number, X first, minimum before maximum.
    public IReadOnlyList<GraphValidationError> ParseErrors
    {
        get
        {
            var errors = new List<GraphValidationError>();
            AddParseError(errors, GraphAxisField.X, GraphAxisBound.Minimum, XMinimumText);
            AddParseError(errors, GraphAxisField.X, GraphAxisBound.Maximum, XMaximumText);
            AddParseError(errors, GraphAxisField.Y, GraphAxisBound.Minimum, YMinimumText);
            AddParseError(errors, GraphAxisField.Y, GraphAxisBound.Maximum, YMaximumText);
            return errors;
        }
    }

    // Everything the rules find wrong with the ranges, text that is not a number first; empty when they can be used.
    public IReadOnlyList<GraphValidationError> Errors =>
        [.. ParseErrors, .. GraphConfigurationValidator.AxisRangeErrors(Options, _definition)];

    // Why the ranges cannot be used, in the setup's words - including, on a drawn graph, a range that does not fit its
    // automatic ends - or null when they can.
    public string? ValidationMessage =>
        Errors is [var error, ..]
            ? GraphValidationMessages.For(error, _definition)
            : _autoFrame is { } frame
                ? GraphAxisViewportBuilder.Conflicts(frame, _definition, Options).FirstOrDefault()
                : null;

    public bool IsValid => ValidationMessage is null;

    private string Label(GraphAxisField axis)
    {
        var name = axis == GraphAxisField.X ? "X axis" : "Y axis";
        return _definition.AxisKind(axis) == GraphAxisKind.ProbabilityPercent ? $"{name} (%)" : name;
    }

    private string Hint(GraphAxisField axis, GraphAxisBound bound)
    {
        if (_autoFrame is not { } frame || !_definition.SupportsAxisRange(axis))
        {
            return "Auto";
        }

        var (minimum, maximum) =
            GraphAxisViewportBuilder.AutoRange(axis == GraphAxisField.X ? frame.XAxis : frame.YAxis);
        return $"Auto ({AnalysisNumberFormat.Statistic(bound == GraphAxisBound.Minimum ? minimum : maximum)})";
    }

    private GraphAxisRangeOption Option(GraphAxisField axis, string? minimum, string? maximum) =>
        _definition.SupportsAxisRange(axis)
            ? new GraphAxisRangeOption(Parse(minimum), Parse(maximum))
            : GraphAxisRangeOption.Auto;

    private void AddParseError(
        List<GraphValidationError> errors,
        GraphAxisField axis,
        GraphAxisBound bound,
        string? text)
    {
        if (_definition.SupportsAxisRange(axis) && !SpecificationLimitParser.TryParse(text, out _))
        {
            errors.Add(new GraphValidationError(
                GraphValidationReason.AxisRangeValueNotNumeric, Axis: axis, Bound: bound));
        }
    }

    // A typed value: numbers are read as everywhere in YAT (invariant culture); blank and not-a-number are null.
    private static double? Parse(string? text) => SpecificationLimitParser.TryParse(text, out var value) ? value : null;

    // A chosen value as it is typed back: the shortest text that reads as the same number.
    private static string TextOf(double? value) =>
        value is { } number ? number.ToString("R", CultureInfo.InvariantCulture) : string.Empty;
}
