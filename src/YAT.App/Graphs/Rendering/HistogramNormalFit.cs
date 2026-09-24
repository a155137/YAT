using YAT.Analytics.Statistics;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// One point of a histogram's normal fit: a value on the X axis and the curve's height there, on the bars' Y scale.
public readonly record struct HistogramCurvePoint(double X, double Height);

// The normal curve fitted to one histogram series (Task #042): the normal distribution of the series' own mean and
// sample standard deviation, drawn on the same Y scale as its bars.
//
// With the bins' width w and the standard normal density φ, at z = (x - mean) / standard deviation:
//
//     Frequency   N x w x φ(z) / standard deviation   (the count a bin of width w centred at x is expected to hold)
//     Percent     100 x w x φ(z) / standard deviation (the same count as a percent of the series' N)
//     Density     φ(z) / standard deviation           (the normal density itself: unit area, like the density bars)
//
// The curve is sampled at PointCount evenly spaced points over mean ± SampledStandardDeviations standard deviations -
// the mean itself among them - so its shape depends on the fit alone, never on the pixels it is drawn on or on the
// range of the X axis: the plot area clips whatever of it lies outside.
public sealed record HistogramNormalFit
{
    // An odd number, so the middle point is the mean and the peak is always sampled.
    public const int PointCount = 201;

    // Out here the curve is below 0.04 % of its peak: less than a pixel on any plot.
    public const int SampledStandardDeviations = 4;

    private const int Middle = PointCount / 2;

    public HistogramNormalFit(double mean, double standardDeviation, IReadOnlyList<HistogramCurvePoint> points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!double.IsFinite(mean))
        {
            throw new ArgumentException("A normal fit needs a finite mean.", nameof(mean));
        }

        if (!double.IsFinite(standardDeviation) || standardDeviation <= 0)
        {
            throw new ArgumentException(
                "A normal fit needs a finite standard deviation greater than zero.", nameof(standardDeviation));
        }

        if (points.Count < 2)
        {
            throw new ArgumentException("A normal fit needs at least two points to draw.", nameof(points));
        }

        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Height) || point.Height < 0)
            {
                throw new ArgumentException(
                    "Every point of a normal fit must be finite, and no lower than zero.", nameof(points));
            }

            if (index > 0 && !(point.X > points[index - 1].X))
            {
                throw new ArgumentException(
                    "The points of a normal fit must be in strictly increasing X order.", nameof(points));
            }
        }

        Mean = mean;
        StandardDeviation = standardDeviation;
        Points = [.. points];
        MaximumHeight = Points.Max(point => point.Height);
    }

    public double Mean { get; }

    // The sample standard deviation (n - 1), as the statistics panel shows it.
    public double StandardDeviation { get; }

    public IReadOnlyList<HistogramCurvePoint> Points { get; }

    // The highest point of the curve: what the Y axis has to reach to show all of it.
    public double MaximumHeight { get; }

    // The fit of one series' observations on the histogram's Y scale, or null when these observations have no normal
    // fit that can be drawn: fewer than two of them, no spread, a mean or standard deviation that is not finite, a
    // sampling range the numbers cannot tell apart, or a curve too tall for a double. A series without a fit is still
    // drawn; it only has no curve.
    //
    // The mean and standard deviation are Analytics' own (Descriptives) over the same observations in the same order
    // as the statistics panel's, so the two always agree. Nothing is sorted: a few passes over the values, then the
    // curve's fixed number of points.
    public static HistogramNormalFit? Fit(ReadOnlySpan<double> values, HistogramYScale scale, double binWidth)
    {
        if (values.Length < 2)
        {
            return null;
        }

        var mean = Descriptives.Mean(values);
        var standardDeviation = Descriptives.StandardDeviation(values);
        if (!double.IsFinite(mean) || !double.IsFinite(standardDeviation) || standardDeviation <= 0)
        {
            return null;
        }

        // What the density is multiplied by to read on the bars' scale.
        var factor = scale switch
        {
            HistogramYScale.Percent => 100d * binWidth,
            HistogramYScale.Density => 1d,
            _ => values.Length * binWidth
        };

        if (!double.IsFinite(factor) || factor <= 0)
        {
            return null;
        }

        var points = new HistogramCurvePoint[PointCount];
        for (var index = 0; index < PointCount; index++)
        {
            // The score of each point comes from its index alone, symmetric about the mean (where it is 0): the curve
            // keeps its shape even where the X values themselves are rounded, as they are on data with a large offset.
            var z = (index - Middle) * SampledStandardDeviations / (double)Middle;
            var x = mean + (z * standardDeviation);
            var height = factor * NormalDistribution.Pdf(z) / standardDeviation;

            // A standard deviation below the resolution of the mean leaves X values that cannot be told apart, and one
            // tiny enough makes the peak overflow: neither is a curve that can be drawn.
            if (!double.IsFinite(x) || !double.IsFinite(height) || height < 0
                || (index > 0 && !(x > points[index - 1].X)))
            {
                return null;
            }

            points[index] = new HistogramCurvePoint(x, height);
        }

        return new HistogramNormalFit(mean, standardDeviation, points);
    }
}
