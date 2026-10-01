using YAT.Application.Specifications;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

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
    SpecificationTargetOutsideLimits,

    // The histogram's Y scale or binning mode is not a defined choice.
    HistogramOptionsInvalid,

    // Count binning without a whole number of bins in the allowed range.
    HistogramBinCountInvalid,

    // Width-and-start binning without a positive, finite bin width.
    HistogramBinWidthInvalid,

    // Width-and-start binning without a finite bin start.
    HistogramBinStartInvalid,

    // A label is Custom but has no text once it is normalized (see GraphLabelRules). LabelField says which one.
    LabelTextMissing,

    // A label's mode is not a defined choice. LabelField says which one.
    LabelModeInvalid,

    // A role that takes several columns was given more than GraphRoleDefinition.MaximumColumns of them. Role says
    // which one.
    TooManyColumns,

    // An axis range value is not a finite number. Axis and Bound say which one.
    AxisRangeValueNotNumeric,

    // An axis range value lies outside what its axis can show (see GraphAxisKind). Axis and Bound say which one.
    AxisRangeValueOutsideAxis,

    // An axis range whose minimum is not below its maximum. Axis says which one.
    AxisRangeNotIncreasing,

    // An axis range too narrow for its ends to be told apart. Axis says which one.
    AxisRangeTooNarrow,

    // The legend's mode or position is not a defined choice.
    LegendOptionsInvalid,

    // The statistics panel's mode is not a defined choice.
    StatisticsOptionsInvalid,

    // The statistics panel is shown (Auto or Show) but no statistic is chosen for it.
    StatisticsItemsMissing,

    // The grid mode is not a defined choice.
    AppearanceGridModeInvalid,

    // A custom palette without colours, or with more than GraphPalette.MaximumColors.
    AppearancePaletteInvalid,

    // A colour typed for the appearance is not "#RRGGBB".
    AppearanceColorInvalid,

    // A box plot's box width is not a whole number from BoxPlotOptions.MinimumBoxWidthPercent to
    // BoxPlotOptions.MaximumBoxWidthPercent.
    BoxPlotWidthInvalid,

    // The filter's column is not among the worksheet's columns (e.g. it was deleted). WorksheetColumnId says which one.
    FilterColumnNotFound,

    // The filter's column belongs to a different worksheet.
    FilterColumnFromAnotherWorksheet,

    // The filter's column is neither Numeric nor String, or its values are not of the column's data type.
    FilterColumnIncompatibleType,

    // The filter selects nothing: no value and not Missing.
    FilterSelectionEmpty
}

// One reason a configuration is not valid. Role and WorksheetColumnId identify what to correct - or, for a problem with
// the specification, Field, for a problem with a label, LabelField, and for a problem with an axis range, Axis and
// Bound; the UI turns this into readable text.
public sealed record GraphValidationError(
    GraphValidationReason Reason,
    GraphVariableRole? Role = null,
    Guid? WorksheetColumnId = null,
    SpecificationField? Field = null,
    GraphLabelField? LabelField = null,
    GraphAxisField? Axis = null,
    GraphAxisBound? Bound = null);

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

        // A role that takes several columns takes at most GraphRoleDefinition.MaximumColumns of them.
        foreach (var role in definition.Roles.Where(role => role.AllowsMultiple))
        {
            var count = configuration.Assignments.Count(assignment => assignment.Role == role.Role);
            if (count > GraphRoleDefinition.MaximumColumns)
            {
                errors.Add(new GraphValidationError(GraphValidationReason.TooManyColumns, role.Role));
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

        // Likewise the histogram's own options, only where they are read.
        if (definition.Supports(GraphCapability.HistogramControls))
        {
            errors.AddRange(HistogramOptionsRules.Check(configuration.HistogramOptions).Select(problem => new GraphValidationError(problem switch
            {
                HistogramOptionsProblem.BinCountInvalid => GraphValidationReason.HistogramBinCountInvalid,
                HistogramOptionsProblem.BinWidthInvalid => GraphValidationReason.HistogramBinWidthInvalid,
                HistogramOptionsProblem.BinStartInvalid => GraphValidationReason.HistogramBinStartInvalid,
                _ => GraphValidationReason.HistogramOptionsInvalid
            })));
        }

        // And the labels, only where the graph type has them.
        if (definition.Supports(GraphCapability.Labels))
        {
            errors.AddRange(LabelErrors(configuration.LabelOptions));
        }

        // And the axis ranges, only for the axes the graph type lets the user choose.
        if (definition.Supports(GraphCapability.AxisRange))
        {
            errors.AddRange(AxisRangeErrors(configuration.AxisRangeOptions, definition));
        }

        // And the legend, only where the graph type has one.
        if (definition.Supports(GraphCapability.Legend) && !configuration.LegendOptions.IsValid)
        {
            errors.Add(new GraphValidationError(GraphValidationReason.LegendOptionsInvalid));
        }

        // And the statistics panel, only where the graph type has one.
        if (definition.Supports(GraphCapability.StatisticsPanel))
        {
            errors.AddRange(StatisticsErrors(configuration.StatisticsOptions));
        }

        // And the appearance, only where the graph type has one.
        if (definition.Supports(GraphCapability.Appearance))
        {
            errors.AddRange(AppearanceErrors(configuration.AppearanceOptions));
        }

        // And the box plot's own options, only where the graph type has them.
        if (definition.Supports(GraphCapability.BoxPlotControls))
        {
            errors.AddRange(BoxPlotErrors(configuration.BoxPlotOptions));
        }

        // And the filter, for every graph type: which rows a graph reads is not a capability of its own.
        if (configuration.Filter is { } filter)
        {
            errors.AddRange(FilterErrors(filter, configuration.WorksheetId, columnsById));
        }

        return errors.Count == 0 ? GraphValidationResult.Valid : new GraphValidationResult(errors);
    }

    // The appearance rules, as graph validation errors - for a whole configuration here, and for an appearance edited
    // on its own: a defined grid mode, and a custom palette of one to sixteen colours. (A colour itself is always
    // valid: text that is not "#RRGGBB" never becomes one.)
    public static IReadOnlyList<GraphValidationError> AppearanceErrors(GraphAppearanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var errors = new List<GraphValidationError>();
        if (!Enum.IsDefined(options.GridMode))
        {
            errors.Add(new GraphValidationError(GraphValidationReason.AppearanceGridModeInvalid));
        }

        if (options.Palette is { IsValid: false })
        {
            errors.Add(new GraphValidationError(GraphValidationReason.AppearancePaletteInvalid));
        }

        return errors;
    }

    // The filter's rules, against the column metadata only - never its values: a Numeric or String column of the graph's
    // worksheet, values of that column's data type, and something selected. Which roles the column has in the graph does
    // not matter. Whether the selected values still occur in the column cannot be known here; a filter whose values no
    // row has simply keeps no row.
    private static IEnumerable<GraphValidationError> FilterErrors(
        GraphValueFilter filter,
        Guid worksheetId,
        IReadOnlyDictionary<Guid, WorksheetColumn> columnsById)
    {
        if (!columnsById.TryGetValue(filter.ColumnId, out var column))
        {
            yield return new GraphValidationError(GraphValidationReason.FilterColumnNotFound, WorksheetColumnId: filter.ColumnId);
            yield break;
        }

        if (column.WorksheetId != worksheetId)
        {
            yield return new GraphValidationError(GraphValidationReason.FilterColumnFromAnotherWorksheet, WorksheetColumnId: column.Id);
            yield break;
        }

        if (column.DataType is not (WorksheetDataType.Numeric or WorksheetDataType.String) || column.DataType != filter.DataType)
        {
            yield return new GraphValidationError(GraphValidationReason.FilterColumnIncompatibleType, WorksheetColumnId: column.Id);
            yield break;
        }

        if (filter.IsEmpty)
        {
            yield return new GraphValidationError(GraphValidationReason.FilterSelectionEmpty, WorksheetColumnId: column.Id);
        }
    }

    // The box plot options' rules, as graph validation errors - for a whole configuration here, and for the options
    // edited on their own: a box width of 20 to 90 percent.
    public static IReadOnlyList<GraphValidationError> BoxPlotErrors(BoxPlotOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.IsValid ? [] : [new GraphValidationError(GraphValidationReason.BoxPlotWidthInvalid)];
    }

    // The statistics options' rules, as graph validation errors - for a whole configuration here, and for the options
    // edited on their own after a graph is drawn: a defined mode, and at least one statistic unless the panel is
    // hidden.
    public static IReadOnlyList<GraphValidationError> StatisticsErrors(GraphStatisticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.IsModeValid)
        {
            return [new GraphValidationError(GraphValidationReason.StatisticsOptionsInvalid)];
        }

        return options.IsValid ? [] : [new GraphValidationError(GraphValidationReason.StatisticsItemsMissing)];
    }

    // The shared label rules, as graph validation errors - for a whole configuration here, and for labels edited on
    // their own after a graph is drawn, so both are refused for the same reasons in the same words.
    public static IReadOnlyList<GraphValidationError> LabelErrors(GraphLabelOptions options) =>
    [
        .. GraphLabelRules.Check(options).Select(problem => new GraphValidationError(
            problem.Kind == GraphLabelProblemKind.CustomTextMissing
                ? GraphValidationReason.LabelTextMissing
                : GraphValidationReason.LabelModeInvalid,
            LabelField: problem.Field))
    ];

    // The shared axis range rules, as graph validation errors - for a whole configuration here, and for ranges edited
    // on their own after a graph is drawn.
    public static IReadOnlyList<GraphValidationError> AxisRangeErrors(
        GraphAxisRangeOptions options,
        GraphTypeDefinition definition) =>
    [
        .. GraphAxisRangeRules.Check(options, definition).Select(problem => new GraphValidationError(
            problem.Kind switch
            {
                GraphAxisRangeProblemKind.NotFinite => GraphValidationReason.AxisRangeValueNotNumeric,
                GraphAxisRangeProblemKind.OutsideAxis => GraphValidationReason.AxisRangeValueOutsideAxis,
                GraphAxisRangeProblemKind.NotIncreasing => GraphValidationReason.AxisRangeNotIncreasing,
                _ => GraphValidationReason.AxisRangeTooNarrow
            },
            Axis: problem.Axis,
            Bound: problem.Bound))
    ];

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
