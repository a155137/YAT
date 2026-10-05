using System.Globalization;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Turns the observations of one measured variable into a normal probability plot: it splits them into series, ranks
// each series on its own, gives every observation the normal score of its rank, and draws the line each series' own
// mean and standard deviation describe.
//
// Every group is sorted and ranked by itself. Ranking everything together and splitting afterwards would give each
// group the positions of the whole sample instead of its own, which is a different graph.
//
// The statistics use every observation. Only the drawing is capped, and only after everything has been computed, so a
// sampled plot has exactly the same axes and the same fitted lines as an unsampled one.
public sealed class ProbabilityPlotRenderModelBuilder
{
    // The series of observations whose group value is empty. They are plotted, never dropped.
    public const string MissingGroupLabel = "(Missing)";

    // Group values of a numeric group column, and nothing else, are formatted with this.
    private const string GroupValueFormat = "0.####";

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    private readonly int _maximumRenderedPoints;
    private readonly GraphSeriesOrder? _seriesOrder;

    // seriesOrder: the whole graph's series order, for a panel of a graph drawn in panels (Task #058); none numbers the
    // series in the order they are found (GraphGroupOrder).
    public ProbabilityPlotRenderModelBuilder(int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints, GraphSeriesOrder? seriesOrder = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRenderedPoints, 1);
        _maximumRenderedPoints = maximumRenderedPoints;
        _seriesOrder = seriesOrder;
    }

    // A series' index: its place in the whole graph when a panel is built against one, else its place here.
    private int SeriesIndex(string label, int index) => _seriesOrder?.IndexOf(label, index) ?? index;

    // The probability plot of these observations, or null when none of them can be plotted. The labels name the
    // worksheet columns; they are given, not looked up. With the default options: every series that can have a fitted
    // line has one.
    public ProbabilityPlotRenderModel? Build(
        UnivariateGraphData data,
        ProbabilityPlotLabels labels,
        CancellationToken cancellationToken = default) =>
        Build(data, labels, ProbabilityPlotOptions.Default, cancellationToken);

    // The same, with the probability plot's own options. Without a fitted line, no line is worked out at all - no mean,
    // no standard deviation, no line - and the horizontal axis is chosen from the points alone, because the only reason
    // it reaches further is to show the ends of the lines. The points, their scores, the vertical axis and the sampling
    // are the same either way.
    public ProbabilityPlotRenderModel? Build(
        UnivariateGraphData data,
        ProbabilityPlotLabels labels,
        ProbabilityPlotOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();

        var partition = Split(data, cancellationToken);
        if (partition.ObservationCount == 0)
        {
            return null;
        }

        // Each series on its own: sorted, ranked, scored, and - for its fitted line - summarised by its own mean and
        // standard deviation.
        var prepared = new List<PreparedSeries>(partition.Series.Count);
        var minimumScore = double.PositiveInfinity;
        var maximumScore = double.NegativeInfinity;
        var minimumValue = double.PositiveInfinity;
        var maximumValue = double.NegativeInfinity;

        foreach (var buffer in partition.Series)
        {
            var series = Prepare(buffer, options.ShowFittedLine, cancellationToken);
            prepared.Add(series);

            minimumScore = Math.Min(minimumScore, series.Points[0].Score);
            maximumScore = Math.Max(maximumScore, series.Points[^1].Score);
            minimumValue = Math.Min(minimumValue, series.Points[0].Value);
            maximumValue = Math.Max(maximumValue, series.Points[^1].Value);
        }

        var vertical = ProbabilityAxis.Axis(minimumScore, maximumScore);

        // The lines are drawn across the whole axis, so their ends have to be visible too. Without fitted lines there are
        // none, and nothing but the points decides the horizontal axis.
        var lines = new ProbabilityPlotFittedLine?[prepared.Count];
        for (var index = 0; options.ShowFittedLine && index < prepared.Count; index++)
        {
            var series = prepared[index];
            if (series.Mean is not { } mean || series.StandardDeviation is not { } standardDeviation
                || !double.IsFinite(standardDeviation) || standardDeviation <= 0 || !double.IsFinite(mean))
            {
                // A group with no spread has nothing a straight line could describe.
                continue;
            }

            var line = new ProbabilityPlotFittedLine(mean, standardDeviation, vertical.Range.Minimum, vertical.Range.Maximum);

            lines[index] = line;
            minimumValue = Math.Min(minimumValue, line.FromValue);
            maximumValue = Math.Max(maximumValue, line.ToValue);
        }

        var horizontal = GraphAxisRanges.FromValues(minimumValue, maximumValue);

        // Only now, with every statistic and both axes decided, is the drawing capped.
        var quotas = DisplaySampling.Quotas(
            [.. prepared.Select(series => series.Points.Length)], partition.ObservationCount, _maximumRenderedPoints);

        var models = new List<ProbabilityPlotSeriesRenderModel>(prepared.Count);
        var legendEntries = new List<GraphLegendEntry>(prepared.Count);
        for (var index = 0; index < prepared.Count; index++)
        {
            if (quotas[index] == 0)
            {
                continue;
            }

            var series = prepared[index];
            models.Add(new ProbabilityPlotSeriesRenderModel(
                series.Label,
                SeriesIndex(series.Label, index),
                Sample(series.Points, quotas[index], cancellationToken),
                lines[index],
                series.Points.Length));

            legendEntries.Add(new GraphLegendEntry(series.Label, SeriesIndex(series.Label, index)));
        }

        var frame = new GraphRenderModel(
            $"Normal Probability Plot of {labels.Variable}",
            new GraphAxisModel(horizontal, GraphAxisTicks.Nice(horizontal), labels.AxisTitle ?? labels.Variable),
            vertical,
            // A plot without a group column is one unnamed series, and one series needs no legend.
            data.Group is null ? null : new GraphLegendModel(_seriesOrder?.Arrange(legendEntries, entry => entry.SeriesIndex) ?? legendEntries, labels.GroupColumn));

        return new ProbabilityPlotRenderModel(frame, _seriesOrder?.Arrange(models, item => item.SeriesIndex) ?? models, partition.ObservationCount);
    }

    // One series: its own values in ascending order, each with the normal score of its own rank, and - when its fitted
    // line is wanted - the mean and standard deviation the line is drawn from. Equal values stay separate observations,
    // so each of them gets its own rank and its own point.
    private static PreparedSeries Prepare(SeriesBuffer buffer, bool withSummary, CancellationToken cancellationToken)
    {
        var values = buffer.Values.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(values);
        cancellationToken.ThrowIfCancellationRequested();

        double? mean = withSummary ? Descriptives.Mean(values) : null;
        double? standardDeviation = withSummary ? Descriptives.StandardDeviation(values) : null;
        cancellationToken.ThrowIfCancellationRequested();

        var points = new ProbabilityPlotPoint[values.Length];
        for (var index = 0; index < values.Length; index++)
        {
            if ((index & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var position = ProbabilityPlotPositions.Benard(index + 1, values.Length);
            points[index] = new ProbabilityPlotPoint(values[index], NormalDistribution.InverseCdf(position));
        }

        return new PreparedSeries(buffer.Label, points, mean, standardDeviation);
    }

    private static ReadOnlyMemory<ProbabilityPlotPoint> Sample(
        ProbabilityPlotPoint[] points,
        int quota,
        CancellationToken cancellationToken)
    {
        if (quota >= points.Length)
        {
            return points;
        }

        var sampled = new ProbabilityPlotPoint[quota];
        for (var index = 0; index < quota; index++)
        {
            if ((index & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            sampled[index] = points[DisplaySampling.SampleIndex(index, points.Length, quota)];
        }

        return sampled;
    }

    // One pass over the observations: every plottable one joins its group's series, in worksheet row order.
    //
    // Value[i] and Group[i] are read at the same index, which is the same worksheet row (the data pipeline aligns
    // them); the values are never filtered separately.
    private static Partition Split(UnivariateGraphData data, CancellationToken cancellationToken)
    {
        var partition = new Partition();
        var values = data.Values.Span;
        var group = data.Group;
        var textGroups = group is StringGroupData text ? text.Values.Span : default;
        var numericGroups = group is NumericGroupData numeric ? numeric.Values.Span : default;

        Dictionary<string, SeriesBuffer>? byText = null;
        Dictionary<double, SeriesBuffer>? byNumber = null;
        SeriesBuffer? missing = null;
        SeriesBuffer? ungrouped = null;

        for (var row = 0; row < data.Count; row++)
        {
            if ((row & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // The raw store rejects non-finite numbers, so this is a guard, not a filter with data behind it.
            var value = values[row];
            if (!double.IsFinite(value))
            {
                continue;
            }

            SeriesBuffer series;
            if (group is null)
            {
                series = ungrouped ??= partition.Add(string.Empty);
            }
            else if (group.IsMissing(row))
            {
                series = missing ??= partition.Add(MissingGroupLabel);
            }
            else if (!textGroups.IsEmpty)
            {
                var key = textGroups[row]!;
                byText ??= new Dictionary<string, SeriesBuffer>(StringComparer.Ordinal);
                if (!byText.TryGetValue(key, out var found))
                {
                    found = partition.Add(key);
                    byText.Add(key, found);
                }

                series = found;
            }
            else
            {
                var key = numericGroups[row]!.Value;
                byNumber ??= new Dictionary<double, SeriesBuffer>();
                if (!byNumber.TryGetValue(key, out var found))
                {
                    found = partition.Add(key.ToString(GroupValueFormat, CultureInfo.InvariantCulture));
                    byNumber.Add(key, found);
                }

                series = found;
            }

            series.Add(value);
            partition.Extend();
        }

        // Numeric groups from the smallest up, "(Missing)" last; text groups as first seen (GraphGroupOrder, Task #059).
        partition.Arrange(byNumber, missing);
        return partition;
    }

    // One series after its own statistics have been worked out. Mean and StandardDeviation are null when no fitted line
    // was asked for: nothing else uses them.
    private sealed record PreparedSeries(string Label, ProbabilityPlotPoint[] Points, double? Mean, double? StandardDeviation);

    // The series found so far, in first-observed order until arranged in the order they are drawn (GraphGroupOrder).
    private sealed class Partition
    {
        private readonly List<SeriesBuffer> _series = [];

        public IReadOnlyList<SeriesBuffer> Series => _series;

        public int ObservationCount { get; private set; }

        // The series put in the order they are drawn (GraphGroupOrder).
        public void Arrange(Dictionary<double, SeriesBuffer>? byNumber, SeriesBuffer? missing)
        {
            var ordered = GraphGroupOrder.Arrange(_series, byNumber, missing);
            if (!ReferenceEquals(ordered, _series))
            {
                _series.Clear();
                _series.AddRange(ordered);
            }
        }

        public SeriesBuffer Add(string label)
        {
            var series = new SeriesBuffer(label);
            _series.Add(series);
            return series;
        }

        public void Extend() => ObservationCount++;
    }

    // Growable buffer of one series' values, in the order the worksheet rows were read.
    private sealed class SeriesBuffer(string label)
    {
        private double[] _items = new double[64];

        public string Label { get; } = label;

        public int Count { get; private set; }

        public ReadOnlyMemory<double> Values => new(_items, 0, Count);

        public void Add(double value)
        {
            if (Count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }

            _items[Count++] = value;
        }
    }
}
