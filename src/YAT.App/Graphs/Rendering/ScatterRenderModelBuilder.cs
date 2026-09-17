using System.Globalization;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Turns the observations of a scatter plot into something that can be drawn: it splits them into series, decides the
// axis ranges and ticks, caps how many points are drawn, and assembles the render model.
//
// This is the whole mapping from graph data to presentation, and the only place that does it. The graph window, the
// canvas and the Graph menu controller have none of it; the graph data it reads is never changed.
public sealed class ScatterRenderModelBuilder
{
    // The most points a scatter plot draws. It is a hard cap: beyond it the model carries a deterministic sample, so
    // the drawing stays responsive no matter how many rows the worksheet has.
    public const int DefaultMaximumRenderedPoints = 100_000;

    // The series of observations whose group value is empty. They are plotted, never dropped.
    public const string MissingGroupLabel = "(Missing)";

    // Group values of a numeric group column, and nothing else, are formatted with this.
    private const string GroupValueFormat = "0.####";

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    private readonly int _maximumRenderedPoints;

    public ScatterRenderModelBuilder(int maximumRenderedPoints = DefaultMaximumRenderedPoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumRenderedPoints, 1);
        _maximumRenderedPoints = maximumRenderedPoints;
    }

    // The scatter plot of these observations, or null when none of them can be plotted. The labels name the worksheet
    // columns; they are given, not looked up.
    public ScatterRenderModel? Build(ScatterGraphData data, ScatterPlotLabels labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(labels);
        cancellationToken.ThrowIfCancellationRequested();

        var partition = Split(data, cancellationToken);
        if (partition.PointCount == 0)
        {
            return null;
        }

        var quotas = Quotas(partition.Series, partition.PointCount);

        var series = new List<ScatterSeriesRenderModel>(partition.Series.Count);
        var legendEntries = new List<GraphLegendEntry>(partition.Series.Count);
        for (var index = 0; index < partition.Series.Count; index++)
        {
            if (quotas[index] == 0)
            {
                continue;
            }

            var buffer = partition.Series[index];
            series.Add(new ScatterSeriesRenderModel(buffer.Label, index, Sample(buffer, quotas[index], cancellationToken)));
            legendEntries.Add(new GraphLegendEntry(buffer.Label, index));
        }

        // The ranges come from every observation, not from the sample: what the graph shows must not depend on how many
        // of its points are drawn.
        var x = GraphAxisRanges.FromValues(partition.MinimumX, partition.MaximumX);
        var y = GraphAxisRanges.FromValues(partition.MinimumY, partition.MaximumY);

        var frame = new GraphRenderModel(
            $"Scatterplot of {labels.YColumn} vs {labels.XColumn}",
            new GraphAxisModel(x, GraphAxisTicks.Nice(x), labels.XColumn),
            new GraphAxisModel(y, GraphAxisTicks.Nice(y), labels.YColumn),
            // A plot without a group column is one unnamed series, and one series needs no legend.
            data.Group is null ? null : new GraphLegendModel(legendEntries, labels.GroupColumn));

        return new ScatterRenderModel(frame, series, partition.PointCount);
    }

    // One pass over the observations: every plottable one joins its group's series, in worksheet row order, and the
    // extremes of both axes are collected on the way.
    //
    // X[i], Y[i] and Group[i] are read at the same index, which is the same worksheet row (the data pipeline aligns
    // them); the values are never filtered separately.
    private static Partition Split(ScatterGraphData data, CancellationToken cancellationToken)
    {
        var partition = new Partition();
        var x = data.XValues.Span;
        var y = data.YValues.Span;
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
            var xValue = x[row];
            var yValue = y[row];
            if (!double.IsFinite(xValue) || !double.IsFinite(yValue))
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

            series.Add(new ScatterPoint(xValue, yValue));
            partition.Extend(xValue, yValue);
        }

        return partition;
    }

    // How many points each series may draw, spending a fixed budget: never more than the cap in total, and as even a
    // share of it as the series sizes allow.
    private int[] Quotas(IReadOnlyList<SeriesBuffer> series, int pointCount)
    {
        var quotas = new int[series.Count];

        if (pointCount <= _maximumRenderedPoints)
        {
            for (var index = 0; index < series.Count; index++)
            {
                quotas[index] = series[index].Count;
            }

            return quotas;
        }

        // More groups than the cap allows points: a categorical case no scatter plot can show anyway. The first groups
        // in first-observed order get one point each, and the cap still holds.
        if (series.Count >= _maximumRenderedPoints)
        {
            for (var index = 0; index < _maximumRenderedPoints; index++)
            {
                quotas[index] = 1;
            }

            return quotas;
        }

        // Every series keeps one point first, so no group disappears; the rest of the budget follows the sizes of the
        // series, and what rounding leaves over goes to the largest remainders (ties in first-observed order).
        var budget = _maximumRenderedPoints - series.Count;
        var pool = pointCount - series.Count;
        var remainders = new (double Fraction, int Index)[series.Count];
        var granted = 0;

        for (var index = 0; index < series.Count; index++)
        {
            var exact = budget * (double)(series[index].Count - 1) / pool;
            var whole = (int)exact;
            quotas[index] = 1 + whole;
            granted += whole;
            remainders[index] = (exact - whole, index);
        }

        var leftover = budget - granted;
        if (leftover > 0)
        {
            Array.Sort(remainders, (first, second) =>
            {
                var byFraction = second.Fraction.CompareTo(first.Fraction);
                return byFraction != 0 ? byFraction : first.Index.CompareTo(second.Index);
            });

            foreach (var (_, index) in remainders)
            {
                if (leftover == 0)
                {
                    break;
                }

                if (quotas[index] >= series[index].Count)
                {
                    continue;
                }

                quotas[index]++;
                leftover--;
            }
        }

        return quotas;
    }

    // The points a series contributes: all of them when it may draw all of them, otherwise evenly spread indexes that
    // keep the first and the last observation of the series. Index arithmetic only, so the same data always samples the
    // same way - at every window size, on every redraw and in every theme.
    private static ReadOnlyMemory<ScatterPoint> Sample(SeriesBuffer series, int quota, CancellationToken cancellationToken)
    {
        if (quota >= series.Count)
        {
            return series.Points;
        }

        var sampled = new ScatterPoint[quota];
        if (quota == 1)
        {
            sampled[0] = series[0];
            return sampled;
        }

        for (var index = 0; index < quota; index++)
        {
            if ((index & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            sampled[index] = series[(int)((long)index * (series.Count - 1) / (quota - 1))];
        }

        return sampled;
    }

    // The series found so far, in first-observed order, with the extremes of the plotted values.
    private sealed class Partition
    {
        private readonly List<SeriesBuffer> _series = [];

        public IReadOnlyList<SeriesBuffer> Series => _series;

        public int PointCount { get; private set; }

        public double MinimumX { get; private set; } = double.PositiveInfinity;

        public double MaximumX { get; private set; } = double.NegativeInfinity;

        public double MinimumY { get; private set; } = double.PositiveInfinity;

        public double MaximumY { get; private set; } = double.NegativeInfinity;

        public SeriesBuffer Add(string label)
        {
            var series = new SeriesBuffer(label);
            _series.Add(series);
            return series;
        }

        public void Extend(double x, double y)
        {
            PointCount++;
            MinimumX = Math.Min(MinimumX, x);
            MaximumX = Math.Max(MaximumX, x);
            MinimumY = Math.Min(MinimumY, y);
            MaximumY = Math.Max(MaximumY, y);
        }
    }

    // Growable buffer of one series' points, in the order the worksheet rows were read.
    private sealed class SeriesBuffer(string label)
    {
        private ScatterPoint[] _items = new ScatterPoint[64];

        public string Label { get; } = label;

        public int Count { get; private set; }

        public ReadOnlyMemory<ScatterPoint> Points => new(_items, 0, Count);

        public ScatterPoint this[int index] => _items[index];

        public void Add(ScatterPoint point)
        {
            if (Count == _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
            }

            _items[Count++] = point;
        }
    }
}
