using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// One box of a box plot, ready to be drawn: where its category sits on the X axis, the five values its shape is made
// of, its mean, and the outliers drawn beside it.
//
// Everything here is a value on the Y axis; nothing is a pixel. The box was worked out by the builder from all of the
// observations of its variable and group - the renderer only draws what this says.
public sealed record BoxPlotBoxRenderModel
{
    public BoxPlotBoxRenderModel(
        string label,
        int categoryIndex,
        int seriesIndex,
        double lowerWhisker,
        double firstQuartile,
        double median,
        double thirdQuartile,
        double upperWhisker,
        double mean,
        ReadOnlyMemory<double> outliers,
        int observationCount,
        int outlierCount)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegative(categoryIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        ArgumentOutOfRangeException.ThrowIfLessThan(observationCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(outlierCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(outliers.Length, outlierCount);

        foreach (var value in (double[])[lowerWhisker, firstQuartile, median, thirdQuartile, upperWhisker, mean])
        {
            if (!double.IsFinite(value))
            {
                throw new ArgumentException("A box is made of finite values.", nameof(median));
            }
        }

        // What is guaranteed, and only that: the median lies between the quartiles, and the lower whisker is not above
        // the upper one.
        //
        // The whiskers are NOT guaranteed to enclose the box. They are observations, while R-7 quartiles are
        // interpolated between observations: when Q3 is interpolated towards a value beyond the upper fence, the
        // largest observation inside the fence - the upper whisker - lies below Q3, inside the box (and likewise at
        // Q1). That is a correct box, so it is accepted as it is; nothing here clamps or moves a value.
        if (firstQuartile > median || median > thirdQuartile)
        {
            throw new ArgumentException("A box reads Q1 <= median <= Q3.", nameof(median));
        }

        if (lowerWhisker > upperWhisker)
        {
            throw new ArgumentException("A box's lower whisker cannot be above its upper whisker.", nameof(lowerWhisker));
        }

        Label = label;
        CategoryIndex = categoryIndex;
        SeriesIndex = seriesIndex;
        LowerWhisker = lowerWhisker;
        FirstQuartile = firstQuartile;
        Median = median;
        ThirdQuartile = thirdQuartile;
        UpperWhisker = upperWhisker;
        Mean = mean;
        Outliers = outliers;
        ObservationCount = observationCount;
        OutlierCount = outlierCount;
    }

    // What the box is of: the variable, or the variable and its group.
    public string Label { get; }

    // Which slot of the X axis it is drawn in; the model's Categories say what that slot is called.
    public int CategoryIndex { get; }

    // The colour the box is drawn in, through the theme's palette. Boxes of the same group share it across variables.
    public int SeriesIndex { get; }

    // The smallest observation inside the lower fence. Usually below Q1, but it can be above it (see the constructor).
    public double LowerWhisker { get; }

    public double FirstQuartile { get; }

    public double Median { get; }

    public double ThirdQuartile { get; }

    // The largest observation inside the upper fence. Usually above Q3, but it can be below it (see the constructor).
    public double UpperWhisker { get; }

    public double Mean { get; }

    // The outliers this box draws, ascending. All of them unless the display budget was spent.
    public ReadOnlyMemory<double> Outliers { get; }

    // Observations the box was computed from, outliers included.
    public int ObservationCount { get; }

    // Outliers the box has, whether or not all of them are drawn.
    public int OutlierCount { get; }
}

// A box plot ready to be drawn: the shared graph frame, the categories along the X axis, and one box per category that
// has observations.
//
// Every box comes from all of the observations of its variable and group. Only the number of outlier markers that are
// drawn is capped, and only after every box has been worked out.
public sealed record BoxPlotRenderModel
{
    public BoxPlotRenderModel(
        GraphRenderModel frame,
        IReadOnlyList<string> categories,
        IReadOnlyList<BoxPlotBoxRenderModel> boxes,
        int sourceObservationCount,
        int outlierCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(categories);
        ArgumentNullException.ThrowIfNull(boxes);

        if (categories.Count == 0)
        {
            throw new ArgumentException("A box plot needs at least one category.", nameof(categories));
        }

        if (boxes.Any(box => box is null))
        {
            throw new ArgumentException("A box must not be null.", nameof(boxes));
        }

        if (boxes.Any(box => box.CategoryIndex >= categories.Count))
        {
            throw new ArgumentOutOfRangeException(nameof(boxes), "A box must sit in one of the plot's categories.");
        }

        var rendered = boxes.Sum(box => box.Outliers.Length);
        if (outlierCount < rendered)
        {
            throw new ArgumentOutOfRangeException(
                nameof(outlierCount), outlierCount, "A box plot cannot draw more outliers than it has.");
        }

        if (sourceObservationCount < boxes.Sum(box => box.ObservationCount))
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceObservationCount), sourceObservationCount, "A box plot cannot use more observations than it was given.");
        }

        Frame = frame;
        Categories = [.. categories];
        Boxes = [.. boxes];
        SourceObservationCount = sourceObservationCount;
        OutlierCount = outlierCount;
        RenderedOutlierCount = rendered;
    }

    public GraphRenderModel Frame { get; }

    // The X axis slots, in order. A category without observations keeps its slot and has no box.
    public IReadOnlyList<string> Categories { get; }

    public IReadOnlyList<BoxPlotBoxRenderModel> Boxes { get; }

    // Observations the graph data had (after the null rules of the data pipeline).
    public int SourceObservationCount { get; }

    // Outliers the boxes have, before the display cap.
    public int OutlierCount { get; }

    // Outlier markers this model draws.
    public int RenderedOutlierCount { get; }

    public bool WasSampled => RenderedOutlierCount < OutlierCount;

    // How the boxes are drawn: their width, and whether their means and outliers are marked (Task #047). Drawing only -
    // the boxes, the frame and its axes are the same whatever it says - so a drawn box plot takes other options as
    // this same model with other Options, its boxes and frame untouched.
    public BoxPlotOptions Options
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            if (!value.IsValid)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value.BoxWidthPercent, "A box width is 20 to 90 percent.");
            }

            field = value;
        }
    } = BoxPlotOptions.Default;
}

// The column names a box plot is labelled with. They are passed in rather than read here, so nothing in the
// presentation layer reaches for worksheet metadata of its own.
public sealed record BoxPlotLabels(IReadOnlyList<string> Variables, string? GroupColumn = null);
