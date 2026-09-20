namespace YAT.app.Graphs.Rendering;

// One plotted observation: the value that was measured, and the normal score its place in the sorted sample gives it.
public readonly record struct ProbabilityPlotPoint(double Value, double Score);

// The line a normally distributed sample would fall on: x = mean + standardDeviation * score, drawn between two
// scores. It comes from the sample's own mean and standard deviation, not from fitting a line through the points.
public sealed record ProbabilityPlotFittedLine
{
    public ProbabilityPlotFittedLine(double mean, double standardDeviation, double fromScore, double toScore)
    {
        if (!double.IsFinite(mean))
        {
            throw new ArgumentException("A fitted line needs a finite mean.", nameof(mean));
        }

        if (!double.IsFinite(standardDeviation) || standardDeviation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(standardDeviation), standardDeviation, "A fitted line needs a positive standard deviation.");
        }

        if (!double.IsFinite(fromScore) || !double.IsFinite(toScore) || toScore <= fromScore)
        {
            throw new ArgumentException("A fitted line needs two different finite scores.", nameof(toScore));
        }

        Mean = mean;
        StandardDeviation = standardDeviation;
        FromScore = fromScore;
        ToScore = toScore;
        FromValue = mean + (standardDeviation * fromScore);
        ToValue = mean + (standardDeviation * toScore);
    }

    public double Mean { get; }

    public double StandardDeviation { get; }

    public double FromScore { get; }

    public double ToScore { get; }

    // Where the line starts and ends, in the same values the points are drawn at.
    public double FromValue { get; }

    public double ToValue { get; }
}

// One group of a probability plot: its observations in ascending order, each with the score of its rank, and the line
// its own mean and standard deviation describe. A group with no spread has points but no line.
public sealed record ProbabilityPlotSeriesRenderModel
{
    public ProbabilityPlotSeriesRenderModel(
        string label,
        int seriesIndex,
        ReadOnlyMemory<ProbabilityPlotPoint> points,
        ProbabilityPlotFittedLine? fittedLine,
        int observationCount)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(observationCount, points.Length);

        Label = label;
        SeriesIndex = seriesIndex;
        Points = points;
        FittedLine = fittedLine;
        ObservationCount = observationCount;
    }

    public string Label { get; }

    public int SeriesIndex { get; }

    // The points this series draws, in ascending order of value.
    public ReadOnlyMemory<ProbabilityPlotPoint> Points { get; }

    public ProbabilityPlotFittedLine? FittedLine { get; }

    // The observations behind the series. Every one of them was ranked, scored and used for the mean, the standard
    // deviation and the axes, even when only some of them are drawn.
    public int ObservationCount { get; }
}

// A normal probability plot ready to be drawn: the shared graph frame and the series on it.
//
// Every statistic in it - the ranks, the plotting positions, the scores, each group's mean and standard deviation, both
// axes - comes from all of the observations. Only the drawing is capped.
public sealed record ProbabilityPlotRenderModel
{
    public ProbabilityPlotRenderModel(
        GraphRenderModel frame,
        IReadOnlyList<ProbabilityPlotSeriesRenderModel> series,
        int sourceObservationCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(series);
        if (series.Any(item => item is null))
        {
            throw new ArgumentException("A probability plot series must not be null.", nameof(series));
        }

        var rendered = series.Sum(item => item.Points.Length);
        if (sourceObservationCount < rendered)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceObservationCount), sourceObservationCount, "A probability plot cannot draw more points than it has.");
        }

        Frame = frame;
        Series = [.. series];
        SourceObservationCount = sourceObservationCount;
        RenderedPointCount = rendered;
    }

    public GraphRenderModel Frame { get; }

    public IReadOnlyList<ProbabilityPlotSeriesRenderModel> Series { get; }

    // Observations the graph data had (after the null rules of the data pipeline).
    public int SourceObservationCount { get; }

    // Observations this model draws.
    public int RenderedPointCount { get; }

    public bool WasSampled => RenderedPointCount < SourceObservationCount;
}

// The column names a probability plot is labelled with. They are passed in rather than read here, so nothing in the
// presentation layer reaches for worksheet metadata of its own.
public sealed record ProbabilityPlotLabels(string Variable, string? GroupColumn = null);
