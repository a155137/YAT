using System.Globalization;

namespace YAT.App.Tests.Robustness;

// A small deterministic random number generator for test data: SplitMix64. It is written out here rather than taken
// from System.Random because a seeded System.Random is not guaranteed to produce the same sequence across .NET
// versions, and a generated failure must replay the same way on every machine.
internal sealed class RobustnessRandom(ulong seed)
{
    private ulong _state = seed;

    public ulong NextUInt64()
    {
        var z = _state += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    // Uniform in [0, 1).
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    // Uniform integer in [0, exclusiveMaximum).
    public int Next(int exclusiveMaximum) => (int)(NextDouble() * exclusiveMaximum);

    public T Pick<T>(IReadOnlyList<T> items) => items[Next(items.Count)];

    // Standard normal (Box-Muller); the uniform is kept away from 0 so the logarithm stays finite.
    public double NextGaussian()
    {
        var u1 = Math.Max(NextDouble(), 1e-12);
        var u2 = NextDouble();
        return Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    // A seed of its own for every (seed, case) pair, so each case can be rebuilt without generating the ones before it.
    public static ulong Mix(ulong seed, int caseNumber) => new RobustnessRandom(seed ^ (0xD1B54A32D192ED03UL * (ulong)(caseNumber + 1))).NextUInt64();
}

// Generated robustness cases, biased towards what measured semiconductor data looks like rather than towards arbitrary
// doubles: large offsets with small variation, readings quantized to an instrument resolution, a handful of distinct
// values repeated many times, tails and outliers, empty cells, and groups (sites, lots) of very different sizes.
//
// A case is fully determined by (seed, caseNumber). The family is chosen round-robin from the case number, so every
// family is exercised at every scale of test run.
internal static class RobustnessGenerator
{
    // The fixed primary seed of the harness.
    public const ulong PrimarySeed = 20260922;

    private static readonly string[] Families =
    [
        "Normal", "Quantized", "FewUnique", "Tailed", "Bimodal", "Skewed", "CrossZero", "LargeOffsetTinySpread", "Constant"
    ];

    private static readonly double[] Offsets = [0, 1e-3, 1, 1e3, 1.5e4, 1e6];
    private static readonly double[] RelativeSpreads = [1e-6, 1e-5, 1e-4, 1e-3, 1e-2, 1e-1];
    private static readonly double[] MissingRatios = [0, 0, 0.1, 0.5, 0.9];
    private static readonly double[] TailRatios = [0, 0.01, 0.05];
    private static readonly double[] TailMagnitudes = [10, 100, 1e4];
    private static readonly int[] UniqueCounts = [1, 2, 3, 4, 7, 16];
    private static readonly int[] GroupCounts = [0, 0, 1, 2, 3, 5, 8];

    // Most cases are small or medium, a few reach the largest size: small cases are where edge cases live, and they are
    // cheap.
    private static readonly int[] SmallSizes = [1, 2, 3, 4, 5, 8, 16, 50, 200, 1000];

    // The case with this number, of at most maximumRows rows.
    public static RobustnessCase Create(int caseNumber, int maximumRows, ulong seed = PrimarySeed)
    {
        var random = new RobustnessRandom(RobustnessRandom.Mix(seed, caseNumber));
        var family = Families[caseNumber % Families.Length];

        var rows = random.NextDouble() < 0.85 ? random.Pick(SmallSizes) : 1 + random.Next(maximumRows);
        rows = Math.Min(rows, maximumRows);

        var offset = random.Pick(Offsets) * (random.NextDouble() < 0.5 ? 1 : -1);
        var scale = Math.Max(Math.Abs(offset), 1) * random.Pick(RelativeSpreads);
        var quantized = family is "Quantized" or "FewUnique" || random.NextDouble() < 0.3;
        var step = quantized ? scale * random.Pick((double[])[0.01, 0.1, 0.5]) : 0;
        var unique = random.Pick(UniqueCounts);
        var missing = random.Pick(MissingRatios);
        var tailRatio = family == "Tailed" ? random.Pick((double[])[0.01, 0.05]) : random.Pick(TailRatios);
        var tailMagnitude = random.Pick(TailMagnitudes);
        var groupCount = random.Pick(GroupCounts);
        var zipf = random.NextDouble() < 0.5;
        var missingGroups = groupCount > 0 && random.NextDouble() < 0.3 ? 0.1 : 0;

        if (family == "CrossZero")
        {
            offset = 0;
            scale = random.Pick((double[])[1e-9, 1e-7, 1e-3, 1]);
            step = random.NextDouble() < 0.5 ? scale / 10 : 0;
        }

        if (family == "LargeOffsetTinySpread")
        {
            offset = random.Pick((double[])[1.5e4, 1e6]);
            scale = offset * 1e-9;
        }

        var levels = Enumerable.Range(0, unique).Select(_ => offset + (random.NextGaussian() * scale)).ToArray();

        var values = new double?[rows];
        var pairedY = new double?[rows];
        for (var row = 0; row < rows; row++)
        {
            var value = family switch
            {
                "FewUnique" => random.Pick(levels),
                "Bimodal" => offset + (scale * ((random.NextDouble() < 0.5 ? -5 : 5) + random.NextGaussian())),
                "Skewed" => offset + (scale * Math.Exp(1.5 * random.NextGaussian())),
                "Constant" => offset,
                _ => offset + (scale * random.NextGaussian())
            };

            if (tailRatio > 0 && random.NextDouble() < tailRatio)
            {
                value += (random.NextDouble() < 0.5 ? -1 : 1) * tailMagnitude * scale;
            }

            if (step > 0)
            {
                value = Math.Round(value / step) * step;
            }

            values[row] = random.NextDouble() < missing ? null : value;

            // A paired Y with a linear relation to X, its own noise and its own empty cells.
            var y = (2 * (value - offset)) + (scale * random.NextGaussian()) + 3;
            pairedY[row] = random.NextDouble() < missing / 2 ? null : (step > 0 ? Math.Round(y / step) * step : y);
        }

        string?[]? groups = null;
        if (groupCount > 0)
        {
            groups = new string?[rows];
            for (var row = 0; row < rows; row++)
            {
                // Zipf-like imbalance: group g gets weight 1 / (g + 1)^2.
                var index = zipf ? ZipfIndex(random, groupCount) : random.Next(groupCount);
                groups[row] = random.NextDouble() < missingGroups ? null : $"G{index}";
            }
        }

        var parameters = string.Create(
            CultureInfo.InvariantCulture,
            $"offset={offset:R} scale={scale:R} step={step:R} unique={unique} missing={missing:R} tail={tailRatio:R}x{tailMagnitude:R} groups={groupCount}{(zipf ? "(zipf)" : string.Empty)} missingGroups={missingGroups:R}");

        return new RobustnessCase
        {
            Name = $"generated-{caseNumber}",
            Family = family,
            Origin = string.Create(CultureInfo.InvariantCulture, $"generated seed={seed} case={caseNumber}"),
            Parameters = parameters,
            Values = values,
            Groups = groups,
            PairedY = pairedY
        };
    }

    private static int ZipfIndex(RobustnessRandom random, int count)
    {
        var total = 0d;
        for (var index = 0; index < count; index++)
        {
            total += 1d / ((index + 1) * (index + 1));
        }

        var target = random.NextDouble() * total;
        for (var index = 0; index < count; index++)
        {
            target -= 1d / ((index + 1) * (index + 1));
            if (target <= 0)
            {
                return index;
            }
        }

        return count - 1;
    }
}
