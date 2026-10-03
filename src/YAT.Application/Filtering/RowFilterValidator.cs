using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Filtering;

// Why a row filter cannot be used.
public enum RowFilterProblemKind
{
    // More than RowFilter.MaximumConditions conditions.
    TooManyConditions,

    // A condition's column is not among the worksheet's columns (e.g. it was deleted).
    ColumnNotFound,

    // A condition's column belongs to another worksheet: a filter reads the rows of one worksheet.
    ColumnFromAnotherWorksheet,

    // A condition's column is neither Numeric nor String, or not the data type its values are of.
    ColumnIncompatibleType,

    // A value-set condition with nothing selected - no value and not Missing.
    SelectionEmpty
}

// One problem of a row filter: what, which condition (none for the filter as a whole) and which column.
public sealed record RowFilterProblem(RowFilterProblemKind Kind, int? ConditionIndex = null, Guid? ColumnId = null);

// The rules a row filter obeys against the worksheet's column metadata (Task #053) - never its values - for graphs and
// analyses alike: at most RowFilter.MaximumConditions conditions, and for each a Numeric or String column of the
// worksheet, conditions of that column's data type, and something selected. Whether the values still occur in the
// column cannot be known here; a condition no row meets simply keeps no row. The numbers and text of a condition were
// checked when it was made (finite numbers, Lower not above Upper, text not blank).
public static class RowFilterValidator
{
    public static IReadOnlyList<RowFilterProblem> Validate(
        RowFilter filter,
        Guid worksheetId,
        IReadOnlyDictionary<Guid, WorksheetColumn> columnsById)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(columnsById);

        var problems = new List<RowFilterProblem>();
        if (filter.Conditions.Count > RowFilter.MaximumConditions)
        {
            problems.Add(new RowFilterProblem(RowFilterProblemKind.TooManyConditions));
        }

        for (var index = 0; index < filter.Conditions.Count; index++)
        {
            if (Check(filter.Conditions[index], worksheetId, columnsById) is { } kind)
            {
                problems.Add(new RowFilterProblem(kind, index, filter.Conditions[index].ColumnId));
            }
        }

        return problems;
    }

    private static RowFilterProblemKind? Check(RowFilterCondition condition, Guid worksheetId, IReadOnlyDictionary<Guid, WorksheetColumn> columnsById)
    {
        if (!columnsById.TryGetValue(condition.ColumnId, out var column))
        {
            return RowFilterProblemKind.ColumnNotFound;
        }

        if (column.WorksheetId != worksheetId)
        {
            return RowFilterProblemKind.ColumnFromAnotherWorksheet;
        }

        if (column.DataType is not (WorksheetDataType.Numeric or WorksheetDataType.String) || column.DataType != condition.DataType)
        {
            return RowFilterProblemKind.ColumnIncompatibleType;
        }

        return condition is ValueSetCondition { IsEmpty: true } ? RowFilterProblemKind.SelectionEmpty : null;
    }
}
