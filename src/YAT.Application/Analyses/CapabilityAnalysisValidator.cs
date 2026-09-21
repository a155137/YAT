using YAT.Domain.Entities;

namespace YAT.Application.Analyses;

// Checks a capability configuration against the worksheet's column metadata and the rules a specification has to obey.
//
// The column rules (at least one variable, no variable twice, the column still belongs to this worksheet, Numeric
// variables, a Numeric or String group) are the analysis rules every analysis shares, so they are checked by the
// generic validator rather than written again here. What is added is what capability alone requires: every variable
// needs a limit, a two-sided specification has to be the right way round, and a result has to show something.
//
// It never reads worksheet values and has no UI dependency.
public sealed class CapabilityAnalysisValidator
{
    private static readonly AnalysisConfigurationValidator ColumnRules = new();

    public AnalysisValidationResult Validate(
        CapabilityAnalysisConfiguration configuration,
        IReadOnlyList<WorksheetColumn> worksheetColumns)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(worksheetColumns);

        var errors = new List<AnalysisValidationError>(ColumnRules.Validate(configuration.ToAnalysisConfiguration(), worksheetColumns).Errors);

        foreach (var variable in configuration.Variables)
        {
            CheckSpecification(errors, variable);
        }

        if (configuration.DisplayStatistics.Count == 0)
        {
            errors.Add(new AnalysisValidationError(AnalysisValidationReason.NoStatisticSelected));
        }

        return errors.Count == 0 ? AnalysisValidationResult.Valid : new AnalysisValidationResult(errors);
    }

    private static void CheckSpecification(List<AnalysisValidationError> errors, CapabilityVariable variable)
    {
        var lower = Limit(errors, variable, variable.LowerSpecificationLimit, AnalysisSpecificationField.LowerSpecificationLimit);
        var upper = Limit(errors, variable, variable.UpperSpecificationLimit, AnalysisSpecificationField.UpperSpecificationLimit);

        if (lower is null && upper is null)
        {
            // Capability is measured against a specification: without a limit there is nothing to measure against.
            errors.Add(new AnalysisValidationError(
                AnalysisValidationReason.SpecificationLimitMissing, AnalysisColumnRole.Variable, variable.WorksheetColumnId));
            return;
        }

        if (lower is { } lowerLimit && upper is { } upperLimit && lowerLimit >= upperLimit)
        {
            errors.Add(new AnalysisValidationError(
                AnalysisValidationReason.SpecificationLimitsOutOfOrder, AnalysisColumnRole.Variable, variable.WorksheetColumnId));
        }
    }

    // A limit that can be used, or null after reporting why it cannot. NaN and the infinities are not limits.
    private static double? Limit(
        List<AnalysisValidationError> errors,
        CapabilityVariable variable,
        double? limit,
        AnalysisSpecificationField field)
    {
        if (limit is not { } value)
        {
            return null;
        }

        if (double.IsFinite(value))
        {
            return value;
        }

        errors.Add(new AnalysisValidationError(
            AnalysisValidationReason.SpecificationLimitNotNumeric, AnalysisColumnRole.Variable, variable.WorksheetColumnId, field));
        return null;
    }
}
