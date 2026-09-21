using YAT.Application.Analyses;

namespace YAT.app.Analyses;

// The groups the worksheet rows of one analysis are split into: their labels, how many rows each of them holds, where
// each one's observations are gathered in a shared buffer, and which group every row belongs to.
//
// One rule, shared by every analysis, so two analyses of the same worksheet never disagree about what the groups are:
// groups appear in the order the worksheet first shows them, a row whose group value is empty joins "(Missing)" and
// keeps its observation, and numeric group values are labelled the way the graphs label them. Every row belongs to
// exactly one group, which is what makes "N + Missing is the group's own rows" true.
//
// Without a group column there is a single unnamed group holding every row.
public sealed class AnalysisGroups
{
    // The group of rows whose group value is empty. They are analysed, never dropped.
    public const string MissingGroupLabel = "(Missing)";

    // Cancellation is checked every this many rows (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    private AnalysisGroups(IReadOnlyList<string> labels, int[] rowCounts, int[] offsets, int[]? indexByRow)
    {
        Labels = labels;
        RowCounts = rowCounts;
        Offsets = offsets;
        IndexByRow = indexByRow;
    }

    public IReadOnlyList<string> Labels { get; }

    // Worksheet rows per group; they add up to the worksheet's rows.
    public int[] RowCounts { get; }

    // Where each group's observations start in a buffer of one entry per worksheet row.
    public int[] Offsets { get; }

    // The group of every row, or null when there is only one group and every row belongs to it.
    public int[]? IndexByRow { get; }

    public int Count => Labels.Count;

    public int IndexOf(int row) => IndexByRow is { } indexes ? indexes[row] : 0;

    public static AnalysisGroups Split(AnalysisData data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (data.Group is not { } group)
        {
            return new AnalysisGroups([string.Empty], [data.RowCount], [0], null);
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

        var counts = rowCounts.ToArray();
        var offsets = new int[counts.Length];
        var offset = 0;
        for (var index = 0; index < counts.Length; index++)
        {
            offsets[index] = offset;
            offset += counts[index];
        }

        return new AnalysisGroups(labels, counts, offsets, indexByRow);
    }

    private static int Add(List<string> labels, List<int> rowCounts, string label)
    {
        labels.Add(label);
        rowCounts.Add(0);
        return labels.Count - 1;
    }
}
