using System.Globalization;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.app.Analyses;

namespace YAT.app.Graphs.Rendering;

// Works out the statistics panel of a graph from the graph data the graph was prepared from: Mean, sample standard
// deviation and N of every series. It is the only place the panel's numbers come from, so a histogram, a probability
// plot and an empirical CDF of the same data show the same statistics.
//
// The statistics are YAT.Analytics' own (Descriptives), over the observations the graph uses: rows without a value
// were dropped when the data was read, so they are not counted; a value that is not finite is skipped, as every graph
// skips it; a row without a group value belongs to "(Missing)".
// Series follow the graph's own order - groups in the order they were first observed - and keep its colours.
//
// Nothing is read again from storage and nothing is sorted: an ungrouped graph is summarised where its values are,
// and a grouped one in one pass that gathers each group's values together.
public static class GraphStatisticsPanelBuilder
{
    public const string Title = "Statistics";

    // The group of observations whose group value is empty, as every graph calls it.
    public const string MissingGroupLabel = "(Missing)";

    // How a standard deviation that does not exist (a single observation) is shown.
    public const string UndefinedText = "—";

    // Group values of a numeric group column are labelled the way the graphs label them.
    private const string GroupValueFormat = "0.####";

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    // The frame to draw for a prepared graph: with the statistics panel when the graph type offers one and the user
    // wants it, unchanged otherwise. The graph preparation calls this, so every graph gets its panel the same way.
    public static GraphRenderModel Attach(
        GraphRenderModel frame,
        GraphData data,
        GraphTypeDefinition definition,
        GraphPresentationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        if (!definition.Supports(GraphCapability.StatisticsPanel) || !options.ShowStatistics || data is not UnivariateGraphData univariate)
        {
            return frame;
        }

        return Build(univariate, cancellationToken) is { } panel ? frame.WithStatisticsPanel(panel) : frame;
    }

    // The panel of these observations, or null when there are none.
    public static GraphStatisticsPanel? Build(UnivariateGraphData data, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();

        if (data.Count == 0)
        {
            return null;
        }

        if (data.Group is null)
        {
            return Ungrouped(data.Values.Span, cancellationToken);
        }

        var (labels, indexByRow, counts) = Groups(data, cancellationToken);
        if (counts.Length == 0)
        {
            return null;
        }

        // Each group's values gathered together, in one buffer, in one pass.
        var offsets = new int[counts.Length];
        for (var group = 1; group < counts.Length; group++)
        {
            offsets[group] = offsets[group - 1] + counts[group - 1];
        }

        var buffer = new double[offsets[^1] + counts[^1]];
        var filled = new int[counts.Length];
        var values = data.Values.Span;
        for (var row = 0; row < values.Length; row++)
        {
            var group = indexByRow[row];
            if (group >= 0)
            {
                buffer[offsets[group] + filled[group]++] = values[row];
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        var rows = new GraphStatisticsRow[counts.Length];
        for (var group = 0; group < counts.Length; group++)
        {
            rows[group] = Row(labels[group], group, buffer.AsSpan(offsets[group], counts[group]));
        }

        return new GraphStatisticsPanel(Title, data.Group.Column.Name, rows);
    }

    // The one series of an ungrouped graph. The values are summarised where they are; only when some of them are not
    // finite - which the raw store does not let happen - are the finite ones copied out first.
    private static GraphStatisticsPanel? Ungrouped(ReadOnlySpan<double> values, CancellationToken cancellationToken)
    {
        var finite = 0;
        foreach (var value in values)
        {
            if (double.IsFinite(value))
            {
                finite++;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (finite == 0)
        {
            return null;
        }

        if (finite < values.Length)
        {
            var kept = new double[finite];
            var next = 0;
            foreach (var value in values)
            {
                if (double.IsFinite(value))
                {
                    kept[next++] = value;
                }
            }

            values = kept;
        }

        return new GraphStatisticsPanel(Title, null, [Row(string.Empty, null, values)]);
    }

    // One series: its Mean, its sample standard deviation when it has one, and N - the statistics of Analytics.
    private static GraphStatisticsRow Row(string label, int? seriesIndex, ReadOnlySpan<double> values)
    {
        var mean = Descriptives.Mean(values);
        double? standardDeviation = values.Length >= 2 ? Descriptives.StandardDeviation(values) : null;

        return new GraphStatisticsRow(
            label,
            seriesIndex,
            values.Length,
            mean,
            standardDeviation,
            AnalysisNumberFormat.Count(values.Length),
            AnalysisNumberFormat.Statistic(mean),
            standardDeviation is null ? UndefinedText : AnalysisNumberFormat.Statistic(standardDeviation));
    }

    // The groups in first-observed order, the group of every observation (-1 for a value that is not finite), and how
    // many observations each holds - the same groups, labels and colours as the graph's own series.
    private static (List<string> Labels, int[] IndexByRow, int[] Counts) Groups(UnivariateGraphData data, CancellationToken cancellationToken)
    {
        var group = data.Group!;
        var labels = new List<string>();
        var counts = new List<int>();
        var indexByRow = new int[data.Count];
        var values = data.Values.Span;
        var textGroups = group is StringGroupData text ? text.Values.Span : default;
        var numericGroups = group is NumericGroupData numeric ? numeric.Values.Span : default;

        Dictionary<string, int>? byText = null;
        Dictionary<double, int>? byNumber = null;
        var missing = -1;

        for (var row = 0; row < data.Count; row++)
        {
            if ((row & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int index;
            if (!double.IsFinite(values[row]))
            {
                indexByRow[row] = -1;
                continue;
            }

            if (group.IsMissing(row))
            {
                index = missing >= 0 ? missing : missing = Add(labels, counts, MissingGroupLabel);
            }
            else if (!textGroups.IsEmpty)
            {
                var key = textGroups[row]!;
                byText ??= new Dictionary<string, int>(StringComparer.Ordinal);
                if (!byText.TryGetValue(key, out index))
                {
                    index = Add(labels, counts, key);
                    byText.Add(key, index);
                }
            }
            else
            {
                var key = numericGroups[row]!.Value;
                byNumber ??= [];
                if (!byNumber.TryGetValue(key, out index))
                {
                    index = Add(labels, counts, key.ToString(GroupValueFormat, CultureInfo.InvariantCulture));
                    byNumber.Add(key, index);
                }
            }

            indexByRow[row] = index;
            counts[index]++;
        }

        return (labels, indexByRow, counts.ToArray());
    }

    private static int Add(List<string> labels, List<int> counts, string label)
    {
        labels.Add(label);
        counts.Add(0);
        return labels.Count - 1;
    }
}
