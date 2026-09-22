namespace YAT.app.Graphs.Rendering;

// One row of a statistics panel: one series of the graph (or the whole graph, when it is not grouped), what the
// statistics are, and the text they are shown as.
//
// The values are what the statistics mean; the texts are what the renderer draws. Like axis tick labels and legend
// labels, the texts are decided when the model is built, never by the renderer.
public sealed record GraphStatisticsRow
{
    public GraphStatisticsRow(
        string label,
        int? seriesIndex,
        int count,
        double mean,
        double? standardDeviation,
        string countText,
        string meanText,
        string standardDeviationText)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentNullException.ThrowIfNull(countText);
        ArgumentNullException.ThrowIfNull(meanText);
        ArgumentNullException.ThrowIfNull(standardDeviationText);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);

        if (seriesIndex is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seriesIndex), seriesIndex, "A series index cannot be negative.");
        }

        if (!double.IsFinite(mean) || standardDeviation is { } spread && (!double.IsFinite(spread) || spread < 0))
        {
            throw new ArgumentException("A statistics row is made of finite values and a non-negative spread.", nameof(mean));
        }

        Label = label;
        SeriesIndex = seriesIndex;
        Count = count;
        Mean = mean;
        StandardDeviation = standardDeviation;
        CountText = countText;
        MeanText = meanText;
        StandardDeviationText = standardDeviationText;
    }

    // The group the row describes; empty when the graph is not grouped.
    public string Label { get; }

    // The colour of the group in the graph and its legend, through the theme's palette; null when not grouped.
    public int? SeriesIndex { get; }

    // N: the observations of this series the graph used.
    public int Count { get; }

    public double Mean { get; }

    // The sample standard deviation, or null when the series has fewer than two observations.
    public double? StandardDeviation { get; }

    public string CountText { get; }

    public string MeanText { get; }

    public string StandardDeviationText { get; }
}

// The statistics of a graph, shown in a panel beside its plot: one block of Mean, StDev and N when the graph is not
// grouped, a compact table with one row per group when it is.
//
// It is part of the graph frame, like the legend, so it is drawn by the frame renderer and appears the same on screen,
// in a PNG and in a presentation. The rows are all kept here even when the panel has room to draw only some of them.
public sealed record GraphStatisticsPanel
{
    public GraphStatisticsPanel(string title, string? groupHeader, IReadOnlyList<GraphStatisticsRow> rows)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(rows);

        if (rows.Count == 0)
        {
            throw new ArgumentException("A statistics panel needs at least one row; use no panel instead.", nameof(rows));
        }

        if (rows.Any(row => row is null))
        {
            throw new ArgumentException("A statistics row must not be null.", nameof(rows));
        }

        if (groupHeader is null && rows.Count != 1)
        {
            throw new ArgumentException("An ungrouped statistics panel has exactly one row.", nameof(rows));
        }

        Title = title;
        GroupHeader = groupHeader;
        Rows = [.. rows];
    }

    public string Title { get; }

    // The grouping column's name heading the group labels; null when the graph is not grouped.
    public string? GroupHeader { get; }

    public bool IsGrouped => GroupHeader is not null;

    // One row per series, in the graph's own series order.
    public IReadOnlyList<GraphStatisticsRow> Rows { get; }
}
