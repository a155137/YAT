using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// One bin of a histogram: the half-open interval [LowerEdge, UpperEdge) it counts. On bins drawn over the data's own
// range (automatic or counted bins) the last bin also counts its upper edge, which is the largest observation. On a
// fixed width-and-start grid every bin is half-open, the last one included: the grid always reaches past the largest
// observation instead, so a value's bin never depends on which other values it is drawn with.
public sealed record HistogramBin
{
    public HistogramBin(double lowerEdge, double upperEdge)
    {
        if (!double.IsFinite(lowerEdge) || !double.IsFinite(upperEdge))
        {
            throw new ArgumentException("A histogram bin must have finite edges.", nameof(lowerEdge));
        }

        if (upperEdge <= lowerEdge)
        {
            throw new ArgumentException($"A histogram bin must be wider than nothing ({lowerEdge}..{upperEdge}).", nameof(upperEdge));
        }

        LowerEdge = lowerEdge;
        UpperEdge = upperEdge;
    }

    public double LowerEdge { get; }

    public double UpperEdge { get; }

    public double Width => UpperEdge - LowerEdge;
}

// How often one group of observations falls into each bin, and how tall its bars are drawn. Counts has one entry per
// bin of the histogram, in bin order, and is what the histogram is: the observations in each bin. Heights are those
// counts on the histogram's Y scale (the counts themselves, a percent, or a density) - what the bars are drawn to; they
// never replace the counts. The series index selects the colour from the theme palette, for the bars and for the normal
// fit drawn over them.
public sealed record HistogramSeriesRenderModel
{
    // A series drawn on the frequency scale: every bar as tall as its count.
    public HistogramSeriesRenderModel(string label, int seriesIndex, IReadOnlyList<int> counts)
        : this(label, seriesIndex, counts, counts is null ? null! : [.. counts.Select(count => (double)count)])
    {
    }

    public HistogramSeriesRenderModel(string label, int seriesIndex, IReadOnlyList<int> counts, IReadOnlyList<double> heights)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        ArgumentNullException.ThrowIfNull(counts);
        ArgumentNullException.ThrowIfNull(heights);
        if (counts.Any(count => count < 0))
        {
            throw new ArgumentException("A bin count cannot be negative.", nameof(counts));
        }

        if (heights.Count != counts.Count)
        {
            throw new ArgumentException("A histogram series needs one bar height per bin count.", nameof(heights));
        }

        if (heights.Any(height => !double.IsFinite(height) || height < 0))
        {
            throw new ArgumentException("A bar height must be a finite, non-negative number.", nameof(heights));
        }

        Label = label;
        SeriesIndex = seriesIndex;
        Counts = [.. counts];
        Heights = [.. heights];
        ObservationCount = Counts.Sum();
    }

    public string Label { get; }

    public int SeriesIndex { get; }

    // One count per bin, in bin order.
    public IReadOnlyList<int> Counts { get; }

    // One bar height per bin, in bin order, on the histogram's Y scale.
    public IReadOnlyList<double> Heights { get; }

    // Every observation of this series; the sum of its counts.
    public int ObservationCount { get; }

    // The normal curve of this series' own mean and standard deviation, on the same Y scale as its bars (Task #042).
    // Null when no fit was asked for, or when this series has none that can be drawn; either way no curve is drawn.
    public HistogramNormalFit? NormalFit { get; init; }
}

// A histogram ready to be drawn: the shared graph frame, the bins every series is counted into, and those counts.
//
// Every series is counted into the same bins - that is what makes the groups comparable - and every observation the
// graph data delivered is counted, so the counts add up to SourceObservationCount exactly. Nothing is sampled: a
// million observations become the same handful of bins as a thousand.
public sealed record HistogramRenderModel
{
    public HistogramRenderModel(
        GraphRenderModel frame,
        IReadOnlyList<HistogramBin> bins,
        IReadOnlyList<HistogramSeriesRenderModel> series,
        int sourceObservationCount,
        HistogramYScale yScale = HistogramYScale.Frequency)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(bins);
        ArgumentNullException.ThrowIfNull(series);

        if (bins.Count == 0)
        {
            throw new ArgumentException("A histogram needs at least one bin.", nameof(bins));
        }

        if (series.Any(item => item is null))
        {
            throw new ArgumentException("A histogram series must not be null.", nameof(series));
        }

        if (series.Any(item => item.Counts.Count != bins.Count))
        {
            throw new ArgumentException("Every histogram series must be counted into every bin.", nameof(series));
        }

        var counted = series.Sum(item => item.ObservationCount);
        if (counted != sourceObservationCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceObservationCount),
                sourceObservationCount,
                $"A histogram counts every observation it was given ({counted} counted).");
        }

        Frame = frame;
        Bins = [.. bins];
        Series = [.. series];
        SourceObservationCount = sourceObservationCount;
        MaximumCount = series.Count == 0 ? 0 : series.Max(item => item.Counts.Count == 0 ? 0 : item.Counts.Max());
        MaximumHeight = series.Count == 0 ? 0 : series.Max(item => item.Heights.Count == 0 ? 0 : item.Heights.Max());
        YScale = yScale;
    }

    // What the bar heights measure.
    public HistogramYScale YScale { get; }

    // The tallest bar on that scale: what the Y axis has to reach. On the frequency scale, MaximumCount.
    public double MaximumHeight { get; }

    public GraphRenderModel Frame { get; }

    public IReadOnlyList<HistogramBin> Bins { get; }

    public IReadOnlyList<HistogramSeriesRenderModel> Series { get; }

    // The observations behind the counts. Histograms use all of them.
    public int SourceObservationCount { get; }

    // The tallest bar of the graph: what the frequency axis has to reach.
    public int MaximumCount { get; }
}

// The column names a histogram is labelled with. They are passed in rather than read here, so nothing in the
// presentation layer reaches for worksheet metadata of its own.
public sealed record HistogramPlotLabels(string Variable, string? GroupColumn = null)
{
    // The X axis title when it is not the variable's name: several variables drawn together are one axis of "Data"
    // (see GraphVariablesTogether). Null for the variable's name.
    public string? AxisTitle { get; init; }
}
