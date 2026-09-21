using YAT.Analytics.Statistics;
using YAT.Application.Analyses;

namespace YAT.app.Analyses;

// Turns the worksheet rows of a capability analysis into a result table: it splits the rows into groups, walks each
// variable once in worksheet row order, and hands what it collected to YAT.Analytics.
//
// The walk is the whole point. Worksheet row order is test order, so the observations are never sorted and never
// compacted: each group keeps its own moving-range sequence, a row of another group does not interrupt it, and a row
// of this group without a value does - two measurements either side of a gap were not taken one after the other.
//
// The calculation does not depend on what the result shows: every index the data supports is computed, and the
// configuration only decides which columns the table gets.
//
// It computes no statistic itself (the mean, the moving-range sigma and the indices are Analytics) and knows nothing
// about windows, dialogs, repositories or DuckDB.
public sealed class CapabilityAnalysisBuilder
{
    private const string TitlePrefix = "Capability Analysis";

    // Cancellation is checked every this many rows (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    // One row per variable, or per variable and observed group. An observed group stays in the table even when a
    // variable has no value in it (N = 0).
    public AnalysisResultTable Build(
        AnalysisData data,
        CapabilityAnalysisConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(configuration);
        cancellationToken.ThrowIfCancellationRequested();

        var groups = AnalysisGroups.Split(data, cancellationToken);
        var isGrouped = data.Group is not null;
        var specifications = configuration.Variables.ToDictionary(variable => variable.WorksheetColumnId);
        var statistics = Shown(configuration);
        var rows = new List<AnalysisResultRow>(data.Variables.Count * groups.Count);

        // One buffer for every variable and group: each group's observations are gathered into its own segment of it,
        // in worksheet row order, and overwritten by the next variable.
        var buffer = new double[data.RowCount];
        var filled = new int[groups.Count];
        var estimators = new MovingRangeSigma[groups.Count];

        foreach (var variable in data.Variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Walk(variable, groups, buffer, filled, estimators, cancellationToken);

            var specification = specifications.GetValueOrDefault(variable.Column.ColumnId);
            for (var group = 0; group < groups.Count; group++)
            {
                var observations = buffer.AsSpan(groups.Offsets[group], filled[group]);
                var summary = CapabilitySummary.Compute(
                    filled[group],
                    groups.RowCounts[group] - filled[group],
                    observations.IsEmpty ? null : Descriptives.Mean(observations),
                    estimators[group].StandardDeviation,
                    specification?.LowerSpecificationLimit,
                    specification?.UpperSpecificationLimit);

                rows.Add(Row(variable, isGrouped ? groups.Labels[group] : null, summary, statistics));
            }
        }

        return new AnalysisResultTable(Title(data), Columns(isGrouped, statistics), rows);
    }

    // One pass over the worksheet rows of one variable, in order: every value joins its group's observations and its
    // group's moving-range sequence, and a row of that group without a value breaks that sequence.
    //
    // Rows of other groups are simply not offered to this group's estimator, so they never interrupt it: SITE 1's
    // measurements are consecutive SITE 1 measurements, whatever was measured on another site in between.
    private static void Walk(
        AnalysisVariableData variable,
        AnalysisGroups groups,
        double[] buffer,
        int[] filled,
        MovingRangeSigma[] estimators,
        CancellationToken cancellationToken)
    {
        Array.Clear(filled);
        for (var group = 0; group < estimators.Length; group++)
        {
            estimators[group] = new MovingRangeSigma();
        }

        var values = variable.Values.Span;
        for (var row = 0; row < values.Length; row++)
        {
            if ((row & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var group = groups.IndexOf(row);

            // The raw store rejects non-finite numbers, so the finite test is a guard, not a filter with data behind it.
            if (values[row] is { } value && double.IsFinite(value))
            {
                buffer[groups.Offsets[group] + filled[group]++] = value;
                estimators[group].Add(value);
            }
            else
            {
                estimators[group].Break();
            }
        }
    }

    private static AnalysisResultRow Row(
        AnalysisVariableData variable,
        string? groupLabel,
        CapabilitySummary summary,
        IReadOnlyList<CapabilityStatistic> statistics)
    {
        var cells = new List<string>(statistics.Count + 2) { variable.Column.Name };
        if (groupLabel is not null)
        {
            cells.Add(groupLabel);
        }

        foreach (var statistic in statistics)
        {
            cells.Add(Cell(summary, statistic));
        }

        return new AnalysisResultRow(cells);
    }

    private static string Cell(CapabilitySummary summary, CapabilityStatistic statistic) => statistic switch
    {
        CapabilityStatistic.Count => AnalysisNumberFormat.Count(summary.Count),
        CapabilityStatistic.Missing => AnalysisNumberFormat.Count(summary.MissingCount),
        CapabilityStatistic.Mean => AnalysisNumberFormat.Statistic(summary.Mean),
        CapabilityStatistic.WithinStandardDeviation => AnalysisNumberFormat.Statistic(summary.WithinStandardDeviation),
        CapabilityStatistic.LowerSpecificationLimit => AnalysisNumberFormat.Statistic(summary.LowerSpecificationLimit),
        CapabilityStatistic.UpperSpecificationLimit => AnalysisNumberFormat.Statistic(summary.UpperSpecificationLimit),
        CapabilityStatistic.Cp => AnalysisNumberFormat.Statistic(summary.Cp),
        CapabilityStatistic.Cpl => AnalysisNumberFormat.Statistic(summary.Cpl),
        CapabilityStatistic.Cpu => AnalysisNumberFormat.Statistic(summary.Cpu),
        CapabilityStatistic.Cpk => AnalysisNumberFormat.Statistic(summary.Cpk),
        _ => string.Empty
    };

    // The chosen statistics in the order a result shows them, whatever order they were chosen in.
    private static IReadOnlyList<CapabilityStatistic> Shown(CapabilityAnalysisConfiguration configuration) =>
        [.. CapabilityAnalysisConfiguration.SelectableStatistics.Where(configuration.Shows)];

    // Variable, and Group when the analysis groups, are structural: they say which result a row is, so they are always
    // there. The rest is what the configuration asked for.
    private static IReadOnlyList<AnalysisResultColumn> Columns(bool isGrouped, IReadOnlyList<CapabilityStatistic> statistics)
    {
        var columns = new List<AnalysisResultColumn>(statistics.Count + 2)
        {
            new("Variable", AnalysisResultAlignment.Left)
        };

        if (isGrouped)
        {
            columns.Add(new AnalysisResultColumn("Group", AnalysisResultAlignment.Left));
        }

        foreach (var statistic in statistics)
        {
            columns.Add(new AnalysisResultColumn(Name(statistic), AnalysisResultAlignment.Right));
        }

        return columns;
    }

    // The heading each statistic is shown under. "Within StDev" is the moving-range estimate, never the sample
    // standard deviation a descriptive statistics table calls "StDev".
    public static string Name(CapabilityStatistic statistic) => statistic switch
    {
        CapabilityStatistic.Count => "N",
        CapabilityStatistic.Missing => "Missing",
        CapabilityStatistic.Mean => "Mean",
        CapabilityStatistic.WithinStandardDeviation => "Within StDev",
        CapabilityStatistic.LowerSpecificationLimit => "LSL",
        CapabilityStatistic.UpperSpecificationLimit => "USL",
        CapabilityStatistic.Cp => "Cp",
        CapabilityStatistic.Cpl => "Cpl",
        CapabilityStatistic.Cpu => "Cpu",
        CapabilityStatistic.Cpk => "Cpk",
        _ => statistic.ToString()
    };

    private static string Title(AnalysisData data) =>
        $"{TitlePrefix}: {string.Join(", ", data.Variables.Select(variable => variable.Column.Name))}";
}
