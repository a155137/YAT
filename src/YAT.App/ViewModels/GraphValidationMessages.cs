using System.Globalization;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Rendering;

namespace YAT.app.ViewModels;

// User-facing text for graph configuration problems. Column ids and role enum names are never shown.
public static class GraphValidationMessages
{
    // The first problem of the result, or null when it is valid.
    public static string? For(GraphValidationResult result, GraphTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(definition);

        return result.Errors.Count == 0 ? null : For(result.Errors[0], definition);
    }

    public static string For(GraphValidationError error, GraphTypeDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(definition);

        var roleName = error.Role is { } role ? definition.FindRole(role)?.DisplayName ?? role.ToString() : null;

        return error.Reason switch
        {
            GraphValidationReason.MissingRequiredRole when error.Role == GraphVariableRole.Variable => "Please select a variable.",
            GraphValidationReason.MissingRequiredRole or GraphValidationReason.IncompatibleDataType =>
                $"Please select a Numeric column for {roleName}.",
            GraphValidationReason.TooManyColumns when error.Role == GraphVariableRole.Variable =>
                $"Select at most {GraphRoleDefinition.MaximumColumns} variables.",
            GraphValidationReason.TooManyColumns =>
                $"Select at most {GraphRoleDefinition.MaximumColumns} columns for {roleName}.",
            GraphValidationReason.PanelSameAsGroup =>
                "Choose a different column for panels than for grouping: each panel is grouped by the group column.",
            GraphValidationReason.ColumnNotFound => "The selected column is no longer available.",
            GraphValidationReason.ColumnFromAnotherWorksheet => "The selected column belongs to another worksheet.",
            GraphValidationReason.SpecificationValueNotNumeric => $"{FieldName(error.Field)} must be a number.",
            GraphValidationReason.SpecificationLimitsOutOfOrder => "LSL must be below USL.",
            GraphValidationReason.SpecificationTargetOutsideLimits => "Target must lie within the specification limits (LSL to USL).",
            GraphValidationReason.HistogramBinCountInvalid =>
                $"Number of bins must be a whole number from {HistogramOptions.MinimumBinCount} to {HistogramOptions.MaximumBinCount}.",
            GraphValidationReason.HistogramBinWidthInvalid => "Bin width must be a positive number.",
            GraphValidationReason.HistogramBinStartInvalid => "Bin start must be a number.",
            GraphValidationReason.HistogramOptionsInvalid => "Please choose a Y scale and a way to choose the bins.",
            GraphValidationReason.LabelTextMissing =>
                $"Enter {LabelName(error.LabelField, article: true)}, or choose Auto or Hidden.",
            GraphValidationReason.LabelModeInvalid =>
                $"Please choose Auto, Custom or Hidden for the {LabelName(error.LabelField, article: false)}.",
            GraphValidationReason.AxisRangeValueNotNumeric => $"{BoundName(error)} must be a number.",
            GraphValidationReason.AxisRangeValueOutsideAxis => OutsideAxis(error, definition),
            GraphValidationReason.AxisRangeNotIncreasing =>
                $"{AxisName(error.Axis)} minimum must be below the {AxisName(error.Axis)} maximum.",
            GraphValidationReason.AxisRangeTooNarrow => $"The {AxisName(error.Axis)} range is too narrow to be shown.",
            GraphValidationReason.LegendOptionsInvalid => "Please choose how the legend is shown and where.",
            GraphValidationReason.StatisticsOptionsInvalid => "Please choose how the statistics are shown.",
            GraphValidationReason.StatisticsItemsMissing => "Choose at least one statistic, or hide the statistics.",
            GraphValidationReason.AppearanceGridModeInvalid => "Please choose how the grid is shown.",
            GraphValidationReason.AppearancePaletteInvalid =>
                $"A custom palette has {GraphPalette.MinimumColors} to {GraphPalette.MaximumColors} colors.",
            GraphValidationReason.AppearanceColorInvalid => "Enter each color as #RRGGBB, for example #1F77B4.",
            GraphValidationReason.BoxPlotWidthInvalid =>
                $"Box width must be a whole number from {BoxPlotOptions.MinimumBoxWidthPercent} to {BoxPlotOptions.MaximumBoxWidthPercent}.",
            GraphValidationReason.FilterColumnNotFound => "The filter column is no longer available. Edit the filter.",
            GraphValidationReason.FilterColumnFromAnotherWorksheet => "The filter column belongs to another worksheet. Edit the filter.",
            GraphValidationReason.FilterColumnIncompatibleType =>
                "The filter's values do not match its column. Choose a Numeric or String column and its values.",
            GraphValidationReason.FilterSelectionEmpty =>
                "Choose at least one value, or (Missing), for each \"is any of\" or \"is not any of\" condition of the filter.",
            GraphValidationReason.FilterTooManyConditions => "A filter can have at most 20 conditions. Edit the filter.",
            _ => "This graph cannot be created with the current settings."
        };
    }

    // "X-axis", "Y-axis", as the setup and the graph preparation name the axes.
    private static string AxisName(GraphAxisField? axis) => GraphAxisViewportBuilder.AxisName(axis ?? GraphAxisField.X);

    // "X-axis minimum", "Y-axis maximum".
    private static string BoundName(GraphValidationError error) =>
        $"{AxisName(error.Axis)} {(error.Bound == GraphAxisBound.Maximum ? "maximum" : "minimum")}";

    // What an axis of this kind can show, in the words its values are typed in.
    private static string OutsideAxis(GraphValidationError error, GraphTypeDefinition definition) =>
        definition.AxisKind(error.Axis ?? GraphAxisField.X) switch
        {
            GraphAxisKind.NonNegative => $"{BoundName(error)} cannot be below 0.",
            GraphAxisKind.Percent => $"{BoundName(error)} must be from 0 to 100.",
            GraphAxisKind.ProbabilityPercent =>
                $"{BoundName(error)} must be from {Percent(GraphAxisRangeRules.MinimumProbabilityPercent)} " +
                $"to {Percent(GraphAxisRangeRules.MaximumProbabilityPercent)} (%).",
            _ => $"{BoundName(error)} must be a number."
        };

    private static string Percent(double value) => value.ToString(CultureInfo.InvariantCulture);

    // The labels as the setup names them: "Graph title", "X-axis title", "Y-axis title".
    private static string LabelName(GraphLabelField? field, bool article) => field switch
    {
        GraphLabelField.XAxisTitle => article ? "an X-axis title" : "X-axis title",
        GraphLabelField.YAxisTitle => article ? "a Y-axis title" : "Y-axis title",
        _ => article ? "a graph title" : "graph title"
    };

    // The names the setup labels the specification fields with.
    private static string FieldName(SpecificationField? field) => field switch
    {
        SpecificationField.LowerLimit => "LSL",
        SpecificationField.UpperLimit => "USL",
        _ => "Target"
    };
}
