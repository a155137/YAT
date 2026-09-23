using YAT.Application.Graphs;
using YAT.Application.Specifications;

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
            _ => "This graph cannot be created with the current settings."
        };
    }

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
