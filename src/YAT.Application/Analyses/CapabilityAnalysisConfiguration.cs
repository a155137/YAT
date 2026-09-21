namespace YAT.Application.Analyses;

// The statistics a capability result can show. The calculation produces every one of them that its data supports;
// this only decides which ones a table shows (Variable and Group are structural and always shown).
public enum CapabilityStatistic
{
    Count,
    Missing,
    Mean,
    WithinStandardDeviation,
    LowerSpecificationLimit,
    UpperSpecificationLimit,
    Cp,
    Cpl,
    Cpu,
    Cpk
}

// One measured variable of a capability analysis and the specification it is measured against. Limits are per
// variable, because Reg1 and Reg2 are different measurements with different specifications; a limit that is not
// specified is null, and a variable needs at least one of them.
public sealed record CapabilityVariable(Guid WorksheetColumnId, double? LowerSpecificationLimit, double? UpperSpecificationLimit);

// A capability analysis of one worksheet: the variables with their specifications, the optional column the
// observations are grouped by, and the statistics the result should show.
//
// It is its own configuration rather than a descriptive-statistics one with extras: per-variable specification limits
// and display choices belong to capability, and the generic analysis configuration stays what the data query needs.
// ToAnalysisConfiguration is exactly that generic part, so capability reads its worksheet rows through the same
// query service as every other analysis.
public sealed record CapabilityAnalysisConfiguration(
    Guid WorksheetId,
    IReadOnlyList<CapabilityVariable> Variables,
    Guid? GroupColumnId,
    IReadOnlyList<CapabilityStatistic> DisplayStatistics)
{
    // What a capability result shows until the user says otherwise: how many observations there were, where the
    // process sits and how much it moves. The indices are opt-in, because a specification is only worth reading once
    // the data behind it has been looked at.
    public static readonly IReadOnlyList<CapabilityStatistic> DefaultDisplayStatistics =
    [
        CapabilityStatistic.Count,
        CapabilityStatistic.Mean,
        CapabilityStatistic.WithinStandardDeviation
    ];

    // The statistics a table may offer, in the order a result shows them.
    public static readonly IReadOnlyList<CapabilityStatistic> SelectableStatistics =
    [
        CapabilityStatistic.Count,
        CapabilityStatistic.Missing,
        CapabilityStatistic.Mean,
        CapabilityStatistic.WithinStandardDeviation,
        CapabilityStatistic.LowerSpecificationLimit,
        CapabilityStatistic.UpperSpecificationLimit,
        CapabilityStatistic.Cp,
        CapabilityStatistic.Cpl,
        CapabilityStatistic.Cpu,
        CapabilityStatistic.Cpk
    ];

    // The generic part of this configuration: which worksheet, which columns, which group. The specifications and the
    // display choices are capability's own business and stay here.
    public AnalysisConfiguration ToAnalysisConfiguration() =>
        new(WorksheetId, [.. Variables.Select(variable => variable.WorksheetColumnId)], GroupColumnId);

    public bool Shows(CapabilityStatistic statistic) => DisplayStatistics.Contains(statistic);
}
