using YAT.Domain.Enums;

namespace YAT.Application.Analyses;

// Which worksheet columns an analysis role accepts. Measured variables are Numeric; a group may also be a String
// column (how groups are labelled is not decided here). Validation and the setup dialog read the rule from here, so
// there is one answer to "may this column be used for this".
public static class AnalysisColumnRoles
{
    private static readonly IReadOnlyList<WorksheetDataType> Numeric = [WorksheetDataType.Numeric];

    private static readonly IReadOnlyList<WorksheetDataType> NumericOrString = [WorksheetDataType.Numeric, WorksheetDataType.String];

    public static IReadOnlyList<WorksheetDataType> AllowedDataTypes(AnalysisColumnRole role) =>
        role == AnalysisColumnRole.Group ? NumericOrString : Numeric;

    public static bool Allows(AnalysisColumnRole role, WorksheetDataType dataType) => AllowedDataTypes(role).Contains(dataType);
}
