using YAT.Application.Analyses;

namespace YAT.app.ViewModels;

// User-facing text for analysis configuration problems. Column ids and role enum names are never shown; a column is
// named when the caller can resolve its name.
public static class AnalysisValidationMessages
{
    // The first problem of the result, or null when it is valid.
    public static string? For(AnalysisValidationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return For(result.Errors, _ => null);
    }

    // The first problem of a list of errors, or null when there is none. columnName turns the column an error is about
    // into the name the user typed a specification for; it may return null when the column cannot be named.
    public static string? For(IReadOnlyList<AnalysisValidationError> errors, Func<Guid, string?> columnName)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(columnName);
        return errors.Count == 0 ? null : For(errors[0], columnName);
    }

    public static string For(AnalysisValidationError error) => For(error, _ => null);

    public static string For(AnalysisValidationError error, Func<Guid, string?> columnName)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(columnName);

        // "Reg1: " when the error is about a column that can be named, so a specification problem points at the row
        // the user has to correct.
        var subject = error.WorksheetColumnId is { } columnId && columnName(columnId) is { } name ? $"{name}: " : string.Empty;
        var side = error.Field == AnalysisSpecificationField.UpperSpecificationLimit ? "upper" : "lower";

        return error.Reason switch
        {
            AnalysisValidationReason.NoVariableSelected => "Please select at least one variable.",
            AnalysisValidationReason.DuplicateVariable => "Please select each variable only once.",
            AnalysisValidationReason.ColumnNotFound => "A selected column is no longer available.",
            AnalysisValidationReason.ColumnFromAnotherWorksheet => "A selected column belongs to another worksheet.",
            AnalysisValidationReason.IncompatibleDataType when error.Role == AnalysisColumnRole.Group =>
                "Please select a Numeric or String column for the grouping variable.",
            AnalysisValidationReason.IncompatibleDataType => "Please select Numeric columns as variables.",
            AnalysisValidationReason.SpecificationLimitNotNumeric =>
                $"{subject}the {side} specification limit must be a number.",
            AnalysisValidationReason.SpecificationLimitMissing =>
                $"{subject}please enter a lower or an upper specification limit.",
            AnalysisValidationReason.SpecificationLimitsOutOfOrder =>
                $"{subject}the lower specification limit must be below the upper one.",
            AnalysisValidationReason.NoStatisticSelected => "Please select at least one statistic to display.",
            AnalysisValidationReason.FilterColumnNotFound => "The filter column is no longer available. Edit the filter.",
            AnalysisValidationReason.FilterColumnFromAnotherWorksheet => "The filter column belongs to another worksheet. Edit the filter.",
            AnalysisValidationReason.FilterColumnIncompatibleType => "The filter does not match its column's data type. Edit the filter.",
            AnalysisValidationReason.FilterSelectionEmpty =>
                "Choose at least one value, or (Missing), for each \"is any of\" or \"is not any of\" condition of the filter.",
            AnalysisValidationReason.FilterTooManyConditions => "A filter can have at most 20 conditions. Edit the filter.",
            _ => "This analysis cannot be run with the current settings."
        };
    }
}
