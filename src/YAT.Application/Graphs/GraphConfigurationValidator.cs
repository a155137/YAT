using YAT.Application.Specifications;
using YAT.Domain.Entities;

namespace YAT.Application.Graphs;

public enum GraphValidationReason
{
    UnknownGraphType,

    // A required role (e.g. the scatter plot's X) has no column.
    MissingRequiredRole,

    // A role the graph type does not have was assigned.
    UnsupportedRole,

    // A role that takes one column was assigned more than once.
    DuplicateRole,

    // A role that takes several columns was given the same column twice.
    DuplicateColumn,

    // The assigned column is not among the worksheet's columns (e.g. it was deleted).
    ColumnNotFound,

    // The assigned column belongs to a different worksheet: V1 graphs read one worksheet.
    ColumnFromAnotherWorksheet,

    // The column's data type is not allowed for the role (e.g. a String column on a Numeric axis).
    IncompatibleDataType,

    // A specification value is not a finite number. Field says which one.
    SpecificationValueNotNumeric,

    // The lower specification limit is not below the upper one.
    SpecificationLimitsOutOfOrder,

    // The target lies below the lower specification limit or above the upper one.
    SpecificationTargetOutsideLimits
}

// One reason a configuration is not valid. Role and WorksheetColumnId identify what to correct - or, for a problem with
// the specification, Field; the UI turns this into readable text.
public sealed record GraphValidationError(
    GraphValidationReason Reason,
    GraphVariableRole? Role = null,
    Guid? WorksheetColumnId = null,
    SpecificationField? Field = null);

public sealed record GraphValidationResult(IReadOnlyList<GraphValidationError> Errors)
{
    public static readonly GraphValidationResult Valid = new([]);

    public bool IsValid => Errors.Count == 0;
}

// Checks a graph configuration against its graph specification and the worksheet's column metadata. It never reads
// worksheet values and has no UI dependency.
public sealed class GraphConfigurationValidator
{
    public GraphValidationResult Validate(GraphConfiguration configuration, IReadOnlyList<WorksheetColumn> worksheetColumns)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(worksheetColumns);

        if (!GraphTypeDefinitions.TryGet(configuration.GraphType, out var definition))
        {
            return new GraphValidationResult([new GraphValidationError(GraphValidationReason.UnknownGraphType)]);
        }

        var errors = new List<GraphValidationError>();
        var columnsById = worksheetColumns
            .GroupBy(column => column.Id)
            .ToDictionary(group => group.Key, group => group.First());

        foreach (var assignment in configuration.Assignments)
        {
            var roleDefinition = definition.FindRole(assignment.Role);
            if (roleDefinition is null)
            {
                errors.Add(new GraphValidationError(GraphValidationReason.UnsupportedRole, assignment.Role));
                continue;
            }

            // Cardinality is read from the role, never from the graph type: a role that takes one column may be
            // assigned once, and a role that takes several may not be given the same column twice.
            if (!roleDefinition.AllowsMultiple && configuration.Assignments.Count(other => other.Role == assignment.Role) > 1)
            {
                AddOnce(errors, new GraphValidationError(GraphValidationReason.DuplicateRole, assignment.Role));
                continue;
            }

            if (roleDefinition.AllowsMultiple && configuration.Assignments.Count(other =>
                    other.Role == assignment.Role && other.WorksheetColumnId == assignment.WorksheetColumnId) > 1)
            {
                AddOnce(errors, new GraphValidationError(
                    GraphValidationReason.DuplicateColumn, assignment.Role, assignment.WorksheetColumnId));
                continue;
            }

            if (!columnsById.TryGetValue(assignment.WorksheetColumnId, out var column))
            {
                errors.Add(new GraphValidationError(GraphValidationReason.ColumnNotFound, assignment.Role, assignment.WorksheetColumnId));
                continue;
            }

            if (column.WorksheetId != configuration.WorksheetId)
            {
                errors.Add(new GraphValidationError(GraphValidationReason.ColumnFromAnotherWorksheet, assignment.Role, column.Id));
                continue;
            }

            if (!roleDefinition.Allows(column.DataType))
            {
                errors.Add(new GraphValidationError(GraphValidationReason.IncompatibleDataType, assignment.Role, column.Id));
            }
        }

        foreach (var required in definition.RequiredRoles)
        {
            if (!configuration.Assignments.Any(assignment => assignment.Role == required.Role))
            {
                errors.Add(new GraphValidationError(GraphValidationReason.MissingRequiredRole, required.Role));
            }
        }

        // A specification is checked only where it is drawn; graph types without the capability ignore it.
        if (definition.Supports(GraphCapability.SpecificationLines))
        {
            errors.AddRange(SpecificationErrors(configuration.Specification));
        }

        return errors.Count == 0 ? GraphValidationResult.Valid : new GraphValidationResult(errors);
    }

    // The shared specification rules, as graph validation errors.
    private static IEnumerable<GraphValidationError> SpecificationErrors(Specification specification) =>
        SpecificationRules.Check(specification).Select(problem => new GraphValidationError(
            problem.Kind switch
            {
                SpecificationProblemKind.NotFinite => GraphValidationReason.SpecificationValueNotNumeric,
                SpecificationProblemKind.LimitsOutOfOrder => GraphValidationReason.SpecificationLimitsOutOfOrder,
                _ => GraphValidationReason.SpecificationTargetOutsideLimits
            },
            Field: problem.Field));

    private static void AddOnce(List<GraphValidationError> errors, GraphValidationError error)
    {
        if (!errors.Contains(error))
        {
            errors.Add(error);
        }
    }
}
