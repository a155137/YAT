namespace YAT.app.Graphs.Rendering;

// One plotted observation, in data values. Screen coordinates are the renderer's business.
public readonly record struct ScatterPoint(double X, double Y);

// The points of one scatter series: one group of the graph, or the whole graph when it is not grouped. The series index
// selects the colour from the theme palette, so no rendering resource reaches this model.
public sealed record ScatterSeriesRenderModel
{
    public ScatterSeriesRenderModel(string label, int seriesIndex, ReadOnlyMemory<ScatterPoint> points)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        Label = label;
        SeriesIndex = seriesIndex;
        Points = points;
    }

    public string Label { get; }

    public int SeriesIndex { get; }

    public ReadOnlyMemory<ScatterPoint> Points { get; }
}

// A scatter plot ready to be drawn: the shared graph frame (title, axes, legend) plus the points of each series.
//
// The counts describe what the display shows compared with what the worksheet holds: a scatter plot of more points than
// the render cap draws a deterministic sample of them. The worksheet data and the graph data behind it are untouched;
// only this model is sampled, and the counts are kept so that a later task can tell the user about it.
public sealed record ScatterRenderModel
{
    public ScatterRenderModel(GraphRenderModel frame, IReadOnlyList<ScatterSeriesRenderModel> series, int sourcePointCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(series);
        if (series.Any(item => item is null))
        {
            throw new ArgumentException("A scatter series must not be null.", nameof(series));
        }

        var rendered = series.Sum(item => item.Points.Length);
        if (sourcePointCount < rendered)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourcePointCount), sourcePointCount, "A scatter plot cannot draw more points than it has.");
        }

        Frame = frame;
        Series = [.. series];
        SourcePointCount = sourcePointCount;
        RenderedPointCount = rendered;
    }

    public GraphRenderModel Frame { get; }

    public IReadOnlyList<ScatterSeriesRenderModel> Series { get; }

    // Observations the graph data had (after the null rules of the data pipeline).
    public int SourcePointCount { get; }

    // Observations this model draws.
    public int RenderedPointCount { get; }

    public bool WasSampled => RenderedPointCount < SourcePointCount;
}

// The column names a scatter plot is labelled with. They are passed in rather than read here, so nothing in the
// presentation layer reaches for worksheet metadata of its own.
public sealed record ScatterPlotLabels(string XColumn, string YColumn, string? GroupColumn = null);
