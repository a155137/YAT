using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using YAT.Application.Analyses;
using YAT.Application.Graphs;
using YAT.app.Analyses;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// How the ticks of an axis are chosen in its scale dialog (Task #054).
public enum GraphAxisTickMode
{
    Auto,
    Interval,
    Custom
}

// One axis of a drawn graph as the user edits it in its Edit X Scale / Edit Y Scale dialog.
//
// Its range (Task #052): a minimum and a maximum, each Auto or a typed value. It is the #043 range of that axis - a
// GraphAxisRangeOption, Auto being null - read with the same parser and refused by the same rules in the same words
// (GraphConfigurationValidator, GraphValidationMessages), and by the same fit to the graph's automatic ends
// (GraphAxisViewportBuilder.Conflicts). The other axis's range is kept as it is, and checked with it. A field under Auto
// shows the graph's automatic value there; unchecking Auto starts the field from that value.
//
// Its ticks (Task #054), apart from its range: Auto, an interval, or values of the user's own - typed in the units the
// axis shows (percent on a percent or probability axis), separated by commas, semicolons or spaces, in any order. They
// are refused by the tick rules over the range the dialog describes (GraphAxisTickBuilder.Problems); values that lie
// off that range are allowed and kept, and the dialog says how many. The interval and value fields start from the
// ticks the axis shows now, so a user edits what is there.
//
// Reset to Auto sets both the range and the ticks to Auto, in the dialog only: nothing is applied until OK.
public sealed partial class GraphAxisScaleEditorViewModel : ObservableObject
{
    private static readonly char[] ValueSeparators = [',', ';', ' ', '\t', '\r', '\n'];

    private readonly GraphTypeDefinition _definition;
    private readonly GraphAxisRangeOptions _current;
    private readonly GraphRenderModel _autoFrame;

    public GraphAxisScaleEditorViewModel(
        GraphTypeDefinition definition,
        GraphAxisField axis,
        GraphAxisRangeOptions current,
        GraphRenderModel autoFrame,
        GraphAxisTickOptions? currentTicks = null)
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

        // The fields start from the ticks the axis has: chosen ones as they were chosen, Auto ones as they are shown.
        var ticks = (currentTicks ?? GraphAxisTickOptions.Default).For(axis);
        var shown = ShownAxis(current) ?? (axis == GraphAxisField.X ? autoFrame.XAxis : autoFrame.YAxis);
        IntervalText = ticks is GraphAxisTickOption.FixedInterval { Interval: var interval } ? TextOf(interval) : AutoInterval(shown);
        ValuesText = ticks is GraphAxisTickOption.CustomValues { Values: var values }
            ? string.Join(", ", values.Select(TextOf))
            : string.Join(", ", shown.Ticks.Select(tick => tick.Label));
        TickMode = ticks switch
        {
            GraphAxisTickOption.FixedInterval => GraphAxisTickMode.Interval,
            GraphAxisTickOption.CustomValues => GraphAxisTickMode.Custom,
            _ => GraphAxisTickMode.Auto
        };
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
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Edit), nameof(Errors), nameof(ValidationMessage), nameof(IsValid), nameof(TickNotice))]
    public partial bool MinimumIsAuto { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Edit), nameof(Errors), nameof(ValidationMessage), nameof(IsValid), nameof(TickNotice))]
    public partial bool MaximumIsAuto { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Edit), nameof(Errors), nameof(ValidationMessage), nameof(IsValid), nameof(TickNotice))]
    public partial string MinimumText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Option), nameof(Options), nameof(Edit), nameof(Errors), nameof(ValidationMessage), nameof(IsValid), nameof(TickNotice))]
    public partial string MaximumText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TicksAreAuto), nameof(TicksAreInterval), nameof(TicksAreCustom), nameof(TickOption), nameof(Edit), nameof(ValidationMessage), nameof(IsValid), nameof(TickNotice))]
    public partial GraphAxisTickMode TickMode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TickOption), nameof(Edit), nameof(ValidationMessage), nameof(IsValid), nameof(TickNotice))]
    public partial string IntervalText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TickOption), nameof(Edit), nameof(ValidationMessage), nameof(IsValid), nameof(TickNotice))]
    public partial string ValuesText { get; set; }

    // The three tick choices as the dialog's radio buttons set them.
    public bool TicksAreAuto
    {
        get => TickMode == GraphAxisTickMode.Auto;
        set => Choose(value, GraphAxisTickMode.Auto);
    }

    public bool TicksAreInterval
    {
        get => TickMode == GraphAxisTickMode.Interval;
        set => Choose(value, GraphAxisTickMode.Interval);
    }

    public bool TicksAreCustom
    {
        get => TickMode == GraphAxisTickMode.Custom;
        set => Choose(value, GraphAxisTickMode.Custom);
    }

    // The range the dialog describes for its axis: Auto where Auto is checked, the typed value elsewhere (null, and so
    // Auto, when it is not a number - which Errors then reports).
    public GraphAxisRangeOption Option => new(MinimumIsAuto ? null : Parse(MinimumText), MaximumIsAuto ? null : Parse(MaximumText));

    // Both axes' ranges as they would be: this axis's from the dialog, the other's as it is.
    public GraphAxisRangeOptions Options => Axis == GraphAxisField.X ? _current with { X = Option } : _current with { Y = Option };

    // The ticks the dialog describes for its axis, or null while what is typed for them is not numbers.
    public GraphAxisTickOption? TickOption => TickMode switch
    {
        GraphAxisTickMode.Interval => Parse(IntervalText) is { } interval ? new GraphAxisTickOption.FixedInterval(interval) : null,
        GraphAxisTickMode.Custom => ParseValues(ValuesText, out var values) is null ? new GraphAxisTickOption.CustomValues(values) : null,
        _ => GraphAxisTickOption.Auto
    };

    // What OK confirms: the axis's range and its ticks. Null while either cannot be used.
    public GraphAxisScaleEdit? Edit => IsValid && TickOption is { } ticks ? new GraphAxisScaleEdit(Option, ticks) : null;

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

    // Why the range or the ticks cannot be used, in the setup's words - including a range that does not fit the
    // graph's automatic ends, and ticks that cannot be drawn over the range - or null when they can. The range first:
    // the ticks are checked over it.
    public string? ValidationMessage
    {
        get
        {
            if (Errors is [var error, ..])
            {
                return GraphValidationMessages.For(error, _definition);
            }

            if (GraphAxisViewportBuilder.Conflicts(_autoFrame, _definition, Options).FirstOrDefault() is { } conflict)
            {
                return conflict;
            }

            if (TickParseError() is { } parseError)
            {
                return parseError;
            }

            return TickOption is { } ticks && ShownAxis(Options) is { } axis
                ? GraphAxisTickBuilder.Problems(axis, _definition, Axis, ticks).FirstOrDefault()
                : null;
        }
    }

    public bool IsValid => ValidationMessage is null;

    // Said, not refused: how many of the values typed lie off the range the dialog describes - kept, and drawn once a
    // range reaches them. Null when there are none, or nothing can be said yet.
    public string? TickNotice
    {
        get
        {
            if (TickMode != GraphAxisTickMode.Custom || !IsValid || TickOption is not GraphAxisTickOption.CustomValues values
                || ShownAxis(Options) is not { } axis
                || GraphAxisTickBuilder.OutsideCount(axis, values) is not (> 0 and var outside))
            {
                return null;
            }

            return $"{outside} of {values.Values.Count} tick values lie outside the range shown. They are kept, and " +
                "drawn when the range reaches them.";
        }
    }

    // Reset to Auto: both ends of the range and the ticks Auto again, in the dialog only.
    public void ResetToAuto()
    {
        MinimumIsAuto = true;
        MaximumIsAuto = true;
        TickMode = GraphAxisTickMode.Auto;
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

    private void Choose(bool chosen, GraphAxisTickMode mode)
    {
        if (chosen)
        {
            TickMode = mode;
        }
    }

    // This axis as it would be shown over these ranges - its range and its Auto ticks - or null while the ranges cannot
    // be shown.
    private GraphAxisModel? ShownAxis(GraphAxisRangeOptions ranges)
    {
        if (GraphConfigurationValidator.AxisRangeErrors(ranges, _definition).Count > 0
            || GraphAxisViewportBuilder.Conflicts(_autoFrame, _definition, ranges).Count > 0)
        {
            return null;
        }

        var frame = GraphAxisViewportBuilder.Attach(_autoFrame, _definition, ranges);
        return Axis == GraphAxisField.X ? frame.XAxis : frame.YAxis;
    }

    // What typed for the ticks is not a number, in the user's words; null when everything is.
    private string? TickParseError()
    {
        var name = GraphAxisViewportBuilder.AxisName(Axis);
        return TickMode switch
        {
            GraphAxisTickMode.Interval when Parse(IntervalText) is null => $"The {name} tick interval must be a number.",
            GraphAxisTickMode.Custom when ParseValues(ValuesText, out _) is { } text =>
                text.Length == 0 ? $"Enter at least one {name} tick value." : $"The {name} tick value \"{text}\" is not a number.",
            _ => null
        };
    }

    // The step between the Auto ticks the axis shows, as a starting interval: blank on a probability axis, whose Auto
    // ticks are not evenly spaced, or with fewer than two ticks.
    private static string AutoInterval(GraphAxisModel axis)
    {
        if (axis.Scale == GraphAxisScale.Probability || axis.Ticks.Count < 2)
        {
            return string.Empty;
        }

        // Rounded to the digits the ticks were made of: 14.92 - 14.9 is 0.02, not 0.019999999999999574.
        var step = axis.Ticks[1].Value - axis.Ticks[0].Value;
        return TextOf(double.Parse(step.ToString("G12", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
    }

    private void AddParseError(List<GraphValidationError> errors, GraphAxisBound bound, bool isAuto, string? text)
    {
        if (!isAuto && Parse(text) is null)
        {
            errors.Add(new GraphValidationError(GraphValidationReason.AxisRangeValueNotNumeric, Axis: Axis, Bound: bound));
        }
    }

    // The values typed for the ticks. Returns null when every one is a number, the first that is not when one is not,
    // and an empty text when none was typed.
    private static string? ParseValues(string? text, out List<double> values)
    {
        values = [];
        foreach (var part in (text ?? string.Empty).Split(ValueSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (Parse(part) is not { } value)
            {
                return part;
            }

            values.Add(value);
        }

        return values.Count == 0 ? string.Empty : null;
    }

    // A typed value, read as everywhere in YAT (invariant culture); blank and not-a-number (NaN, infinity) are null.
    private static double? Parse(string? text) => SpecificationLimitParser.TryParse(text, out var value) ? value : null;

    // A chosen value as it is typed back: the shortest text that reads as the same number.
    private static string TextOf(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
