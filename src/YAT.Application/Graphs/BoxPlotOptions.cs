namespace YAT.Application.Graphs;

// What a box plot shows of its own plot (Task #047): how wide its boxes are drawn, and whether their means and their
// outliers are marked. Drawing only: none of it changes a box's statistics, its whiskers or the axes - the whiskers end
// at the most extreme observations inside the fences whether the outliers are marked or not, and the Y axis still
// reaches every outlier - so it can be changed after the graph is drawn without its data.
//
// Only a graph type that declares GraphCapability.BoxPlotControls reads it; every other graph type ignores it, so a
// configuration can always carry the defaults.
public sealed record BoxPlotOptions(int BoxWidthPercent = BoxPlotOptions.DefaultBoxWidthPercent, bool ShowMean = true, bool ShowOutliers = true)
{
    // A box body is this share of its category's slot, in percent: the width box plots have always been drawn at.
    public const int DefaultBoxWidthPercent = 60;

    // Narrower than this a box is a line; wider, neighbouring boxes run into each other.
    public const int MinimumBoxWidthPercent = 20;
    public const int MaximumBoxWidthPercent = 90;

    // Boxes as they have always been drawn: 60 %, with their means and outliers marked.
    public static BoxPlotOptions Default { get; } = new();

    // A box width of MinimumBoxWidthPercent to MaximumBoxWidthPercent.
    public bool IsValid => BoxWidthPercent is >= MinimumBoxWidthPercent and <= MaximumBoxWidthPercent;
}
