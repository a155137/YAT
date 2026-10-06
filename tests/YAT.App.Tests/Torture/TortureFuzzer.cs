using System.Globalization;
using SkiaSharp;
using YAT.App.Tests.Robustness;
using YAT.Application.Graphs;

namespace YAT.App.Tests.Torture;

// Task #063, 3: the random graph fuzzer. A case is a worksheet and a request, both fully determined by (seed, case
// number) through the harness's own SplitMix64 (RobustnessRandom) - never System.Random, whose seeded sequence is not
// promised across .NET versions. A failure names the seed, the case number, the request and the dataset, and
// TortureFuzzer.Create(seed, caseNumber) rebuilds it exactly.
//
// The data is shaped like measured data rather than arbitrary doubles - offsets with small variation, quantized
// readings, few distinct values, tails, empty cells - and the request covers every graph type, 1 to 50 variables,
// groups, panels (sometimes more than nine, to be refused), the eight statistics in any subset, both layouts, a row
// filter, and the window sizes it is drawn at.
internal sealed record TortureFuzzCase(ulong Seed, int CaseNumber, TortureDataset Dataset, TortureRequest Request, IReadOnlyList<SKSize> Sizes)
{
    public string Describe() =>
        $"seed=0x{Seed:X} case={CaseNumber} (rebuild: TortureFuzzer.Create(0x{Seed:X}, {CaseNumber}))\n  request: {Request}\n  sizes: {string.Join(", ", Sizes.Select(size => $"{size.Width}x{size.Height}"))}\n  {Dataset.Describe()}";
}

internal static class TortureFuzzer
{
    public const ulong Seed = 0x0630_F022;

    private static readonly GraphType[] Types =
        [GraphType.Histogram, GraphType.BoxPlot, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf, GraphType.ScatterPlot];

    private static readonly double[] Offsets = [0, 1e-3, 1, 15, 1e3, 1.5e4, 1e6];
    private static readonly double[] RelativeSpreads = [0, 1e-6, 1e-3, 1e-2, 1e-1, 1];
    private static readonly double[] MissingRatios = [0, 0, 0, 0.1, 0.5, 0.9, 0.99];
    private static readonly int[] RowChoices = [1, 2, 3, 5, 10, 30, 100, 400];

    private static readonly SKSize[] SizeChoices =
        [new(120, 90), new(200, 150), new(320, 240), new(640, 480), new(1024, 768), new(1920, 1080), new(3840, 2160)];

    public static TortureFuzzCase Create(ulong seed, int caseNumber)
    {
        var random = new RobustnessRandom(RobustnessRandom.Mix(seed, caseNumber));
        var type = random.Pick(Types);
        var rows = random.Pick(RowChoices);

        // Mostly few variables, now and then many, and the most the setup takes.
        var variableCount = type == GraphType.ScatterPlot ? 2 : random.Next(10) switch
        {
            < 5 => 1,
            < 8 => 2 + random.Next(6),
            8 => 10 + random.Next(30),
            _ => 50
        };

        var columns = new List<TortureColumn>();
        var names = new List<string>();
        for (var index = 0; index < variableCount; index++)
        {
            var name = random.Next(20) == 0 ? $"V{index + 1} 晶圓 😀 {new string('x', random.Next(60))}" : $"V{index + 1}";
            names.Add(name);
            columns.Add(TortureDataset.Numeric(name, Values(random, rows)));
        }

        string? group = null;
        if (random.Next(2) == 0)
        {
            group = "Lot";
            var count = 1 + random.Next(12);
            var empty = random.Next(3) == 0 ? 0.2 : 0;
            columns.Add(TortureDataset.Text(group, Enumerable.Range(0, rows).Select(row =>
                random.NextDouble() < empty ? null : $"L{(row == 0 ? 0 : random.Next(count))}")));
        }

        string? panel = null;
        if (type != GraphType.BoxPlot && random.Next(3) == 0)
        {
            panel = "Site";
            var count = random.Next(10) == 0 ? 10 + random.Next(4) : 1 + random.Next(9);
            var numeric = random.Next(2) == 0;
            columns.Add(numeric
                ? TortureDataset.Numeric(panel, Enumerable.Range(0, rows).Select(_ => random.Next(10) == 0 ? null : (double?)(1 + random.Next(count))))
                : TortureDataset.Text(panel, Enumerable.Range(0, rows).Select(_ => random.Next(10) == 0 ? null : $"S{random.Next(count)}")));
        }

        (string, string[])? filter = null;
        if (group is not null && random.Next(4) == 0)
        {
            filter = (group, [.. Enumerable.Range(0, 1 + random.Next(3)).Select(_ => $"L{random.Next(6)}").Distinct()]);
        }

        var statistics = new TortureStatistics(
            random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0,
            random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0, random.Next(2) == 0);

        var request = type == GraphType.ScatterPlot
            ? new TortureRequest(type, [names[0]]) { Y = names[1] }
            : new TortureRequest(type, names)
            {
                Layout = variableCount > 1 && random.Next(3) == 0 ? GraphVariableLayout.Separate : GraphVariableLayout.Together
            };
        request = request with { Group = group, Panel = panel, Statistics = statistics, Filter = filter };

        SKSize[] sizes = [random.Pick(SizeChoices), random.Pick(SizeChoices)];
        var dataset = new TortureDataset(string.Create(CultureInfo.InvariantCulture, $"fuzz-{caseNumber}"), columns);
        return new TortureFuzzCase(seed, caseNumber, dataset, request, sizes);
    }

    // One measured-looking column: a family, an offset, a spread relative to it, and empty cells.
    private static double?[] Values(RobustnessRandom random, int rows)
    {
        var offset = random.Pick(Offsets) * (random.Next(4) == 0 ? -1 : 1);
        var spread = Math.Max(Math.Abs(offset), 1) * random.Pick(RelativeSpreads);
        var missing = random.Pick(MissingRatios);
        var family = random.Next(6);
        var resolution = spread > 0 ? spread / (1 + random.Next(50)) : 1;
        var unique = Enumerable.Range(0, 1 + random.Next(4)).Select(_ => offset + (spread * random.NextGaussian())).ToArray();

        var values = new double?[rows];
        for (var row = 0; row < rows; row++)
        {
            if (random.NextDouble() < missing)
            {
                continue;
            }

            var gaussian = random.NextGaussian();
            values[row] = family switch
            {
                0 => offset + (spread * gaussian),
                1 => Math.Round((offset + (spread * gaussian)) / resolution) * resolution,
                2 => unique[random.Next(unique.Length)],
                3 => random.Next(50) == 0 ? offset + (spread * 1000 * Math.Sign(gaussian)) : offset + (spread * gaussian),
                4 => offset + (spread * Math.Exp(1.5 * gaussian)),
                _ => offset
            };
        }

        // A column with no value at all is pasted as text and offered to no variable role (ColumnDataTypeDetector), which
        // would only cancel the case; columns of nothing are the poison corpus's, so a fuzzed column keeps one value.
        if (values.All(value => value is null))
        {
            values[random.Next(rows)] = offset;
        }

        return values;
    }

    // Runs one case; returns what it opened, for the sweep's summary.
    public static async Task<TortureOutcome?> RunAsync(ulong seed, int caseNumber)
    {
        var fuzz = Create(seed, caseNumber);
        using var session = await TortureSession.StartAsync(fuzz.Dataset);
        var outcome = await session.DrawAsync(fuzz.Request);
        if (outcome is not null)
        {
            foreach (var error in outcome.Errors)
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"REFUSED case {caseNumber}: {error}");
            }
        }

        // Every window is laid out and drawn; the first is exported (an export per window of fifty is time, not coverage).
        if (outcome is { Graphs.Count: > 1 })
        {
            TortureInvariants.Verify(fuzz.Describe(), outcome with { Graphs = outcome.Graphs.Take(1).ToList() }, fuzz.Sizes);
            TortureInvariants.Verify(fuzz.Describe(), outcome with { Graphs = outcome.Graphs.Skip(1).ToList() }, fuzz.Sizes, export: false);
        }
        else
        {
            TortureInvariants.Verify(fuzz.Describe(), outcome, fuzz.Sizes);
        }

        return outcome;
    }

    // The seed and the number of cases of an explicit sweep: YAT_TORTURE_SEED (decimal or 0x hex) and YAT_TORTURE_CASES
    // when set, so a sweep can explore another seed and replay exactly what it found.
    public static ulong SweepSeed() =>
        Environment.GetEnvironmentVariable("YAT_TORTURE_SEED") is { Length: > 0 } text
            ? text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : ulong.Parse(text, CultureInfo.InvariantCulture)
            : Seed;

    public static int SweepCases(int defaultCount) =>
        int.TryParse(Environment.GetEnvironmentVariable("YAT_TORTURE_CASES"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0
            ? count
            : defaultCount;

    // Where a sweep writes its summary (outside the repository), or nowhere.
    public static string? ReportPath() => Environment.GetEnvironmentVariable("YAT_TORTURE_REPORT") is { Length: > 0 } path ? path : null;
}

// The fuzzer's normal suite: 50 fixed cases with every "dotnet test".
public sealed class GraphFuzzerTests
{
    public static TheoryData<int> Cases => [.. Enumerable.Range(0, 50)];

    [Theory]
    [MemberData(nameof(Cases))]
    public Task AFuzzedGraphDrawsOrIsRefused(int caseNumber) => TortureFuzzer.RunAsync(TortureFuzzer.Seed, caseNumber);

    // The explicit sweep: 5,000 cases (YAT_TORTURE_CASES) of the seed (YAT_TORTURE_SEED), one after another. Every
    // failure is collected with its seed and case - the sweep does not stop at the first - and the summary of what was
    // drawn, refused and failed is written to YAT_TORTURE_REPORT.
    [Fact(Explicit = true)]
    public async Task TheFuzzerSweepFindsNothing()
    {
        var seed = TortureFuzzer.SweepSeed();
        var count = TortureFuzzer.SweepCases(5000);
        var failures = new List<string>();
        var windows = new Dictionary<GraphType, int>();
        var refusals = new Dictionary<string, int>();
        var notOffered = 0;
        var started = System.Diagnostics.Stopwatch.StartNew();
        for (var caseNumber = 0; caseNumber < count; caseNumber++)
        {
            var type = TortureFuzzer.Create(seed, caseNumber).Request.Type;
            try
            {
                var outcome = await TortureFuzzer.RunAsync(seed, caseNumber);
                if (outcome is null)
                {
                    notOffered++;
                    refusals[$"not offered: {TortureSession.LastNotOffered}"] = refusals.GetValueOrDefault($"not offered: {TortureSession.LastNotOffered}") + 1;
                    continue;
                }

                windows[type] = windows.GetValueOrDefault(type) + outcome.Graphs.Count;
                foreach (var error in outcome.Errors)
                {
                    var key = error.Split('\n')[0].Trim();
                    key = key.Length > 70 ? key[..70] : key;
                    refusals[key] = refusals.GetValueOrDefault(key) + 1;
                }
            }
            catch (Exception exception)
            {
                failures.Add($"FAILURE seed=0x{seed:X} case={caseNumber}: {exception.Message}");
            }
        }

        var report = new List<string>
        {
            $"fuzzer sweep seed=0x{seed:X} cases={count} elapsed={started.Elapsed.TotalSeconds:F1}s failures={failures.Count} not-offered={notOffered}",
            $"windows: {string.Join(", ", windows.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}"))}"
        };
        report.AddRange(refusals.OrderByDescending(pair => pair.Value).Select(pair => $"refused x{pair.Value}: {pair.Key}"));
        report.AddRange(failures);
        if (TortureFuzzer.ReportPath() is { } path)
        {
            await File.WriteAllLinesAsync(path, report, TestContext.Current.CancellationToken);
        }

        Assert.True(failures.Count == 0, string.Join("\n", report));
    }
}
