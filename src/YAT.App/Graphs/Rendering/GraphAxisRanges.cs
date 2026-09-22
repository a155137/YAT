namespace YAT.app.Graphs.Rendering;

// Axis range policy: turns the values a graph actually has into the range it is drawn over. Graphs never plot straight
// onto their extremes - a point exactly on the axis is hard to read - so the data range is padded, and data that has no
// range at all (one repeated value) still gets a usable one.
//
// The policy is presentation, not statistics: it decides what to show, never what the data means.
public static class GraphAxisRanges
{
    // Added at both ends, as a fraction of the data span: 0..100 is drawn as -5..105.
    public const double PaddingFraction = 0.05;

    // Half the width given to a constant non-zero value, as a fraction of its magnitude: 100 is drawn as 99.5..100.5.
    public const double ConstantHalfSpanFraction = 0.005;

    // Half the width given to a constant zero (and to any value too small for the fraction above to change it).
    public const double ZeroHalfSpan = 0.5;

    // The range to draw values between minimum and maximum over. Both must be finite; maximum may equal minimum.
    public static GraphAxisRange FromValues(double minimum, double maximum)
    {
        if (!double.IsFinite(minimum))
        {
            throw new ArgumentException("The smallest value must be finite.", nameof(minimum));
        }

        if (!double.IsFinite(maximum))
        {
            throw new ArgumentException("The largest value must be finite.", nameof(maximum));
        }

        if (maximum < minimum)
        {
            throw new ArgumentException($"The largest value ({maximum}) must not be smaller than the smallest ({minimum}).", nameof(maximum));
        }

        if (minimum == maximum)
        {
            return Constant(minimum);
        }

        var padding = (maximum - minimum) * PaddingFraction;

        // Values near the limits of double can pad or subtract into infinity; the unpadded data range is then the
        // honest answer, and a range that cannot be represented at all falls back to the constant policy.
        return TryCreate(minimum - padding, maximum + padding)
            ?? TryCreate(minimum, maximum)
            ?? Constant(minimum);
    }

    // The range a graph is drawn over when these values have to be seen on it too (a specification's lines): range
    // itself when every value already lies strictly inside it, otherwise range reached out to the values.
    //
    // Only a side a value reaches or passes is moved, and it is moved past that value by the usual padding - a fraction
    // of the new span - so a line is never drawn on the axis itself. The other side keeps the boundary the graph chose
    // for its data. Because every value then lies strictly inside, asking again with the result changes nothing.
    //
    // When the reached-out range cannot be represented (values near the limits of double), or the padding is too small
    // to move the edge off the value at all, range is returned unchanged: the graph keeps its own axis, and a value
    // outside it is simply not seen.
    public static GraphAxisRange Including(GraphAxisRange range, IReadOnlyList<double> values)
    {
        if (!range.IsValid)
        {
            throw new ArgumentException("The axis range must be finite and non-empty.", nameof(range));
        }

        ArgumentNullException.ThrowIfNull(values);
        if (values.Any(value => !double.IsFinite(value)))
        {
            throw new ArgumentException("Every value must be finite.", nameof(values));
        }

        if (values.Count == 0)
        {
            return range;
        }

        var lowest = values.Min();
        var highest = values.Max();
        var extendsDown = lowest <= range.Minimum;
        var extendsUp = highest >= range.Maximum;
        if (!extendsDown && !extendsUp)
        {
            return range;
        }

        var span = (extendsUp ? highest : range.Maximum) - (extendsDown ? lowest : range.Minimum);
        var padding = span * PaddingFraction;
        if (!double.IsFinite(padding) || padding <= 0)
        {
            return range;
        }

        var minimum = extendsDown ? lowest - padding : range.Minimum;
        var maximum = extendsUp ? highest + padding : range.Maximum;

        // The padding has to separate the edge from the value; at a magnitude where it rounds away, it cannot.
        if (TryCreate(minimum, maximum) is not { } reached || !(reached.Minimum < lowest) || !(reached.Maximum > highest))
        {
            return range;
        }

        return reached;
    }

    // One repeated value: a window around it, wide enough to be a range and narrow enough to keep the value's scale.
    private static GraphAxisRange Constant(double value)
    {
        var halfSpan = Math.Abs(value) * ConstantHalfSpanFraction;
        if (!double.IsFinite(halfSpan) || halfSpan <= 0)
        {
            halfSpan = ZeroHalfSpan;
        }

        return TryCreate(value - halfSpan, value + halfSpan)
            ?? TryCreate(value - ZeroHalfSpan, value + ZeroHalfSpan)
            ?? new GraphAxisRange(-ZeroHalfSpan, ZeroHalfSpan);
    }

    private static GraphAxisRange? TryCreate(double minimum, double maximum) =>
        double.IsFinite(minimum) && double.IsFinite(maximum) && maximum > minimum && double.IsFinite(maximum - minimum)
            ? new GraphAxisRange(minimum, maximum)
            : null;
}
