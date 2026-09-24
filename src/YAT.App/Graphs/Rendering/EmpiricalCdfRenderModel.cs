namespace YAT.app.Graphs.Rendering;

// One step of an empirical distribution: an observed value, and the share of the series that is at most that value.
//
// Repeated observations of the same value are one step, not several: a value seen three times in five jumps straight to
// sixty percent.
public readonly record struct EmpiricalCdfPoint(double Value, double CumulativePercent);

// One group's empirical distribution: its own steps, in ascending order, ending at a hundred percent.
public sealed record EmpiricalCdfSeriesRenderModel
{
    public EmpiricalCdfSeriesRenderModel(
        string label,
        int seriesIndex,
        ReadOnlyMemory<EmpiricalCdfPoint> points,
        int observationCount,
        int uniquePointCount)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        if (points.Length == 0)
        {
            throw new ArgumentException("An empirical distribution needs at least one step.", nameof(points));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(uniquePointCount, points.Length);
        ArgumentOutOfRangeException.ThrowIfLessThan(observationCount, uniquePointCount);

        Label = label;
        SeriesIndex = seriesIndex;
        Points = points;
        ObservationCount = observationCount;
        UniquePointCount = uniquePointCount;
    }

    public string Label { get; }

    public int SeriesIndex { get; }

    // The steps this series draws, by ascending value; the last one is at a hundred percent.
    public ReadOnlyMemory<EmpiricalCdfPoint> Points { get; }

    // The observations behind the steps. Repeated values share a step, so there are usually more observations than
    // steps, and the percentages are always the share of this number.
    public int ObservationCount { get; }

    // The steps this distribution has, whether or not all of them are drawn.
    public int UniquePointCount { get; }
}

// An empirical cumulative distribution ready to be drawn: the shared graph frame and one step function per group.
//
// Every percentage comes from all of the observations of its own group. Only the number of steps that are drawn is
// capped, and only after the distribution has been worked out.
public sealed record EmpiricalCdfRenderModel
{
    public EmpiricalCdfRenderModel(
        GraphRenderModel frame,
        IReadOnlyList<EmpiricalCdfSeriesRenderModel> series,
        int sourceObservationCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(series);
        if (series.Any(item => item is null))
        {
            throw new ArgumentException("An empirical distribution series must not be null.", nameof(series));
        }

        var rendered = series.Sum(item => item.Points.Length);
        if (sourceObservationCount < rendered)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceObservationCount), sourceObservationCount, "A distribution cannot draw more steps than it has observations.");
        }

        Frame = frame;
        Series = [.. series];
        SourceObservationCount = sourceObservationCount;
        RenderedPointCount = rendered;
        UniquePointCount = series.Sum(item => item.UniquePointCount);
    }

    public GraphRenderModel Frame { get; }

    public IReadOnlyList<EmpiricalCdfSeriesRenderModel> Series { get; }

    // Observations the graph data had (after the null rules of the data pipeline).
    public int SourceObservationCount { get; }

    // Steps this model draws.
    public int RenderedPointCount { get; }

    // Steps the distributions actually have, before the display cap.
    public int UniquePointCount { get; }

    public bool WasSampled => RenderedPointCount < UniquePointCount;
}

// The column names an empirical CDF is labelled with. They are passed in rather than read here, so nothing in the
// presentation layer reaches for worksheet metadata of its own.
public sealed record EmpiricalCdfLabels(string Variable, string? GroupColumn = null)
{
    // The X axis title when it is not the variable's name: several variables drawn together are one axis of "Data"
    // (see GraphVariablesTogether). Null for the variable's name.
    public string? AxisTitle { get; init; }
}
