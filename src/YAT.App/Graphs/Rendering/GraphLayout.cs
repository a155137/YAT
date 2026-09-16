using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// The measured sizes a layout needs. The renderer measures text with the fonts it is about to draw with and passes the
// results in, so the layout arithmetic stays free of font handling and can be tested on its own.
public sealed record GraphLayoutMetrics
{
    // Space kept clear around the whole graph.
    public float OuterPadding { get; init; } = 12f;

    // Space between neighbouring elements (tick to label, label to axis title, title to plot).
    public float Gap { get; init; } = 6f;

    public float TickLength { get; init; } = 5f;

    public float TitleHeight { get; init; }

    public float AxisTitleHeight { get; init; }

    public float TickLabelHeight { get; init; }

    // Width of the widest Y tick label; the Y labels are drawn to the left of the plot area.
    public float YTickLabelWidth { get; init; }

    // Half the width of the widest X tick label: an X label is centred on its tick, so the last one would otherwise
    // reach past the right edge of the canvas.
    public float XTickLabelOverflow { get; init; }

    public float LegendWidth { get; init; }

    // Rejects sizes that would produce meaningless rectangles. Zero is valid and means "this element is not shown".
    internal void EnsureValid()
    {
        Require(OuterPadding, nameof(OuterPadding));
        Require(Gap, nameof(Gap));
        Require(TickLength, nameof(TickLength));
        Require(TitleHeight, nameof(TitleHeight));
        Require(AxisTitleHeight, nameof(AxisTitleHeight));
        Require(TickLabelHeight, nameof(TickLabelHeight));
        Require(YTickLabelWidth, nameof(YTickLabelWidth));
        Require(XTickLabelOverflow, nameof(XTickLabelOverflow));
        Require(LegendWidth, nameof(LegendWidth));
    }

    private static void Require(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "A layout metric must be a finite, non-negative number.");
        }
    }
}

// Where each part of a graph is drawn, in the canvas coordinates of the control. The areas never overlap and all of
// them lie inside Canvas; the space between them (outer padding, the gap kept for labels that overhang the plot) is
// deliberately unassigned.
//
// When the canvas is too small for a usable plot, HasPlotArea is false and every area except Canvas is empty: the
// renderer then paints the background only, instead of drawing axes on top of each other.
public sealed record GraphLayout
{
    public GraphLayout(SKRect canvas, SKRect titleArea, SKRect plotArea, SKRect xAxisArea, SKRect yAxisArea, SKRect legendArea)
    {
        Canvas = canvas;
        TitleArea = titleArea;
        PlotArea = plotArea;
        XAxisArea = xAxisArea;
        YAxisArea = yAxisArea;
        LegendArea = legendArea;
    }

    public SKRect Canvas { get; }

    // The graph title. Empty when the model has no title.
    public SKRect TitleArea { get; }

    // Where the data itself is drawn; also the rectangle a GraphCoordinateTransform maps onto.
    public SKRect PlotArea { get; }

    // Below the plot area: X ticks, X tick labels and the X axis title.
    public SKRect XAxisArea { get; }

    // Left of the plot area: Y ticks, Y tick labels and the Y axis title.
    public SKRect YAxisArea { get; }

    // Right of the plot area. Empty when the model has no legend.
    public SKRect LegendArea { get; }

    public bool HasPlotArea => PlotArea.Width > 0 && PlotArea.Height > 0;
}
