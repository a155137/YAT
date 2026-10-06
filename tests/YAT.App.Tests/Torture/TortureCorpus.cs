using YAT.App.Tests.Robustness;

namespace YAT.App.Tests.Torture;

// Task #063: the numerical poison corpus - worksheet columns of the values that break numerical code: nothing at all,
// almost nothing, the largest and smallest doubles, spans that overflow, variation below a value's own precision, one
// absurd outlier - beside grouping and panel columns with a group or panel of one row, and names no one expects: very
// long, CJK, emoji, punctuation. Every column is built from formulas or the harness's seeded generator (SplitMix64,
// RobustnessRandom), so the corpus is the same on every run and every machine.
internal static class TortureCorpus
{
    public const ulong Seed = 0x063_0001;

    public const int Rows = 200;

    public const string Reference = "Normal";
    public const string Group = "Lot";
    public const string Panel = "Site";
    public const string LongGroup = "Lot ★ 晶圓 😀";

    // A long name with CJK, emoji and punctuation, as an engineer's header row can carry.
    public static readonly string LongName = "Vth 閾值電壓 😀 (mV) / site#1 <x>&\"q\" " + new string('W', 180) + " 終";

    public const string SpecialName = "Reg #1 / μA (%) \"q\" <x>&'";

    // The numeric poison columns, by name: (name, values, note).
    public static IReadOnlyList<(string Name, double?[] Values)> Numeric { get; } = BuildNumeric();

    public static IReadOnlyList<string> NumericNames => [.. Numeric.Select(column => column.Name)];

    public static TortureDataset Dataset { get; } = Build();

    private static List<(string, double?[])> BuildNumeric()
    {
        var random = new RobustnessRandom(Seed);
        double?[] Rows(Func<int, double?> value) => [.. Enumerable.Range(0, TortureCorpus.Rows).Select(value)];

        return
        [
            (Reference, Rows(_ => 10 + random.NextGaussian())),
            ("AllMissing", Rows(_ => null)),
            ("Missing99", Rows(row => row is 17 or 150 ? 5 + row : null)),
            ("Missing99One", Rows(row => row == 99 ? 3.25 : null)),
            ("MaxPositive", Rows(row => double.MaxValue * (1 - (row % 5 * 0.1)))),
            ("MaxBothSigns", Rows(row => row % 2 == 0 ? double.MaxValue : -double.MaxValue)),
            ("MaxSpread", Rows(row => (row % 3 - 1) * double.MaxValue * 0.9)),
            ("Subnormal", Rows(row => double.Epsilon * (1 + (row % 7)))),
            ("SubnormalCrossZero", Rows(row => double.Epsilon * ((row % 9) - 4))),
            ("Huge1e300", Rows(_ => 1e300 * (1 + (0.01 * random.NextGaussian())))),
            ("Tiny1e-300", Rows(_ => 1e-300 * random.NextGaussian())),
            ("Outlier1e300", Rows(row => row == 123 ? 1e300 : 10 + random.NextGaussian())),
            ("OutlierNeg1e300", Rows(row => row == 7 ? -1e300 : random.NextGaussian())),
            ("TinyVariance", Rows(row => 1e6 + (1e-10 * (row % 4)))),
            ("BelowPrecision", Rows(row => 1.0 + (double.Epsilon * (row % 3)))),
            ("NearUlp", Rows(row => row % 2 == 0 ? 1.0 : Math.BitIncrement(1.0))),
            ("Constant", Rows(_ => 7)),
            ("ConstantHuge", Rows(_ => double.MaxValue)),
            ("ConstantNegZero", Rows(row => row % 2 == 0 ? 0.0 : -0.0)),
            (LongName, Rows(_ => 1 + random.NextGaussian())),
            (SpecialName, Rows(_ => -3 + random.NextGaussian()))
        ];
    }

    private static TortureDataset Build()
    {
        // Three lots, one of which has a single row; three sites, one with a single row; empty labels in both.
        string? Lot(int row) => row == 42 ? "Lonely" : row % 11 == 0 ? null : row % 2 == 0 ? "A" : "B";
        string? Site(int row) => row == 7 ? "SoloPanel" : row % 13 == 0 ? null : $"S{row % 3}";
        string? Long(int row) => row == 3 ? new string('長', 120) : row % 2 == 0 ? "😀 émoji 晶圓" : "lot <&> \"q\"";

        return new TortureDataset(
            "poison",
            [
                .. Numeric.Select(column => TortureDataset.Numeric(column.Name, column.Values)),
                TortureDataset.Text(Group, Enumerable.Range(0, Rows).Select(Lot)),
                TortureDataset.Text(Panel, Enumerable.Range(0, Rows).Select(Site)),
                TortureDataset.Text(LongGroup, Enumerable.Range(0, Rows).Select(Long))
            ]);
    }

    // Tiny worksheets: a header row with one, two or three rows under it.
    public static TortureDataset Small(int rows) =>
        new(
            $"rows-{rows}",
            [
                TortureDataset.Numeric(Reference, Enumerable.Range(0, rows).Select(row => (double?)(1.5 + row))),
                TortureDataset.Numeric("Y", Enumerable.Range(0, rows).Select(row => (double?)(10 - row))),
                TortureDataset.Text(Group, Enumerable.Range(0, rows).Select(row => row % 2 == 0 ? "A" : "B")),
                TortureDataset.Text(Panel, Enumerable.Range(0, rows).Select(row => $"S{row}"))
            ]);
}
