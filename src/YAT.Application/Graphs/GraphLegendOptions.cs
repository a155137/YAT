namespace YAT.Application.Graphs;

// Whether a graph's legend is shown (Task #044).
public enum GraphLegendMode
{
    // The legend the graph type gives the graph: one for a graph of groups or of several variables drawn together,
    // none for a graph of one unnamed series.
    Auto,

    // The graph's legend, shown. A graph without groups has no legend to show and gets none: nothing is made up for
    // it, so for now this shows exactly what Auto shows.
    Show,

    // No legend, and its room given back to the plot. The series themselves - their order, colours and statistics -
    // are what they were.
    Hide
}

// Which side of the plot a graph's legend stands on.
public enum GraphLegendPosition
{
    // Beside the plot on the right, above the statistics panel: where a legend always stood.
    Right,

    // Beside the plot on the left, outside the Y axis title.
    Left,

    // Above the plot, below the graph title.
    Top,

    // Below the plot, below the X axis title.
    Bottom
}

// How a graph's legend is presented (Task #044): whether it is shown and where. Part of the graph's configuration - a
// setting of the graph itself, which a graph window can change afterwards without its data. Graph types that do not
// declare GraphCapability.Legend ignore it; the data query and the graph types' builders never read it. The position
// is kept whatever the mode, so a legend hidden and shown again comes back where it was.
public sealed record GraphLegendOptions(
    GraphLegendMode Mode = GraphLegendMode.Auto,
    GraphLegendPosition Position = GraphLegendPosition.Right)
{
    // The legend the graph type gives, on the right: the graph as it always was.
    public static GraphLegendOptions Default { get; } = new();

    // Whether both choices are defined values.
    public bool IsValid => Enum.IsDefined(Mode) && Enum.IsDefined(Position);
}
