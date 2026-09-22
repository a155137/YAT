namespace YAT.app.Graphs.Rendering;

// The axis a reference line marks a value on. An X line is drawn vertically at its X value, a Y line horizontally at
// its Y value.
public enum GraphReferenceAxis
{
    X,
    Y
}

// What a reference line stands for, which decides how it is drawn. The kind is presentation: what the value means was
// settled before the line was made.
public enum GraphReferenceLineKind
{
    // A lower or upper specification limit.
    SpecificationLimit,

    // The value a specification aims at.
    Target
}

// A straight line across the plot at one value of one axis, with the text that names it. Like the legend and the
// statistics panel it belongs to the frame, not to a graph type: whatever the plot shows, the frame renderer draws the
// line through the same coordinate transform, so no graph type draws lines of its own.
//
// The label is decided when the line is made ("LSL 14.5"); the renderer only places it.
public sealed record GraphReferenceLine
{
    public GraphReferenceLine(GraphReferenceAxis axis, double value, string label, GraphReferenceLineKind kind)
    {
        if (!double.IsFinite(value))
        {
            throw new ArgumentException("A reference line value must be a finite number.", nameof(value));
        }

        ArgumentNullException.ThrowIfNull(label);
        Axis = axis;
        Value = value;
        Label = label;
        Kind = kind;
    }

    public GraphReferenceAxis Axis { get; }

    public double Value { get; }

    public string Label { get; }

    public GraphReferenceLineKind Kind { get; }
}
