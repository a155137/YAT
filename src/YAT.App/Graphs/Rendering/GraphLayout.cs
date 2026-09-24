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

    // Height of the legend box with all of its entries. Only used when a statistics panel shares the legend's column:
    // on its own the legend keeps the plot's full height to grow into.
    public float LegendHeight { get; init; }

    // Width the statistics panel needs for its widest content (already capped at its maximum width).
    public float StatisticsPanelWidth { get; init; }

    // Height of the band above the plot that holds the labels of vertical reference lines. 0 when there are none, which
    // leaves the layout exactly as it is without them.
    public float ReferenceLabelHeight { get; init; }

    // The legend as measured for arranging it (Task #044): each entry's label in entry order, the title (0 for none),
    // one row, and the widest "… N more" of this legend. Only read when the legend does not stand where and as it
    // always did (see GraphLayoutCalculator).
    public IReadOnlyList<float> LegendLabelWidths { get; init; } = [];

    public float LegendTitleWidth { get; init; }

    public float LegendRowHeight { get; init; }

    public float LegendMoreWidth { get; init; }

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
        Require(LegendHeight, nameof(LegendHeight));
        Require(StatisticsPanelWidth, nameof(StatisticsPanelWidth));
        Require(ReferenceLabelHeight, nameof(ReferenceLabelHeight));
        Require(LegendTitleWidth, nameof(LegendTitleWidth));
        Require(LegendRowHeight, nameof(LegendRowHeight));
        Require(LegendMoreWidth, nameof(LegendMoreWidth));
        ArgumentNullException.ThrowIfNull(LegendLabelWidths);
        foreach (var width in LegendLabelWidths)
        {
            Require(width, nameof(LegendLabelWidths));
        }
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
    public GraphLayout(
        SKRect canvas,
        SKRect titleArea,
        SKRect plotArea,
        SKRect xAxisArea,
        SKRect yAxisArea,
        SKRect legendArea,
        SKRect statisticsPanelArea = default,
        SKRect referenceLabelArea = default)
    {
        Canvas = canvas;
        TitleArea = titleArea;
        PlotArea = plotArea;
        XAxisArea = xAxisArea;
        YAxisArea = yAxisArea;
        LegendArea = legendArea;
        StatisticsPanelArea = statisticsPanelArea;
        ReferenceLabelArea = referenceLabelArea;
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

    // Where the legend stands: right of the plot area unless the model puts it on another side (Task #044). Empty when
    // the model has no legend.
    public SKRect LegendArea { get; }

    // How the legend's entries are arranged in LegendArea, in its own coordinates - or null for a legend on the right
    // that its single column holds whole, which is drawn exactly as a legend always was.
    public GraphLegendArrangement? LegendArrangement { get; init; }

    // Right of the plot area, below the legend when that is on the right too. Empty when the model has no statistics
    // panel.
    public SKRect StatisticsPanelArea { get; }

    // Directly above the plot area and as wide as it: the labels of vertical reference lines, each over its line. Empty
    // when the model has none.
    public SKRect ReferenceLabelArea { get; }

    public bool HasPlotArea => PlotArea.Width > 0 && PlotArea.Height > 0;
}
