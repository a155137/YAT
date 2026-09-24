namespace YAT.Analytics.Statistics;

// The standard normal distribution: its density, the probability of being below a value, and the value a probability
// is below.
//
// Both directions are rational approximations rather than series, so they cost the same on every value and give the
// same answer on every machine:
//   * Cdf uses Hart's approximation (W. D. Hart, 1968), the one West's "Better approximations to cumulative normal
//     functions" popularised.
//   * InverseCdf uses Acklam's approximation, refined once with a Halley step through Cdf.
// The accuracy that actually matters is pinned down by the tests rather than claimed here.
public static class NormalDistribution
{
    // Beyond this the tail is smaller than a double can hold onto next to 1.
    private const double TailLimit = 37;

    private const double SquareRootOfTwoPi = 2.5066282746310002;

    // Where Acklam's approximation switches between its tail and central forms.
    private const double LowerBreak = 0.02425;

    private static readonly double[] CentralNumerator =
    [
        -3.969683028665376e+01, 2.209460984245205e+02, -2.759285104469687e+02,
        1.383577518672690e+02, -3.066479806614716e+01, 2.506628277459239e+00
    ];

    private static readonly double[] CentralDenominator =
    [
        -5.447609879822406e+01, 1.615858368580409e+02, -1.556989798598866e+02,
        6.680131188771972e+01, -1.328068155288572e+01
    ];

    private static readonly double[] TailNumerator =
    [
        -7.784894002430293e-03, -3.223964580411365e-01, -2.400758277161838e+00,
        -2.549732539343734e+00, 4.374664141464968e+00, 2.938163982698783e+00
    ];

    private static readonly double[] TailDenominator =
    [
        7.784695709041462e-03, 3.224671290700398e-01, 2.445134137142996e+00, 3.754408661907416e+00
    ];

    // The density of the standard normal distribution at z: exp(-z^2 / 2) / sqrt(2 pi). Exact rather than approximated,
    // and never negative; far enough out it is 0, and at an infinite score it is 0.
    public static double Pdf(double z)
    {
        if (double.IsNaN(z))
        {
            throw new ArgumentException("A normal score must be a number.", nameof(z));
        }

        return Math.Exp(-z * z / 2) / SquareRootOfTwoPi;
    }

    // The probability that a standard normal value is at most z.
    public static double Cdf(double z)
    {
        if (double.IsNaN(z))
        {
            throw new ArgumentException("A normal score must be a number.", nameof(z));
        }

        if (double.IsPositiveInfinity(z))
        {
            return 1;
        }

        if (double.IsNegativeInfinity(z))
        {
            return 0;
        }

        var absolute = Math.Abs(z);
        double lowerTail;

        if (absolute > TailLimit)
        {
            lowerTail = 0;
        }
        else
        {
            var exponential = Math.Exp(-absolute * absolute / 2);

            if (absolute < 7.07106781186547)
            {
                var numerator = 3.52624965998911e-02 * absolute + 0.700383064443688;
                numerator = (numerator * absolute) + 6.37396220353165;
                numerator = (numerator * absolute) + 33.912866078383;
                numerator = (numerator * absolute) + 112.079291497871;
                numerator = (numerator * absolute) + 221.213596169931;
                numerator = (numerator * absolute) + 220.206867912376;

                var denominator = 8.83883476483184e-02 * absolute + 1.75566716318264;
                denominator = (denominator * absolute) + 16.064177579207;
                denominator = (denominator * absolute) + 86.7807322029461;
                denominator = (denominator * absolute) + 296.564248779674;
                denominator = (denominator * absolute) + 637.333633378831;
                denominator = (denominator * absolute) + 793.826512519948;
                denominator = (denominator * absolute) + 440.413735824752;

                lowerTail = exponential * numerator / denominator;
            }
            else
            {
                // A continued fraction, which is where the rational form above loses its accuracy.
                var fraction = absolute + 0.65;
                fraction = absolute + (4 / fraction);
                fraction = absolute + (3 / fraction);
                fraction = absolute + (2 / fraction);
                fraction = absolute + (1 / fraction);
                lowerTail = exponential / fraction / SquareRootOfTwoPi;
            }
        }

        return z > 0 ? 1 - lowerTail : lowerTail;
    }

    // The value a standard normal distribution is below with probability p. Defined for 0 < p < 1 only: a probability
    // of 0 or 1 has no finite value, and a plotting position never asks for one.
    public static double InverseCdf(double probability)
    {
        if (!double.IsFinite(probability) || probability <= 0 || probability >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(probability), probability, "A normal score is defined for probabilities strictly between 0 and 1.");
        }

        var score = Approximate(probability);

        // One Halley step through the distribution function itself, which removes most of the approximation's error.
        var error = Cdf(score) - probability;
        var scaled = error * SquareRootOfTwoPi * Math.Exp(score * score / 2);
        var refined = score - (scaled / (1 + (score * scaled / 2)));

        // Far out in the tails the step itself overflows; the approximation on its own is the answer there.
        return double.IsFinite(refined) ? refined : score;
    }

    private static double Approximate(double probability)
    {
        if (probability < LowerBreak)
        {
            var q = Math.Sqrt(-2 * Math.Log(probability));
            return Tail(q);
        }

        if (probability > 1 - LowerBreak)
        {
            var q = Math.Sqrt(-2 * Math.Log(1 - probability));
            return -Tail(q);
        }

        var central = probability - 0.5;
        var squared = central * central;

        var numerator = (((((CentralNumerator[0] * squared + CentralNumerator[1]) * squared + CentralNumerator[2]) * squared
            + CentralNumerator[3]) * squared + CentralNumerator[4]) * squared + CentralNumerator[5]) * central;
        var denominator = ((((CentralDenominator[0] * squared + CentralDenominator[1]) * squared + CentralDenominator[2]) * squared
            + CentralDenominator[3]) * squared + CentralDenominator[4]) * squared + 1;

        return numerator / denominator;
    }

    private static double Tail(double q) =>
        (((((TailNumerator[0] * q + TailNumerator[1]) * q + TailNumerator[2]) * q + TailNumerator[3]) * q + TailNumerator[4]) * q
            + TailNumerator[5])
        / ((((TailDenominator[0] * q + TailDenominator[1]) * q + TailDenominator[2]) * q + TailDenominator[3]) * q + 1);
}
