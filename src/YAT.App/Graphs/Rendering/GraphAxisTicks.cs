using System.Globalization;

namespace YAT.app.Graphs.Rendering;

// Axis tick policy: turns a range into the major ticks a graph shows. V1 spaces them evenly over the range, which is
// deterministic and enough for the first graph types. A nicer algorithm (round tick values, tick counts chosen from the
// available space) becomes another method here; the render model carries explicit ticks either way, so no renderer or
// graph type has to change when that arrives.
public static class GraphAxisTicks
{
    public const int DefaultCount = 6;

    // Labels are formatted invariantly so the same range always produces the same text.
    private const string DefaultFormat = "0.####";

    // count ticks over the whole range, the first at Minimum and the last at Maximum.
    public static IReadOnlyList<GraphAxisTick> Evenly(GraphAxisRange range, int count = DefaultCount, string? format = null)
    {
        if (!range.IsValid)
        {
            throw new ArgumentException("The axis range must be finite and non-empty.", nameof(range));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(count, 2);

        var ticks = new GraphAxisTick[count];
        for (var index = 0; index < count; index++)
        {
            // The last tick is taken from Maximum directly: accumulating the step would leave it a rounding error short.
            var value = index == count - 1
                ? range.Maximum
                : range.Minimum + (range.Span * index / (count - 1));

            ticks[index] = new GraphAxisTick(value, Label(value, format));
        }

        return ticks;
    }

    public static string Label(double value, string? format = null) =>
        value.ToString(format ?? DefaultFormat, CultureInfo.InvariantCulture);
}
