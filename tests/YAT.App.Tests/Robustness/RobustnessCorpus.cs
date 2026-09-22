namespace YAT.App.Tests.Robustness;

// The named robustness corpus: synthetic datasets of the shapes measured semiconductor data takes, and of the shapes
// that have broken graphs before. Every case is built from formulas or from the harness's own seeded generator, so it
// is the same on every run and on every machine. None of it is real data.
//
// Univariate cases feed the histogram, box plot, probability plot and empirical CDF; paired cases feed the scatter
// plot. A case is only given to the graphs it makes sense for.
internal static class RobustnessCorpus
{
    // The reported Task #034.1 column: four decimals repeated. R-7 puts Q3 between 0.133 and 0.157 while every 0.157 is
    // an outlier, so the upper whisker ends inside the box.
    private static readonly double[] Issue0341 = [0.132, 0.157, 0.122, 0.133];

    public static IReadOnlyList<RobustnessCase> Univariate { get; } = BuildUnivariate();

    public static IReadOnlyList<RobustnessCase> Paired { get; } = BuildPaired();

    public static RobustnessCase Named(string name) =>
        Univariate.Concat(Paired).SingleOrDefault(candidate => candidate.Name == name)
        ?? throw new ArgumentException($"There is no robustness case named '{name}'.", nameof(name));

    private static List<RobustnessCase> BuildUnivariate()
    {
        var random = new RobustnessRandom(RobustnessGenerator.PrimarySeed);

        return
        [
            Case("n-1", "Size", [42]),
            Case("n-2", "Size", [10, 20]),
            Case("constant", "Constant", Repeat(100, 50)),

            Case("issue-0341-repeated", "Issue0341", Cycle(Issue0341, 800)),
            Case("issue-0341-minimal", "Issue0341", [.. Issue0341]),
            Case("issue-0341-mirrored", "Issue0341", Cycle([.. Issue0341.Select(value => -value)], 800)),
            Case("issue-0341-grouped", "Issue0341", Cycle(Issue0341, 800), groups: Labels(800, row => row % 8 < 4 ? "SITE1" : "SITE2")),

            Case("few-unique-repeated", "FewUnique", Cycle([0.50, 0.52, 0.51], 900)),
            Case("quantized-readings", "Quantized", Rows(600, _ => Math.Round((1.2 + (0.002 * random.NextGaussian())) / 0.001) * 0.001)),
            Case("tiny-range", "TinyRange", Rows(100, row => row * 1e-9)),
            Case("large-offset-tiny-variation", "LargeOffset", Rows(500, _ => 15000 + (1e-6 * random.NextDouble()))),

            Case("extreme-positive-tail", "Tail", Rows(1000, row => row % 100 == 0 ? 1e6 + row : random.NextDouble())),
            Case("extreme-negative-tail", "Tail", Rows(1000, row => row % 100 == 0 ? -1e6 - row : -random.NextDouble())),
            Case("bimodal", "Bimodal", Rows(1000, row => (row % 2 == 0 ? 0 : 10) + random.NextGaussian())),
            Case("highly-skewed", "Skewed", Rows(1000, _ => Math.Exp(1.5 * random.NextGaussian()))),
            Case("tiny-cross-zero", "CrossZero", Rows(400, _ => Math.Round(5e-8 * random.NextGaussian() / 1e-8) * 1e-8)),

            // Nine rows in ten are empty.
            Case("missing-heavy", "Missing", RowsWithGaps(1000, row => row * 0.1, row => row % 10 == 0)),
            Case("uneven-groups", "Groups", Rows(1011, row => row), groups: Labels(1011, row => row == 0 ? "tiny" : row <= 10 ? "small" : "large")),
            Case("missing-group-labels", "Groups", Rows(600, row => row % 37), groups: Labels(600, row => row % 10 < 3 ? null : $"LOT{row % 3}")),
            Case(
                "group-without-values",
                "Groups",
                RowsWithGaps(300, row => row, row => row % 3 != 2),
                groups: Labels(300, row => row % 3 == 2 ? "C" : row % 3 == 0 ? "A" : "B")),
            Case("numeric-groups", "Groups", Rows(400, row => 5 + (row % 7)), groups: Labels(400, row => ((row % 4) + 1 + (row % 4 == 3 ? 0.5 : 0)).ToString(System.Globalization.CultureInfo.InvariantCulture)), numericGroups: true)
        ];
    }

    private static List<RobustnessCase> BuildPaired()
    {
        var random = new RobustnessRandom(RobustnessGenerator.PrimarySeed + 1);

        return
        [
            PairedCase("paired-n-1", "Size", [1], [2]),
            PairedCase("paired-n-2", "Size", [1, 2], [10, 20]),
            PairedCase("paired-constant", "Constant", Repeat(5, 40), Repeat(7, 40)),
            PairedCase("paired-constant-y", "Constant", Rows(40, row => row), Repeat(3.3, 40)),
            PairedCase("paired-quantized", "Quantized", Cycle(Issue0341, 400), Rows(400, row => Math.Round((row % 4) * 0.01 / 0.005) * 0.005)),
            PairedCase("paired-tiny-range", "TinyRange", Rows(100, row => row * 1e-9), Rows(100, row => row * 2e-9)),
            PairedCase("paired-large-offset", "LargeOffset", Rows(300, _ => 15000 + (1e-6 * random.NextDouble())), Rows(300, _ => 1e6 + random.NextDouble())),
            PairedCase("paired-extreme-tail", "Tail", Rows(500, row => row % 50 == 0 ? 1e6 : row), Rows(500, row => row % 70 == 0 ? -1e6 : row)),
            PairedCase("paired-missing", "Missing", RowsWithGaps(300, row => row, row => row % 3 != 0), RowsWithGaps(300, row => row * 2, row => row % 5 != 0)),
            PairedCase(
                "paired-uneven-groups",
                "Groups",
                Rows(1011, row => row),
                Rows(1011, row => row % 13),
                groups: Labels(1011, row => row == 0 ? "tiny" : row <= 10 ? "small" : row % 17 == 0 ? null : "large"))
        ];
    }

    private static RobustnessCase Case(string name, string family, double?[] values, string?[]? groups = null, bool numericGroups = false) => new()
    {
        Name = name,
        Family = family,
        Origin = "named",
        Values = values,
        Groups = groups,
        NumericGroups = numericGroups
    };

    private static RobustnessCase PairedCase(string name, string family, double?[] x, double?[] y, string?[]? groups = null) => new()
    {
        Name = name,
        Family = family,
        Origin = "named",
        Values = x,
        PairedY = y,
        Groups = groups
    };

    private static double?[] Repeat(double value, int count) => [.. Enumerable.Repeat<double?>(value, count)];

    private static double?[] Cycle(double[] pattern, int count) => [.. Enumerable.Range(0, count).Select(row => (double?)pattern[row % pattern.Length])];

    private static double?[] Rows(int count, Func<int, double> value) => [.. Enumerable.Range(0, count).Select(row => (double?)value(row))];

    private static double?[] RowsWithGaps(int count, Func<int, double> value, Func<int, bool> present) =>
        [.. Enumerable.Range(0, count).Select(row => present(row) ? value(row) : (double?)null)];

    private static string?[] Labels(int count, Func<int, string?> label) => [.. Enumerable.Range(0, count).Select(label)];
}
