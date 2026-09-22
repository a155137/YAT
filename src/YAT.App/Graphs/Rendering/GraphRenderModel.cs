namespace YAT.app.Graphs.Rendering;

// The value range one axis covers. A range is always finite and non-empty: whatever builds a render model (analytics
// results today, a graph pipeline later) must decide what to show when data cannot produce a usable range, so nothing
// further down has to invent one. The default struct value is not a range; use IsValid before trusting one.
public readonly record struct GraphAxisRange
{
    public GraphAxisRange(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum))
        {
            throw new ArgumentException("The axis minimum must be a finite number.", nameof(minimum));
        }

        if (!double.IsFinite(maximum))
        {
            throw new ArgumentException("The axis maximum must be a finite number.", nameof(maximum));
        }

        if (maximum <= minimum)
        {
            throw new ArgumentException($"The axis maximum ({maximum}) must be greater than the minimum ({minimum}).", nameof(maximum));
        }

        if (!double.IsFinite(maximum - minimum))
        {
            throw new ArgumentException("The axis range is too wide to be represented.", nameof(maximum));
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    public double Minimum { get; }

    public double Maximum { get; }

    public double Span => Maximum - Minimum;

    // False for the default value of the struct, which no constructor produced.
    public bool IsValid => double.IsFinite(Minimum) && double.IsFinite(Maximum) && Maximum > Minimum && double.IsFinite(Maximum - Minimum);
}

// One major tick: where it sits in data values, and the text drawn beside it. The label is chosen when the model is
// built, never by the renderer.
public sealed record GraphAxisTick
{
    public GraphAxisTick(double value, string label)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("A tick value must be a finite number.", nameof(value));
        }

        ArgumentNullException.ThrowIfNull(label);
        Value = value;
        Label = label;
    }

    public double Value { get; }

    public string Label { get; }
}

// One axis of a graph: the range it covers, its major ticks and an optional title. Ticks outside the range are allowed
// but are not drawn; nothing here knows about pixels.
public sealed record GraphAxisModel
{
    public GraphAxisModel(GraphAxisRange range, IReadOnlyList<GraphAxisTick> ticks, string? title = null)
    {
        if (!range.IsValid)
        {
            throw new ArgumentException("The axis range must be finite and non-empty.", nameof(range));
        }

        ArgumentNullException.ThrowIfNull(ticks);
        if (ticks.Any(tick => tick is null))
        {
            throw new ArgumentException("An axis tick must not be null.", nameof(ticks));
        }

        Range = range;
        Ticks = [.. ticks];
        Title = title;
    }

    public GraphAxisRange Range { get; }

    public IReadOnlyList<GraphAxisTick> Ticks { get; }

    // Null or empty when the axis has no title; the layout then gives the space back to the plot area.
    public string? Title { get; }
}

// One entry of a legend. The series index selects the colour from the theme's palette, so the model stays free of
// rendering resources and the same model can be drawn in any theme.
public sealed record GraphLegendEntry
{
    public GraphLegendEntry(string label, int seriesIndex)
    {
        ArgumentNullException.ThrowIfNull(label);
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        Label = label;
        SeriesIndex = seriesIndex;
    }

    public string Label { get; }

    public int SeriesIndex { get; }
}

// The legend of a grouped graph. A graph without series has no legend model at all (null), so an empty legend is never
// laid out or drawn.
public sealed record GraphLegendModel
{
    public GraphLegendModel(IReadOnlyList<GraphLegendEntry> entries, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException("A legend must have at least one entry; use no legend instead.", nameof(entries));
        }

        if (entries.Any(entry => entry is null))
        {
            throw new ArgumentException("A legend entry must not be null.", nameof(entries));
        }

        Entries = [.. entries];
        Title = title;
    }

    public IReadOnlyList<GraphLegendEntry> Entries { get; }

    public string? Title { get; }
}

// Everything a graph needs in order to be drawn, and nothing about how it was computed: no worksheet, no observations,
// no statistics. Building one is the job of the layer above the renderer; drawing one is the job of the renderer.
public sealed record GraphRenderModel
{
    public GraphRenderModel(string? title, GraphAxisModel xAxis, GraphAxisModel yAxis, GraphLegendModel? legend = null)
    {
        ArgumentNullException.ThrowIfNull(xAxis);
        ArgumentNullException.ThrowIfNull(yAxis);
        Title = title;
        XAxis = xAxis;
        YAxis = yAxis;
        Legend = legend;
    }

    // Null or empty when the graph has no title; the layout then gives the space back to the plot area.
    public string? Title { get; }

    // Private set only so WithXAxis can replace it on a copy; a model never changes after it is made.
    public GraphAxisModel XAxis { get; private set; }

    public GraphAxisModel YAxis { get; }

    public GraphLegendModel? Legend { get; }

    // The statistics shown beside the plot, or null for none. It is part of the frame, like the legend, so wherever the
    // frame is drawn - the graph window, a PNG, a presentation - the panel is drawn with it.
    //
    // A graph type's own builder produces its frame without a panel; the graph preparation adds it (see
    // GraphStatisticsPanelBuilder.Attach), so the frame a window shows may carry a panel its render model's frame does
    // not.
    public GraphStatisticsPanel? StatisticsPanel { get; init; }

    public GraphRenderModel WithStatisticsPanel(GraphStatisticsPanel? panel) => this with { StatisticsPanel = panel };

    // Lines across the plot at chosen axis values (see GraphReferenceLine), in the order they were given. Empty for
    // none. Like the statistics panel they are added by the graph preparation (GraphSpecificationLinesBuilder), never
    // by a graph type's own builder.
    public IReadOnlyList<GraphReferenceLine> ReferenceLines { get; init; } = [];

    public GraphRenderModel WithReferenceLines(IReadOnlyList<GraphReferenceLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Any(line => line is null))
        {
            throw new ArgumentException("A reference line must not be null.", nameof(lines));
        }

        return this with { ReferenceLines = [.. lines] };
    }

    // The same model over another X axis: what a frame becomes when something drawn on it needs the axis to reach
    // further than the data does. Everything else - title, Y axis, legend, panel, lines - is kept.
    public GraphRenderModel WithXAxis(GraphAxisModel xAxis)
    {
        ArgumentNullException.ThrowIfNull(xAxis);
        var copy = this with { };
        copy.XAxis = xAxis;
        return copy;
    }
}
