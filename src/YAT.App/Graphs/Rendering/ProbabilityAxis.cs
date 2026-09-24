using System.Globalization;
using YAT.Analytics.Statistics;

namespace YAT.app.Graphs.Rendering;

// The vertical axis of a probability plot.
//
// What it measures is a normal score, but what it is read in is percent, so the ticks are chosen percentages placed at
// the score each of them belongs to. That is what makes the spacing uneven - the step from 50% to 70% is much shorter
// than the one from 99% to 99.9% - and it is what makes normally distributed data fall on a straight line.
public static class ProbabilityAxis
{
    public const string Title = "Percent";

    // Kept clear above and below what has to be shown, as a fraction of the score range, so the extreme observations
    // are not drawn on the frame.
    public const double PaddingFraction = 0.02;

    // The percentages a probability plot is always read at.
    public static readonly IReadOnlyList<double> StandardPercents =
        [0.1, 0.5, 1, 2, 5, 10, 20, 30, 50, 70, 80, 90, 95, 98, 99, 99.5, 99.9];

    // Percentages for the far tails. A large sample reaches past 0.1% and 99.9% - a million observations reach about
    // five scores out - and without these the ends of such a plot would have no labels at all. They appear only when
    // the axis actually reaches them.
    public static readonly IReadOnlyList<double> TailPercents =
        [0.0001, 0.001, 0.01, 99.99, 99.999, 99.9999];

    private const string PercentFormat = "0.######";

    // The normal score a percentage sits at.
    public static double Score(double percent) => NormalDistribution.InverseCdf(percent / 100);

    // The percentage a normal score belongs to: what a place on the axis reads as.
    public static double Percent(double score) => NormalDistribution.Cdf(score) * 100;

    // The ticks of the axis over any score range, as a user may choose it (Task #043): the percentages the axis is
    // always read at - the tail ones included - that lie inside the range. A range too narrow to hold two of them is
    // read on 1-2-5 percentages instead (40, 45, 50, 55, 60), each placed at its own score; the labels are always
    // percentages.
    public static IReadOnlyList<GraphAxisTick> Ticks(GraphAxisRange scoreRange)
    {
        if (!scoreRange.IsValid)
        {
            throw new ArgumentException("The axis range must be finite and non-empty.", nameof(scoreRange));
        }

        var percents = StandardPercents.Concat(TailPercents)
            .Where(percent => Score(percent) is var score && score >= scoreRange.Minimum && score <= scoreRange.Maximum)
            .Order()
            .ToList();
        if (percents.Count >= 2)
        {
            return
            [
                .. percents.Select(percent =>
                    new GraphAxisTick(Score(percent), percent.ToString(PercentFormat, CultureInfo.InvariantCulture)))
            ];
        }

        // The ends read back as percentages, let out by a hair: a percentage typed as an end (40) comes back from its
        // score a rounding error inside (40.0000000001), and would otherwise lose its own tick.
        var lowest = Percent(scoreRange.Minimum);
        var highest = Percent(scoreRange.Maximum);
        if (!(highest > lowest))
        {
            return [];
        }

        var slack = (highest - lowest) * 1e-9;
        lowest = Math.Max(lowest - slack, double.Epsilon);
        highest = Math.Min(highest + slack, 100);

        return
        [
            .. GraphAxisTicks.Nice(new GraphAxisRange(lowest, highest))
                .Where(tick => tick.Value is > 0 and < 100 && Score(tick.Value) is var score
                    && double.IsFinite(score) && score >= scoreRange.Minimum && score <= scoreRange.Maximum)
                .Select(tick => new GraphAxisTick(Score(tick.Value), tick.Label))
        ];
    }

    // The axis for scores between minimumScore and maximumScore: at least the standard range of 0.1% to 99.9%, wider
    // when the data is, with the tail percentages that the result reaches.
    public static GraphAxisModel Axis(double minimumScore, double maximumScore)
    {
        if (!double.IsFinite(minimumScore) || !double.IsFinite(maximumScore))
        {
            throw new ArgumentException("A probability axis needs finite scores.", nameof(minimumScore));
        }

        if (maximumScore < minimumScore)
        {
            throw new ArgumentException(
                $"The largest score ({maximumScore}) must not be smaller than the smallest ({minimumScore}).", nameof(maximumScore));
        }

        var lower = Math.Min(Score(StandardPercents[0]), minimumScore);
        var upper = Math.Max(Score(StandardPercents[^1]), maximumScore);
        var padding = (upper - lower) * PaddingFraction;
        var range = new GraphAxisRange(lower - padding, upper + padding);

        var percents = new List<double>(StandardPercents);
        foreach (var percent in TailPercents)
        {
            var score = Score(percent);
            if (score >= range.Minimum && score <= range.Maximum)
            {
                percents.Add(percent);
            }
        }

        percents.Sort();

        var ticks = new List<GraphAxisTick>(percents.Count);
        foreach (var percent in percents)
        {
            ticks.Add(new GraphAxisTick(Score(percent), percent.ToString(PercentFormat, CultureInfo.InvariantCulture)));
        }

        return new GraphAxisModel(range, ticks, Title) { Scale = GraphAxisScale.Probability };
    }
}
