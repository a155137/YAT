using YAT.Domain.Entities;

namespace YAT.Application.Analyses;

public enum AnalysisValidationReason
{
    // No variable was selected: an analysis summarises at least one.
    NoVariableSelected,

    // The same column was selected as a variable more than once.
    DuplicateVariable,

    // A selected column is not among the worksheet's columns (e.g. it was deleted).
    ColumnNotFound,

    // A selected column belongs to a different worksheet: an analysis reads one worksheet.
    ColumnFromAnotherWorksheet,

    // The column's data type is not allowed for its role (a variable must be Numeric; a group may also be String).
    IncompatibleDataType,

    // Capability: the text of a specification limit is not a finite number.
    SpecificationLimitNotNumeric,

    // Capability: the variable has neither a lower nor an upper specification limit.
    SpecificationLimitMissing,

    // Capability: the lower specification limit is not below the upper one.
    SpecificationLimitsOutOfOrder,

    // Capability: every optional result statistic was switched off, leaving nothing but the structural columns.
    NoStatisticSelected
}

// Which side of a specification an error is about. Null for errors that are not about a specification limit.
public enum AnalysisSpecificationField
{
    LowerSpecificationLimit,
    UpperSpecificationLimit
}

// One reason a configuration cannot be used. Role and WorksheetColumnId identify what to correct; the UI turns this
// into readable text.
public sealed record AnalysisValidationError(
    AnalysisValidationReason Reason,
    AnalysisColumnRole? Role = null,
    Guid? WorksheetColumnId = null,
    AnalysisSpecificationField? Field = null);

public sealed record AnalysisValidationResult(IReadOnlyList<AnalysisValidationError> Errors)
{
    public static readonly AnalysisValidationResult Valid = new([]);

    public bool IsValid => Errors.Count == 0;
}

// Checks an analysis configuration against the worksheet's column metadata: at least one variable, no variable twice,
// every column still part of this worksheet, and a data type each role accepts. It never reads worksheet values and
// has no UI dependency.
public sealed class AnalysisConfigurationValidator
{
    public AnalysisValidationResult Validate(AnalysisConfiguration configuration, IReadOnlyList<WorksheetColumn> worksheetColumns)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(worksheetColumns);

        var errors = new List<AnalysisValidationError>();
        var columnsById = worksheetColumns
            .GroupBy(column => column.Id)
            .ToDictionary(group => group.Key, group => group.First());

        if (configuration.VariableColumnIds.Count == 0)
        {
            errors.Add(new AnalysisValidationError(AnalysisValidationReason.NoVariableSelected, AnalysisColumnRole.Variable));
        }

        var seen = new HashSet<Guid>();
        foreach (var columnId in configuration.VariableColumnIds)
        {
            if (!seen.Add(columnId))
            {
                AddOnce(errors, new AnalysisValidationError(
                    AnalysisValidationReason.DuplicateVariable, AnalysisColumnRole.Variable, columnId));
                continue;
            }

            Check(errors, configuration, columnsById, AnalysisColumnRole.Variable, columnId);
        }

        if (configuration.GroupColumnId is { } groupColumnId)
        {
            Check(errors, configuration, columnsById, AnalysisColumnRole.Group, groupColumnId);
        }

        return errors.Count == 0 ? AnalysisValidationResult.Valid : new AnalysisValidationResult(errors);
    }

    private static void Check(
        List<AnalysisValidationError> errors,
        AnalysisConfiguration configuration,
        Dictionary<Guid, WorksheetColumn> columnsById,
        AnalysisColumnRole role,
        Guid columnId)
    {
        if (!columnsById.TryGetValue(columnId, out var column))
        {
            errors.Add(new AnalysisValidationError(AnalysisValidationReason.ColumnNotFound, role, columnId));
            return;
        }

        if (column.WorksheetId != configuration.WorksheetId)
        {
            errors.Add(new AnalysisValidationError(AnalysisValidationReason.ColumnFromAnotherWorksheet, role, columnId));
            return;
        }

        if (!AnalysisColumnRoles.Allows(role, column.DataType))
        {
            errors.Add(new AnalysisValidationError(AnalysisValidationReason.IncompatibleDataType, role, columnId));
        }
    }

    private static void AddOnce(List<AnalysisValidationError> errors, AnalysisValidationError error)
    {
        if (!errors.Contains(error))
        {
            errors.Add(error);
        }
    }
}
