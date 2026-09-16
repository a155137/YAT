using YAT.Application.Graphs;

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
            _ => "This graph cannot be created with the current settings."
        };
    }
}
