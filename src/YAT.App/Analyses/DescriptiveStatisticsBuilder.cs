using YAT.Analytics.Statistics;
using YAT.Application.Analyses;

namespace YAT.app.Analyses;

// Turns the worksheet rows of an analysis into a descriptive statistics table: it splits the rows into groups, hands
// each variable's observations of each group to YAT.Analytics, and lays the summaries out as rows.
//
// This is the whole mapping from analysis data to a result, and the only place that does it. It computes no statistic
// itself - mean, sample standard deviation and the R-7 quartiles are Analytics - and it knows nothing about windows,
// dialogs, repositories or DuckDB: give it data, get a table.
//
// Row alignment is respected throughout: Values[i] and Group[i] are read at the same index, which is the same
// worksheet row, and neither is compacted before the rows are split.
public sealed class DescriptiveStatisticsBuilder
{
    // The group of rows whose group value is empty. They are summarised, never dropped.
    public const string MissingGroupLabel = "(Missing)";

    private const string TitlePrefix = "Descriptive Statistics";

    // Cancellation is checked every this many rows (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    // One row per variable, or per variable and observed group. An observed group stays in the table even when a
    // variable has no value in it (N = 0): the group exists because worksheet rows have it, not because this variable
    // was measured there.
    public AnalysisResultTable Build(AnalysisData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        var groups = Split(data, cancellationToken);
        var isGrouped = data.Group is not null;
        var rows = new List<AnalysisResultRow>(data.Variables.Count * groups.Count);

        // One buffer for every variable and group: each group's observations are gathered into its own segment of it,
        // sorted there by Analytics, and overwritten by the next variable.
        var buffer = new double[data.RowCount];
        var filled = new int[groups.Count];

        foreach (var variable in data.Variables)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Gather(variable, groups, buffer, filled, cancellationToken);

            for (var group = 0; group < groups.Count; group++)
            {
                var observations = buffer.AsSpan(groups.Offsets[group], filled[group]);
                var summary = DescriptiveSummary.ComputeInPlaceSorting(observations, groups.RowCounts[group] - filled[group]);
                rows.Add(Row(variable, isGrouped ? groups.Labels[group] : null, summary));
            }
        }

        return new AnalysisResultTable(Title(data), Columns(isGrouped), rows);
    }

    // The observations of one variable, gathered into the segment of the group each of them belongs to. A row without
    // a value is not gathered; it is counted as missing by the difference between the group's rows and what was
    // gathered, which is why no row is ever silently dropped.
    private static void Gather(
        AnalysisVariableData variable,
        Groups groups,
        double[] buffer,
        int[] filled,
        CancellationToken cancellationToken)
    {
        Array.Clear(filled);

        var values = variable.Values.Span;
        for (var row = 0; row < values.Length; row++)
        {
            if ((row & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // The raw store rejects non-finite numbers, so this is a guard, not a filter with data behind it.
            if (values[row] is not { } value || !double.IsFinite(value))
            {
                continue;
            }

            var group = groups.IndexByRow is { } indexes ? indexes[row] : 0;
            buffer[groups.Offsets[group] + filled[group]++] = value;
        }
    }

    // The groups of the worksheet rows, in the order they are first observed, with the rows each of them holds. Every
    // row belongs to exactly one group, so the group row counts add up to the worksheet's rows - which is what makes
    // N + Missing meaningful per group.
    //
    // Without a group column there is one unnamed group holding every row.
    private static Groups Split(AnalysisData data, CancellationToken cancellationToken)
    {
        if (data.Group is not { } group)
        {
            return Groups.Single(data.RowCount);
        }

        var labels = new List<string>();
        var rowCounts = new List<int>();
        var indexByRow = new int[data.RowCount];
        var textGroups = group is StringAnalysisGroupData text ? text.Values.Span : default;
        var numericGroups = group is NumericAnalysisGroupData numeric ? numeric.Values.Span : default;

        Dictionary<string, int>? byText = null;
        Dictionary<double, int>? byNumber = null;
        var missing = -1;

        for (var row = 0; row < data.RowCount; row++)
        {
            if ((row & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int index;
            if (group.IsMissing(row))
            {
                // A row whose group value is empty keeps its observation and joins "(Missing)", which takes its place
                // in the order like any other group.
                index = missing >= 0 ? missing : missing = Add(labels, rowCounts, MissingGroupLabel);
            }
            else if (!textGroups.IsEmpty)
            {
                var key = textGroups[row]!;
                byText ??= new Dictionary<string, int>(StringComparer.Ordinal);
                if (!byText.TryGetValue(key, out index))
                {
                    index = Add(labels, rowCounts, key);
                    byText.Add(key, index);
                }
            }
            else
            {
                var key = numericGroups[row]!.Value;
                byNumber ??= [];
                if (!byNumber.TryGetValue(key, out index))
                {
                    index = Add(labels, rowCounts, AnalysisNumberFormat.GroupValue(key));
                    byNumber.Add(key, index);
                }
            }

            indexByRow[row] = index;
            rowCounts[index]++;
        }

        return Groups.Observed(labels, rowCounts, indexByRow);
    }

    private static int Add(List<string> labels, List<int> rowCounts, string label)
    {
        labels.Add(label);
        rowCounts.Add(0);
        return labels.Count - 1;
    }

    private static AnalysisResultRow Row(AnalysisVariableData variable, string? groupLabel, DescriptiveSummary summary)
    {
        var cells = new List<string>(11) { variable.Column.Name };
        if (groupLabel is not null)
        {
            cells.Add(groupLabel);
        }

        cells.Add(AnalysisNumberFormat.Count(summary.Count));
        cells.Add(AnalysisNumberFormat.Count(summary.MissingCount));
        cells.Add(AnalysisNumberFormat.Statistic(summary.Mean));
        cells.Add(AnalysisNumberFormat.Statistic(summary.StandardDeviation));
        cells.Add(AnalysisNumberFormat.Statistic(summary.Minimum));
        cells.Add(AnalysisNumberFormat.Statistic(summary.FirstQuartile));
        cells.Add(AnalysisNumberFormat.Statistic(summary.Median));
        cells.Add(AnalysisNumberFormat.Statistic(summary.ThirdQuartile));
        cells.Add(AnalysisNumberFormat.Statistic(summary.Maximum));

        return new AnalysisResultRow(cells);
    }

    // The Group column appears only when the analysis groups: an ungrouped table has nothing to put in it.
    private static IReadOnlyList<AnalysisResultColumn> Columns(bool isGrouped)
    {
        var columns = new List<AnalysisResultColumn>(11)
        {
            new("Variable", AnalysisResultAlignment.Left)
        };

        if (isGrouped)
        {
            columns.Add(new AnalysisResultColumn("Group", AnalysisResultAlignment.Left));
        }

        foreach (var name in (string[])["N", "Missing", "Mean", "StDev", "Min", "Q1", "Median", "Q3", "Max"])
        {
            columns.Add(new AnalysisResultColumn(name, AnalysisResultAlignment.Right));
        }

        return columns;
    }

    private static string Title(AnalysisData data) =>
        $"{TitlePrefix}: {string.Join(", ", data.Variables.Select(variable => variable.Column.Name))}";

    // The groups a table is built over: their labels, how many worksheet rows each holds, where each one's
    // observations are gathered in the shared buffer, and which group every row belongs to (null when there is only
    // one group, because then every row belongs to it).
    private sealed class Groups
    {
        private Groups(IReadOnlyList<string> labels, int[] rowCounts, int[] offsets, int[]? indexByRow)
        {
            Labels = labels;
            RowCounts = rowCounts;
            Offsets = offsets;
            IndexByRow = indexByRow;
        }

        public IReadOnlyList<string> Labels { get; }

        public int[] RowCounts { get; }

        public int[] Offsets { get; }

        public int[]? IndexByRow { get; }

        public int Count => Labels.Count;

        public static Groups Single(int rowCount) => new([string.Empty], [rowCount], [0], null);

        public static Groups Observed(IReadOnlyList<string> labels, IReadOnlyList<int> rowCounts, int[] indexByRow)
        {
            var counts = rowCounts.ToArray();
            var offsets = new int[counts.Length];
            var offset = 0;
            for (var index = 0; index < counts.Length; index++)
            {
                offsets[index] = offset;
                offset += counts[index];
            }

            return new Groups(labels, counts, offsets, indexByRow);
        }
    }
}
