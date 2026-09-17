using System.Globalization;

namespace YAT.app.Graphs.Rendering;

// A whole-number axis and the ticks that belong to it: what GraphAxisTicks.NiceCounts works out for a frequency axis.
public sealed record GraphCountAxis(GraphAxisRange Range, IReadOnlyList<GraphAxisTick> Ticks);

// Axis tick policy: turns a range into the major ticks a graph shows.
//
// Nice produces the engineering scale a reader expects - steps of 1, 2 or 5 times a power of ten, so an axis reads
// 0, 20, 40, 60, 80, 100 instead of 3.7, 23.42, 43.14 - and is what the graphs use. Evenly simply divides the range and
// stays for callers that want exactly that.
//
// Labels are formatted once per axis so every tick of one axis reads the same way, always with the invariant culture.
public static class GraphAxisTicks
{
    public const int DefaultCount = 6;

    // Intervals aimed for by Nice. The 1-2-5 rounding then lands between roughly 4 and 9 ticks, depending on the range.
    public const int DefaultIntervals = 5;

    // A sane upper bound: a 1-2-5 step over its own range produces about a dozen ticks, so more than this means the
    // range and the step no longer relate to each other (values at the limits of double precision).
    private const int MaximumTicks = 1000;

    // Values at or beyond this magnitude, and steps below the small one, read better in scientific notation.
    private const double LargeValue = 1e6;
    private const double SmallStep = 1e-4;

    private const string ScientificFormat = "0.###E+0";
    private const string DefaultFormat = "0.####";

    // The major ticks of range on a 1-2-5 scale: a step of the form 1, 2 or 5 times a power of ten, and every multiple
    // of that step inside the range. The ends of the range are not forced to be ticks.
    public static IReadOnlyList<GraphAxisTick> Nice(GraphAxisRange range, int intervals = DefaultIntervals)
    {
        if (!range.IsValid)
        {
            throw new ArgumentException("The axis range must be finite and non-empty.", nameof(range));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(intervals, 2);

        var step = NiceStep(range.Span / intervals);
        if (!double.IsFinite(step) || step <= 0)
        {
            return Evenly(range, 2);
        }

        var first = Math.Ceiling(range.Minimum / step);
        var last = Math.Floor(range.Maximum / step);
        if (!double.IsFinite(first) || !double.IsFinite(last) || last < first)
        {
            return Evenly(range, 2);
        }

        // Counted, never walked: past 2^53 a double no longer changes when 1 is added to it, and a loop over the
        // multiples themselves would not end.
        var count = last - first + 1;
        if (!double.IsFinite(count) || count < 2 || count > MaximumTicks)
        {
            return Evenly(range, 2);
        }

        var values = new List<double>((int)count);
        for (var index = 0; index < (int)count; index++)
        {
            var value = (first + index) * step;
            if (double.IsFinite(value))
            {
                // -0 and +0 are the same tick; only one of them reads as "0".
                values.Add(value == 0 ? 0 : value);
            }
        }

        // A range too narrow to contain two multiples of its own step says nothing; even division is then clearer.
        return values.Count >= 2 ? Label(values, step) : Evenly(range, 2);
    }

    // The axis a count is read on: it starts at zero, every tick is a whole number, the step is at least one, and the
    // top of the axis is at or above the largest count, so the tallest bar is never cut off.
    //
    //     largest count 83  ->  0, 20, 40, 60, 80, 100
    public static GraphCountAxis NiceCounts(int maximumCount, int intervals = DefaultIntervals)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(intervals, 2);

        // Half a count means nothing, so the 1-2-5 step is rounded up to a whole number.
        var step = Math.Max(1, (int)NiceStep(Math.Max(maximumCount, 1) / (double)intervals));
        var maximum = Math.Max(step, (int)(Math.Ceiling(maximumCount / (double)step) * step));

        var ticks = new List<GraphAxisTick>((maximum / step) + 1);
        for (var value = 0; value <= maximum; value += step)
        {
            ticks.Add(new GraphAxisTick(value, value.ToString(CultureInfo.InvariantCulture)));
        }

        return new GraphCountAxis(new GraphAxisRange(0, maximum), ticks);
    }

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

    // The nearest 1, 2 or 5 times a power of ten: the steps people read scales in.
    private static double NiceStep(double step)
    {
        if (!double.IsFinite(step) || step <= 0)
        {
            return step;
        }

        var exponent = Math.Floor(Math.Log10(step));
        var power = Math.Pow(10, exponent);
        var fraction = step / power;

        var nice = fraction switch
        {
            < 1.5 => 1d,
            < 3 => 2d,
            < 7 => 5d,
            _ => 10d
        };

        return nice * power;
    }

    // One format for the whole axis: the number of decimals the step needs, or scientific notation when the values are
    // large or the step is tiny. Mixing the two on one axis would make the scale harder to read, not easier.
    private static IReadOnlyList<GraphAxisTick> Label(IReadOnlyList<double> values, double step)
    {
        var largest = values.Max(Math.Abs);
        var format = largest >= LargeValue || step < SmallStep
            ? ScientificFormat
            : "F" + Math.Max(0, -(int)Math.Floor(Math.Log10(step)));

        var ticks = new GraphAxisTick[values.Count];
        for (var index = 0; index < values.Count; index++)
        {
            ticks[index] = new GraphAxisTick(values[index], values[index].ToString(format, CultureInfo.InvariantCulture));
        }

        return ticks;
    }
}
