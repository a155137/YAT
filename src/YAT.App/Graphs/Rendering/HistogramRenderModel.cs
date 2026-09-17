namespace YAT.app.Graphs.Rendering;

// One bin of a histogram: the half-open interval [LowerEdge, UpperEdge) it counts, except for the last bin of a
// histogram, which also counts its upper edge so that the largest observation is always somewhere.
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

// How often one group of observations falls into each bin. Counts has one entry per bin of the histogram, in bin
// order; the series index selects the colour from the theme palette.
public sealed record HistogramSeriesRenderModel
{
    public HistogramSeriesRenderModel(string label, int seriesIndex, IReadOnlyList<int> counts)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Any(count => count < 0))
        {
            throw new ArgumentException("A bin count cannot be negative.", nameof(counts));
        }

        Label = label;
        SeriesIndex = seriesIndex;
        Counts = [.. counts];
        ObservationCount = Counts.Sum();
    }

    public string Label { get; }

    public int SeriesIndex { get; }

    // One count per bin, in bin order.
    public IReadOnlyList<int> Counts { get; }

    // Every observation of this series; the sum of its counts.
    public int ObservationCount { get; }
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
        int sourceObservationCount)
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
    }

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
public sealed record HistogramPlotLabels(string Variable, string? GroupColumn = null);
