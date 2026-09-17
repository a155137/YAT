using System.Globalization;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Turns the observations of one measured variable into a histogram: it splits them into series, works out one set of
// bins for all of them, counts every observation into those bins and assembles the render model.
//
// This is the whole mapping from graph data to presentation, and the only place that does it. The bin-count policy
// (Sturges, Freedman-Diaconis, Scott) is the statistics of YAT.Analytics; where the bins start, how wide they are and
// which bin a value belongs to is decided here, because it is what the graph shows rather than what the data means.
//
// Nothing is sampled: a histogram of a million observations is counted from all of them.
public sealed class HistogramRenderModelBuilder
{
    // The series of observations whose group value is empty. They are counted, never dropped.
    public const string MissingGroupLabel = "(Missing)";

    public const string FrequencyAxisTitle = "Frequency";

    // Group values of a numeric group column, and nothing else, are formatted with this.
    private const string GroupValueFormat = "0.####";

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    // The histogram of these observations, or null when none of them can be counted. The labels name the worksheet
    // columns; they are given, not looked up.
    public HistogramRenderModel? Build(UnivariateGraphData data, HistogramPlotLabels labels, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(labels);
        cancellationToken.ThrowIfCancellationRequested();

        var partition = Split(data, cancellationToken);
        if (partition.ObservationCount == 0)
        {
            return null;
        }

        // One set of bins, worked out from every observation: comparing groups is only meaningful when they are counted
        // into the same intervals.
        var grid = Bins(partition, cancellationToken);

        var series = new List<HistogramSeriesRenderModel>(partition.Series.Count);
        var legendEntries = new List<GraphLegendEntry>(partition.Series.Count);
        var maximumCount = 0;
        for (var index = 0; index < partition.Series.Count; index++)
        {
            var buffer = partition.Series[index];
            var counts = Count(buffer, grid, cancellationToken);
            maximumCount = Math.Max(maximumCount, counts.Length == 0 ? 0 : counts.Max());

            series.Add(new HistogramSeriesRenderModel(buffer.Label, index, counts));
            legendEntries.Add(new GraphLegendEntry(buffer.Label, index));
        }

        // The X axis is the bins themselves, not the data with room around it: a histogram's bars fill their axis.
        var x = new GraphAxisRange(grid.Edges[0], grid.Edges[^1]);
        var y = GraphAxisTicks.NiceCounts(maximumCount);

        var frame = new GraphRenderModel(
            $"Histogram of {labels.Variable}",
            new GraphAxisModel(x, GraphAxisTicks.Nice(x), labels.Variable),
            new GraphAxisModel(y.Range, y.Ticks, FrequencyAxisTitle),
            // A histogram without a group column is one unnamed series, and one series needs no legend.
            data.Group is null ? null : new GraphLegendModel(legendEntries, labels.GroupColumn));

        return new HistogramRenderModel(frame, grid.Bins, series, partition.ObservationCount);
    }

    // One pass over the observations: every countable one joins its group's series, in worksheet row order, and the
    // extremes are collected on the way.
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
            partition.Extend(value);
        }

        return partition;
    }

    // The bins of the whole histogram: how many the statistics ask for, and the equal-width intervals they become.
    private static BinGrid Bins(Partition partition, CancellationToken cancellationToken)
    {
        var minimum = partition.Minimum;
        var maximum = partition.Maximum;

        if (minimum == maximum)
        {
            // Every observation is the same value: one bin over the window a constant axis is given (Task #026/#027),
            // so the renderer never sees an interval of no width.
            var constant = GraphAxisRanges.FromValues(minimum, maximum);
            return Grid(constant.Minimum, constant.Maximum, 1);
        }

        var range = maximum - minimum;
        int count;

        if (partition.ObservationCount < HistogramBinCount.SmallSampleThreshold)
        {
            // Sturges needs the count alone; a small sample is not sorted at all.
            count = HistogramBinCount.Suggest(partition.ObservationCount, range, interquartileRange: 0, standardDeviation: 0);
        }
        else
        {
            var values = partition.AllValues(cancellationToken);
            Array.Sort(values);
            cancellationToken.ThrowIfCancellationRequested();

            var interquartileRange = Quantiles.InterquartileRange(values);
            var standardDeviation = Descriptives.StandardDeviation(values);
            cancellationToken.ThrowIfCancellationRequested();

            count = HistogramBinCount.Suggest(partition.ObservationCount, range, interquartileRange, standardDeviation);
        }

        return Grid(minimum, maximum, count);
    }

    // Equal-width bins over start..end. Every edge is start + index * width rather than the previous edge plus the
    // width, so rounding cannot drift along the axis, and the last edge is end itself, so the largest observation is
    // always inside the last bin.
    private static BinGrid Grid(double start, double end, int count)
    {
        var width = (end - start) / count;
        var edges = new double[count + 1];
        for (var index = 0; index <= count; index++)
        {
            edges[index] = start + (index * width);
        }

        edges[count] = end;

        // Bins narrower than the numbers can express: one bin is the only thing left to draw.
        for (var index = 1; index <= count; index++)
        {
            if (!(edges[index] > edges[index - 1]))
            {
                return new BinGrid([new HistogramBin(start, end)], [start, end], start, end - start);
            }
        }

        var bins = new HistogramBin[count];
        for (var index = 0; index < count; index++)
        {
            bins[index] = new HistogramBin(edges[index], edges[index + 1]);
        }

        return new BinGrid(bins, edges, start, width);
    }

    private static int[] Count(SeriesBuffer series, BinGrid grid, CancellationToken cancellationToken)
    {
        var counts = new int[grid.Bins.Count];
        var values = series.Values;

        for (var index = 0; index < values.Length; index++)
        {
            if ((index & CancellationCheckMask) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            counts[grid.IndexOf(values.Span[index])]++;
        }

        return counts;
    }

    // The bins, and what is needed to place a value in one of them.
    private sealed record BinGrid(IReadOnlyList<HistogramBin> Bins, double[] Edges, double Start, double Width)
    {
        // The bin of one value: [lower, upper) for every bin, and the last bin also holds its own upper edge, so the
        // largest observation is counted. A value exactly on an edge between two bins belongs to the one on the right.
        public int IndexOf(double value)
        {
            var last = Bins.Count - 1;
            var index = Width > 0 ? (int)((value - Start) / Width) : 0;
            index = Math.Clamp(index, 0, last);

            // Arithmetic on the width can land a value one bin away from where its own edges put it; the edges decide.
            while (index > 0 && value < Edges[index])
            {
                index--;
            }

            while (index < last && value >= Edges[index + 1])
            {
                index++;
            }

            return index;
        }
    }

    // The series found so far, in first-observed order, with the extremes of the counted values.
    private sealed class Partition
    {
        private readonly List<SeriesBuffer> _series = [];

        public IReadOnlyList<SeriesBuffer> Series => _series;

        public int ObservationCount { get; private set; }

        public double Minimum { get; private set; } = double.PositiveInfinity;

        public double Maximum { get; private set; } = double.NegativeInfinity;

        public SeriesBuffer Add(string label)
        {
            var series = new SeriesBuffer(label);
            _series.Add(series);
            return series;
        }

        public void Extend(double value)
        {
            ObservationCount++;
            Minimum = Math.Min(Minimum, value);
            Maximum = Math.Max(Maximum, value);
        }

        // Every counted value in one array, for the statistics that need the whole sample.
        public double[] AllValues(CancellationToken cancellationToken)
        {
            var all = new double[ObservationCount];
            var written = 0;
            foreach (var series in _series)
            {
                cancellationToken.ThrowIfCancellationRequested();
                series.Values.Span.CopyTo(all.AsSpan(written));
                written += series.Count;
            }

            return all;
        }
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
