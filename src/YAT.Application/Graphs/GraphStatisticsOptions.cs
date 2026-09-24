namespace YAT.Application.Graphs;

// Whether a graph's statistics panel is shown (Task #045).
public enum GraphStatisticsMode
{
    // The panel the graph type gives the graph: every graph type with a statistics panel gives one to a graph with
    // data.
    Auto,

    // The graph's panel, shown. A graph without a panel gets none: nothing is made up for it, so for now this shows
    // exactly what Auto shows.
    Show,

    // No panel, and its room given back to the plot. The statistics themselves are still worked out with the graph,
    // so the panel can be shown again later without the data.
    Hide
}

// One statistic of a statistics panel, in the order the panel shows them.
public enum GraphStatisticsItem
{
    Mean,
    StandardDeviation,
    Count
}

// How a graph's statistics panel is presented (Task #045): whether it is shown, and which of its statistics - Mean,
// StDev and N, always in that order. Part of the graph's configuration - a setting of the graph itself, which a graph
// window can change afterwards without its data. Graph types that do not declare GraphCapability.StatisticsPanel
// ignore it; the data query never reads it. The statistics chosen are kept whatever the mode, so a panel hidden and
// shown again comes back as it was.
public sealed record GraphStatisticsOptions(
    GraphStatisticsMode Mode = GraphStatisticsMode.Auto,
    bool ShowMean = true,
    bool ShowStandardDeviation = true,
    bool ShowCount = true)
{
    // The graph type's panel with every statistic: the graph as it always was.
    public static GraphStatisticsOptions Default { get; } = new();

    // Every statistic, in the order a panel shows them.
    public static IReadOnlyList<GraphStatisticsItem> AllItems { get; } =
        [GraphStatisticsItem.Mean, GraphStatisticsItem.StandardDeviation, GraphStatisticsItem.Count];

    // The statistics chosen, in the order a panel shows them.
    public IReadOnlyList<GraphStatisticsItem> Items =>
    [
        .. ShowMean ? [GraphStatisticsItem.Mean] : Array.Empty<GraphStatisticsItem>(),
        .. ShowStandardDeviation ? [GraphStatisticsItem.StandardDeviation] : Array.Empty<GraphStatisticsItem>(),
        .. ShowCount ? [GraphStatisticsItem.Count] : Array.Empty<GraphStatisticsItem>()
    ];

    public bool HasItems => ShowMean || ShowStandardDeviation || ShowCount;

    // Whether the mode is a defined value.
    public bool IsModeValid => Enum.IsDefined(Mode);

    // A defined mode, and - unless the panel is hidden - at least one statistic to show. A hidden panel may keep any
    // choice of statistics, none included.
    public bool IsValid => IsModeValid && (Mode == GraphStatisticsMode.Hide || HasItems);
}
