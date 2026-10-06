using System.Globalization;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.app.Analyses;

namespace YAT.app.Graphs.Rendering;

// Works out the statistics panel of a graph from the graph data the graph was prepared from: Mean, sample standard
// deviation and N of every series, and (Task #062) its Min, Q1, Median, Q3 and Max. It is the only place the panel's
// numbers come from, so a histogram, a probability plot and an empirical CDF of the same data show the same statistics.
//
// The statistics are YAT.Analytics' own (Descriptives), over the observations the graph uses: rows without a value
// were dropped when the data was read, so they are not counted; a value that is not finite is skipped, as every graph
// skips it; a row without a group value belongs to "(Missing)".
// Series follow the graph's own order - groups in the order they were first observed, or for a numeric group column from
// the smallest value up with "(Missing)" last (GraphGroupOrder) - and keep its colours.
//
// Nothing is read again from storage: an ungrouped graph is summarised where its values are, and a grouped one in one
// pass that gathers each group's values together. Only the five-number summary sorts - the group's gathered values, or
// a copy of an ungrouped graph's - after Mean and StDev are worked out as they always were.
public static class GraphStatisticsPanelBuilder
{
    public const string Title = "Statistics";

    // The group of observations whose group value is empty, as every graph calls it.
    public const string MissingGroupLabel = "(Missing)";

    // Why a graph whose statistics cannot be worked out as numbers is not drawn (Task #063.1).
    public const string StatisticsTooLargeMessage = "Data values are too large to calculate this graph's statistics.";

    // How a standard deviation that does not exist (a single observation) is shown.
    public const string UndefinedText = "—";

    // Group values of a numeric group column are labelled the way the graphs label them.
    private const string GroupValueFormat = "0.####";

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    // The frame of a prepared graph with its statistics panel when the graph type offers one, unchanged otherwise. The
    // graph preparation calls this, so every graph gets its panel the same way. The panel is worked out whether or not
    // it is to be shown (Task #045): whether it is, and with which statistics, is decided afterwards from the frame
    // alone (GraphStatisticsPresentationBuilder), so a hidden panel can be shown again without the data.
    public static GraphRenderModel Attach(
        GraphRenderModel frame,
        GraphData data,
        GraphTypeDefinition definition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(definition);

        // A frame that has its statistics already keeps them: a graph in panels comes with its panels' own (Task #062).
        if (!definition.Supports(GraphCapability.StatisticsPanel) || frame.StatisticsPanel is not null
            || data is not UnivariateGraphData univariate)
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
            // Mean, StDev and N first, from the values in row order as always; then the five-number summary, from the
            // same values sorted in place - this buffer is the builder's own (Task #062).
            cancellationToken.ThrowIfCancellationRequested();
            var series = buffer.AsSpan(offsets[group], counts[group]);
            rows[group] = Row(labels[group], group, series) with { FiveNumbers = FiveNumbers(series) };
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

        // The five-number summary from a sorted copy: these values are the graph data's own and stay as they are
        // (Task #062).
        var row = Row(string.Empty, null, values) with { FiveNumbers = FiveNumbers(values.ToArray()) };
        return new GraphStatisticsPanel(Title, null, [row]);
    }

    // Min, Q1, Median, Q3 and Max of one series (Task #062), WHICH SORTS THE VALUES IN PLACE: Analytics' descriptive
    // summary, so the quartiles are those of the Descriptive Statistics table and the box plot (R-7).
    private static GraphFiveNumberSummary FiveNumbers(Span<double> values)
    {
        var summary = DescriptiveSummary.ComputeInPlaceSorting(values, 0);
        return new GraphFiveNumberSummary(
            summary.Minimum!.Value,
            summary.FirstQuartile!.Value,
            summary.Median!.Value,
            summary.ThirdQuartile!.Value,
            summary.Maximum!.Value,
            AnalysisNumberFormat.Statistic(summary.Minimum),
            AnalysisNumberFormat.Statistic(summary.FirstQuartile),
            AnalysisNumberFormat.Statistic(summary.Median),
            AnalysisNumberFormat.Statistic(summary.ThirdQuartile),
            AnalysisNumberFormat.Statistic(summary.Maximum));
    }

    // One series: its Mean, its sample standard deviation when it has one, and N - the statistics of Analytics.
    private static GraphStatisticsRow Row(string label, int? seriesIndex, ReadOnlySpan<double> values)
    {
        var mean = Descriptives.Mean(values);
        double? standardDeviation = values.Length >= 2 ? Descriptives.StandardDeviation(values) : null;

        // Finite values can still have a standard deviation beyond a double (Task #063.1): the graph is refused with a
        // message, as for a range too large to draw, rather than failing.
        if (!double.IsFinite(mean) || standardDeviation is { } spread && !double.IsFinite(spread))
        {
            throw new GraphPreparationException(StatisticsTooLargeMessage);
        }

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

    // The groups in the order the graph draws them, the group of every observation (-1 for a value that is not finite),
    // and how many observations each holds - the same groups, labels and colours as the graph's own series.
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

        // Numeric groups from the smallest up, "(Missing)" last; text groups as first seen (GraphGroupOrder, Task #059).
        if (byNumber is not null)
        {
            var order = GraphGroupOrder.Order(labels.Count, byNumber, missing);
            var placeOf = new int[order.Length];
            for (var place = 0; place < order.Length; place++)
            {
                placeOf[order[place]] = place;
            }

            for (var row = 0; row < indexByRow.Length; row++)
            {
                if (indexByRow[row] >= 0)
                {
                    indexByRow[row] = placeOf[indexByRow[row]];
                }
            }

            return ([.. order.Select(index => labels[index])], indexByRow, [.. order.Select(index => counts[index])]);
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
