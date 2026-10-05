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
    private readonly GraphSeriesOrder? _seriesOrder;
    private readonly IReadOnlyList<HistogramBin>? _bins;

    // For a panel of a graph drawn in panels (Task #058): seriesOrder is the whole graph's series order, so a group keeps
    // its colour in every panel, and bins are the whole graph's bins, so every panel counts into the same intervals and
    // the panels' bars line up. None of either works both out from the observations given, as a histogram always did.
    public HistogramRenderModelBuilder(GraphSeriesOrder? seriesOrder = null, IReadOnlyList<HistogramBin>? bins = null)
    {
        if (bins is { Count: 0 })
        {
            throw new ArgumentException("A histogram needs at least one bin.", nameof(bins));
        }

        _seriesOrder = seriesOrder;
        _bins = bins;
    }

    // The series of observations whose group value is empty. They are counted, never dropped.
    public const string MissingGroupLabel = "(Missing)";

    public const string FrequencyAxisTitle = "Frequency";

    public const string PercentAxisTitle = "Percent";

    public const string DensityAxisTitle = "Density";

    // What the user is told when a width-and-start grid cannot be drawn for this data. Neither is a defect: the width
    // simply does not suit the data, and the user can choose another.
    public const string TooManyBinsMessage = "The selected bin width requires more than 200 bins. Choose a larger bin width.";

    public const string WidthTooSmallMessage = "The selected bin width is too small for this data range. Choose a larger bin width.";

    // Group values of a numeric group column, and nothing else, are formatted with this.
    private const string GroupValueFormat = "0.####";

    // Cancellation is checked every this many observations (a power of two, so the test is a mask).
    private const int CancellationCheckMask = 0xFFFF;

    // Floating point can put floor((value - start) / width) one bin off the value's true bin; a few steps correct that.
    // Needing more means the width is below what the values can resolve.
    private const int GridCorrectionSteps = 4;

    // The histogram of these observations, or null when none of them can be counted. The labels name the worksheet
    // columns; they are given, not looked up. With the default options: frequency over automatic bins.
    public HistogramRenderModel? Build(UnivariateGraphData data, HistogramPlotLabels labels, CancellationToken cancellationToken = default) =>
        Build(data, labels, HistogramOptions.Default, cancellationToken);

    // The same, with the histogram's own options: how its bins are chosen, what its bars measure and whether each
    // series has its normal fit drawn over it. None of them changes which observations are counted or how the groups
    // are formed, and a series without a fit that can be drawn only goes without its curve. A width-and-start grid that
    // does not suit the data (too many bins, or a width the values cannot resolve) is refused with a
    // GraphPreparationException the user is shown.
    public HistogramRenderModel? Build(
        UnivariateGraphData data,
        HistogramPlotLabels labels,
        HistogramOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(options);
        if (!HistogramOptionsRules.IsValid(options))
        {
            throw new ArgumentException("The histogram options are not valid; validate the configuration first.", nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var partition = Split(data, cancellationToken);
        if (partition.ObservationCount == 0)
        {
            return null;
        }

        // One set of bins, worked out from every observation: comparing groups is only meaningful when they are counted
        // into the same intervals.
        var grid = _bins is { } shared ? SharedGrid(shared) : Bins(partition, options, cancellationToken);

        var series = new List<HistogramSeriesRenderModel>(partition.Series.Count);
        var legendEntries = new List<GraphLegendEntry>(partition.Series.Count);
        var maximumCount = 0;
        var maximumHeight = 0d;
        var maximumFit = 0d;
        for (var index = 0; index < partition.Series.Count; index++)
        {
            var buffer = partition.Series[index];
            var counts = Count(buffer, grid, cancellationToken);
            var heights = Heights(counts, buffer.Count, grid.Bins, options);
            maximumCount = Math.Max(maximumCount, counts.Length == 0 ? 0 : counts.Max());
            maximumHeight = Math.Max(maximumHeight, heights.Length == 0 ? 0 : heights.Max());

            // Each series' own fit, over the bins every series shares. A series that has none is drawn without it.
            HistogramNormalFit? fit = null;
            if (options.ShowNormalFit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                fit = HistogramNormalFit.Fit(buffer.Values.Span, options.YScale, grid.Width);
                maximumFit = Math.Max(maximumFit, fit?.MaximumHeight ?? 0);
            }

            series.Add(new HistogramSeriesRenderModel(buffer.Label, SeriesIndex(buffer.Label, index), counts, heights) { NormalFit = fit });
            legendEntries.Add(new GraphLegendEntry(buffer.Label, SeriesIndex(buffer.Label, index)));
        }

        // The X axis is the bins themselves, not the data with room around it: a histogram's bars fill their axis. A
        // normal fit has no say in it; the plot area clips whatever of a curve lies outside.
        var x = new GraphAxisRange(grid.Edges[0], grid.Edges[^1]);

        // Counts are read on the whole-number axis they always were; a percent or a density on a continuous one. The Y
        // axis reaches the tallest bar, or the highest normal fit when a curve rises above every bar - and only then is
        // it any different from the axis without fits.
        var y = options.YScale switch
        {
            HistogramYScale.Frequency => AxisOf(GraphAxisTicks.NiceCounts(
                maximumFit > maximumCount ? FitCountReach(maximumFit) : maximumCount)),
            _ => AxisOf(GraphAxisTicks.NiceFromZero(Math.Max(maximumHeight, maximumFit)))
        };

        var frame = new GraphRenderModel(
            $"Histogram of {labels.Variable}",
            new GraphAxisModel(x, GraphAxisTicks.Nice(x), labels.AxisTitle ?? labels.Variable),
            new GraphAxisModel(y.Range, y.Ticks, AxisTitle(options.YScale))
            {
                Scale = options.YScale == HistogramYScale.Frequency ? GraphAxisScale.Count : GraphAxisScale.Linear
            },
            // A histogram without a group column is one unnamed series, and one series needs no legend.
            data.Group is null ? null : new GraphLegendModel(_seriesOrder?.Arrange(legendEntries, entry => entry.SeriesIndex) ?? legendEntries, labels.GroupColumn));

        return new HistogramRenderModel(frame, grid.Bins, _seriesOrder?.Arrange(series, item => item.SeriesIndex) ?? series, partition.ObservationCount, options.YScale);
    }

    public static string AxisTitle(HistogramYScale scale) => scale switch
    {
        HistogramYScale.Percent => PercentAxisTitle,
        HistogramYScale.Density => DensityAxisTitle,
        _ => FrequencyAxisTitle
    };

    // The highest count a frequency axis is stretched to for a normal fit: well within what the count axis can step
    // through in whole numbers without overflowing.
    internal const int MaximumFitCount = int.MaxValue / 4;

    // The whole-number count a frequency axis has to reach for a normal fit this high. A count axis is built in whole
    // numbers, so a fit taller than any count a histogram could hold (a series whose spread is far below its bins'
    // width) is reached only as far as MaximumFitCount, and the plot area clips the rest of its peak.
    internal static int FitCountReach(double fit) =>
        fit >= MaximumFitCount ? MaximumFitCount : (int)Math.Ceiling(fit);

    private static (GraphAxisRange Range, IReadOnlyList<GraphAxisTick> Ticks) AxisOf(GraphCountAxis axis) => (axis.Range, axis.Ticks);

    private static (GraphAxisRange Range, IReadOnlyList<GraphAxisTick> Ticks) AxisOf(GraphZeroBasedAxis axis) => (axis.Range, axis.Ticks);

    // The bar heights of one series on the chosen scale. Frequency is the counts themselves. Percent and density are
    // shares of the series' own observations, so every group is scaled by its own size: each group's percents add up to
    // 100, and each group's density bars have unit area, measured with each bin's own width.
    private static double[] Heights(int[] counts, int observations, IReadOnlyList<HistogramBin> bins, HistogramOptions options)
    {
        var heights = new double[counts.Length];
        for (var index = 0; index < counts.Length; index++)
        {
            heights[index] = options.YScale switch
            {
                HistogramYScale.Percent => 100d * counts[index] / observations,
                HistogramYScale.Density => counts[index] / (observations * bins[index].Width),
                _ => counts[index]
            };

            // A bin narrower than a density can be written over (widths near the smallest doubles) has no drawable bar.
            // A width the user chose is theirs to change; bins the data chose that narrow are a defect, not a choice.
            if (!double.IsFinite(heights[index]))
            {
                throw options.BinningMode == HistogramBinningMode.WidthAndStart
                    ? new GraphPreparationException(WidthTooSmallMessage)
                    : new InvalidOperationException($"Bin {index} is too narrow for a density ({bins[index].Width:R}).");
            }
        }

        return heights;
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

        // Numeric groups from the smallest up, "(Missing)" last; text groups as first seen (GraphGroupOrder, Task #059).
        partition.Arrange(byNumber, missing);
        return partition;
    }

    // The bins of the whole histogram. Automatic and counted bins divide the data's own range - the statistics choose
    // how many, or the user does; a width-and-start grid is fixed by the options and only reaches as far as the data.
    private static BinGrid Bins(Partition partition, HistogramOptions options, CancellationToken cancellationToken)
    {
        if (options.BinningMode == HistogramBinningMode.WidthAndStart)
        {
            return FixedGrid(partition.Minimum, partition.Maximum, options.BinWidth!.Value, options.BinStart!.Value);
        }

        var minimum = partition.Minimum;
        var maximum = partition.Maximum;

        if (minimum == maximum)
        {
            // Every observation is the same value: one bin over the window a constant axis is given (Task #026/#027),
            // so the renderer never sees an interval of no width. A requested number of bins does not change that:
            // dividing a window the data never spans would only add empty bins.
            var constant = GraphAxisRanges.FromValues(minimum, maximum);
            return Grid(constant.Minimum, constant.Maximum, 1);
        }

        // The user's number of bins replaces the statistics' choice, and nothing else: the same range, the same edges,
        // the same counting.
        if (options.BinningMode == HistogramBinningMode.Count)
        {
            return Grid(minimum, maximum, options.BinCount!.Value);
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

    // The bins of a fixed grid: edges at start + k x width for whole numbers k, from the bin holding the smallest value
    // to the bin holding the largest - never all the way to start when start is far from the data. Every edge is
    // computed from start and k, never by adding widths, so the same k gives the very same edge for any data; and every
    // bin is [left, right), so a value on an edge belongs to the bin that begins there, the largest value included.
    //
    // The grid is not bent to fit the data: when the width needs more bins than a histogram draws, or is too small for
    // the values to tell its edges apart, the histogram is refused and the user told why.
    private static BinGrid FixedGrid(double minimum, double maximum, double width, double start)
    {
        double Edge(double k) => start + (k * width);

        // A data range of this many widths needs at least this many bins, wherever the grid starts: said first, because
        // it is the reason, even when the width is also too small for the values' precision.
        if (!((maximum - minimum) / width < HistogramOptions.MaximumBinCount))
        {
            throw new GraphPreparationException(TooManyBinsMessage);
        }

        var first = Math.Floor((minimum - start) / width);
        var last = Math.Floor((maximum - start) / width);

        // Past 2^53 a double no longer tells k from k + 1, and no edge could be told from the next.
        const double LargestExactInteger = 9007199254740992d;
        if (!double.IsFinite(first) || !double.IsFinite(last) || Math.Abs(first) > LargestExactInteger || Math.Abs(last) > LargestExactInteger)
        {
            throw new GraphPreparationException(WidthTooSmallMessage);
        }

        // The division can land a value one bin off its own edges; the edges decide, as they do for every bin.
        first = Settle(first, minimum, Edge);
        last = Settle(last, maximum, Edge);

        // Counted before anything is allocated: a width that is small for the data asks for many bins.
        var count = last - first + 1;
        if (count > HistogramOptions.MaximumBinCount)
        {
            throw new GraphPreparationException(TooManyBinsMessage);
        }

        var edges = new double[(int)count + 1];
        for (var index = 0; index < edges.Length; index++)
        {
            edges[index] = Edge(first + index);
            if (!double.IsFinite(edges[index]) || (index > 0 && !(edges[index] > edges[index - 1])))
            {
                throw new GraphPreparationException(WidthTooSmallMessage);
            }
        }

        var bins = new HistogramBin[(int)count];
        for (var index = 0; index < bins.Length; index++)
        {
            bins[index] = new HistogramBin(edges[index], edges[index + 1]);
        }

        return new BinGrid(bins, edges, edges[0], width);
    }

    // The k whose bin [edge(k), edge(k + 1)) holds value, starting from an estimate at most a few bins off. An estimate
    // that will not settle means the edges around value are not distinct numbers.
    private static double Settle(double k, double value, Func<double, double> edge)
    {
        for (var step = 0; step < GridCorrectionSteps; step++)
        {
            if (edge(k) > value)
            {
                k--;
            }
            else if (edge(k + 1) <= value)
            {
                k++;
            }
            else
            {
                return k;
            }
        }

        throw new GraphPreparationException(WidthTooSmallMessage);
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

    // The grid of bins the whole graph worked out, for one of its panels: the same edges, so a value lands in the bin it
    // landed in for the whole graph. The width only speeds the search; the edges decide.
    private static BinGrid SharedGrid(IReadOnlyList<HistogramBin> bins)
    {
        var edges = new double[bins.Count + 1];
        for (var index = 0; index < bins.Count; index++)
        {
            edges[index] = bins[index].LowerEdge;
        }

        edges[^1] = bins[^1].UpperEdge;
        return new BinGrid(bins, edges, edges[0], bins[0].Width);
    }

    // A series' index: its place in the whole graph when a panel is built against one, else its place here.
    private int SeriesIndex(string label, int index) => _seriesOrder?.IndexOf(label, index) ?? index;

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

    // The series found so far, in first-observed order until arranged in the order they are drawn (GraphGroupOrder),
    // with the extremes of the counted values.
    private sealed class Partition
    {
        private readonly List<SeriesBuffer> _series = [];

        public IReadOnlyList<SeriesBuffer> Series => _series;

        public int ObservationCount { get; private set; }

        public double Minimum { get; private set; } = double.PositiveInfinity;

        public double Maximum { get; private set; } = double.NegativeInfinity;

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
