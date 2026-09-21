using YAT.Application.Analyses;

namespace YAT.app.ViewModels;

// User-facing text for analysis configuration problems. Column ids and role enum names are never shown.
public static class AnalysisValidationMessages
{
    // The first problem of the result, or null when it is valid.
    public static string? For(AnalysisValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.Errors.Count == 0 ? null : For(result.Errors[0]);
    }

    public static string For(AnalysisValidationError error)
    {
        ArgumentNullException.ThrowIfNull(error);

        return error.Reason switch
        {
            AnalysisValidationReason.NoVariableSelected => "Please select at least one variable.",
            AnalysisValidationReason.DuplicateVariable => "Please select each variable only once.",
            AnalysisValidationReason.ColumnNotFound => "A selected column is no longer available.",
            AnalysisValidationReason.ColumnFromAnotherWorksheet => "A selected column belongs to another worksheet.",
            AnalysisValidationReason.IncompatibleDataType when error.Role == AnalysisColumnRole.Group =>
                "Please select a Numeric or String column for the grouping variable.",
            AnalysisValidationReason.IncompatibleDataType => "Please select Numeric columns as variables.",
            _ => "This analysis cannot be run with the current settings."
        };
    }
}
