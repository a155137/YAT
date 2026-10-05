using System.Globalization;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Turns the observations of one measured variable into empirical cumulative distributions: it splits them into series,
// sorts each series on its own, collapses repeated values into one step each, and works out the share of the series at
// or below every step.
//
// Every group has its own denominator: a value at the top of a group of ten reaches a hundred percent whether the graph
// has ten observations or a million. Computing one distribution over everything and splitting it afterwards would give
// every group the shape of the whole sample instead of its own.
//
// The percentages use every observation. Only the number of steps that are drawn is capped, and only after the
// distributions have been worked out.
public sealed class EmpiricalCdfRenderModelBuilder
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
    // series in the order they are found.
    public EmpiricalCdfRenderModelBuilder(int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints, GraphSeriesOrder? seriesOrder = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRenderedPoints, 1);
        _maximumRenderedPoints = maximumRenderedPoints;
        _seriesOrder = seriesOrder;
    }

    // A series' index: its place in the whole graph when a panel is built against one, else its place here.
    private int SeriesIndex(string label, int index) => _seriesOrder?.IndexOf(label, index) ?? index;

    // The empirical CDF of these observations, or null when none of them can be plotted. The labels name the worksheet
    // columns; they are given, not looked up.
    public EmpiricalCdfRenderModel? Build(
        UnivariateGraphData data,
        EmpiricalCdfLabels labels,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(labels);
        cancellationToken.ThrowIfCancellationRequested();

        var partition = Split(data, cancellationToken);
        if (partition.ObservationCount == 0)
        {
            return null;
        }

        var prepared = new List<PreparedSeries>(partition.Series.Count);
        var minimumValue = double.PositiveInfinity;
        var maximumValue = double.NegativeInfinity;
        var uniquePointCount = 0;

        foreach (var buffer in partition.Series)
        {
            var series = Prepare(buffer, cancellationToken);
            prepared.Add(series);
            uniquePointCount += series.Points.Length;

            minimumValue = Math.Min(minimumValue, series.Points[0].Value);
            maximumValue = Math.Max(maximumValue, series.Points[^1].Value);
        }

        // One X axis for every group: the distributions are only comparable when they are read against the same values.
        var horizontal = GraphAxisRanges.FromValues(minimumValue, maximumValue);

        // Only now, with every distribution decided, is the drawing capped.
        var quotas = DisplaySampling.Quotas(
            [.. prepared.Select(series => series.Points.Length)], uniquePointCount, _maximumRenderedPoints);

        var models = new List<EmpiricalCdfSeriesRenderModel>(prepared.Count);
        var legendEntries = new List<GraphLegendEntry>(prepared.Count);
        for (var index = 0; index < prepared.Count; index++)
        {
            if (quotas[index] == 0)
            {
                continue;
            }

            var series = prepared[index];
            models.Add(new EmpiricalCdfSeriesRenderModel(
                series.Label,
                SeriesIndex(series.Label, index),
                Sample(series.Points, quotas[index], cancellationToken),
                series.ObservationCount,
                series.Points.Length));

            legendEntries.Add(new GraphLegendEntry(series.Label, SeriesIndex(series.Label, index)));
        }

        var frame = new GraphRenderModel(
            $"Empirical CDF of {labels.Variable}",
            new GraphAxisModel(horizontal, GraphAxisTicks.Nice(horizontal), labels.AxisTitle ?? labels.Variable),
            PercentAxis.Axis(),
            // A plot without a group column is one unnamed series, and one series needs no legend.
            data.Group is null ? null : new GraphLegendModel(_seriesOrder?.Arrange(legendEntries, entry => entry.SeriesIndex) ?? legendEntries, labels.GroupColumn));

        return new EmpiricalCdfRenderModel(frame, _seriesOrder?.Arrange(models, item => item.SeriesIndex) ?? models, partition.ObservationCount);
    }

    // One series: its own values in ascending order, with repeated values collapsed into a single step that jumps
    // straight to the share of the series at or below that value. The last step is a hundred percent exactly, whatever
    // the division left behind.
    private static PreparedSeries Prepare(SeriesBuffer buffer, CancellationToken cancellationToken)
    {
        var values = buffer.Values.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(values);
        cancellationToken.ThrowIfCancellationRequested();

        var steps = new List<EmpiricalCdfPoint>(Math.Min(values.Length, 1024));
        var index = 0;
        while (index < values.Length)
        {
            if ((index & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var value = values[index];
            do
            {
                index++;
            }
            while (index < values.Length && values[index] == value);

            steps.Add(new EmpiricalCdfPoint(value, index * 100d / values.Length));
        }

        // The division is exact for most sample sizes but not for all of them; the top of a distribution is a hundred.
        steps[^1] = new EmpiricalCdfPoint(steps[^1].Value, PercentAxis.Maximum);

        return new PreparedSeries(buffer.Label, [.. steps], values.Length);
    }

    // The steps a series draws: all of them when it may, otherwise evenly spread ones that keep the first and the last
    // (just the last, when only one step may be drawn). A thinned-out distribution is still a step function - the
    // renderer never joins two steps diagonally - and it always ends at a hundred percent.
    private static ReadOnlyMemory<EmpiricalCdfPoint> Sample(
        EmpiricalCdfPoint[] points,
        int quota,
        CancellationToken cancellationToken)
    {
        if (quota >= points.Length)
        {
            return points;
        }

        // A single step is the final one. DisplaySampling keeps the first point when it may keep only one, which is
        // right for a scatter of points but not for a distribution: an empirical CDF ends at a hundred percent, and a
        // curve cut down to its first step would stop short of it.
        if (quota == 1)
        {
            return new[] { points[^1] };
        }

        var sampled = new EmpiricalCdfPoint[quota];
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

        return partition;
    }

    // One series after its distribution has been worked out.
    private sealed record PreparedSeries(string Label, EmpiricalCdfPoint[] Points, int ObservationCount);

    // The series found so far, in first-observed order.
    private sealed class Partition
    {
        private readonly List<SeriesBuffer> _series = [];

        public IReadOnlyList<SeriesBuffer> Series => _series;

        public int ObservationCount { get; private set; }

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
