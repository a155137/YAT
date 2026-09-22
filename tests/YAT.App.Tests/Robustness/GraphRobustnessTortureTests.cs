using System.Diagnostics;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// The statistical graph robustness harness, torture suite: the large workload that is not worth paying on every build.
// These tests are explicit - a normal "dotnet test" skips them - and are run before a release or a graph milestone:
//
//     dotnet test YAT.slnx --explicit only
//
// Timings are written to the test output for information; nothing here passes or fails on time.
[Trait("Category", "Robustness")]
public sealed class GraphRobustnessTortureTests
{
    public const int SweepCaseCount = 5000;
    public const int SweepMaximumRows = 20_000;

    // The sweep uses case numbers of its own, so it does not repeat the normal suite's cases. Rebuild a failing case
    // with RobustnessGenerator.Create(caseNumber, SweepMaximumRows).
    public const int SweepFirstCase = 10_000;

    private static void Report(string message) => TestContext.Current.TestOutputHelper?.WriteLine(message);

    [Fact(Explicit = true)]
    public void AGeneratedSweepHoldsEveryInvariant()
    {
        var watch = Stopwatch.StartNew();
        var observations = 0L;

        for (var index = 0; index < SweepCaseCount; index++)
        {
            var robustnessCase = RobustnessGenerator.Create(SweepFirstCase + index, SweepMaximumRows);
            observations += robustnessCase.ObservationCount;

            // Every case is built and checked; one in ten is also built again, sampled and drawn. Every other case is
            // prepared with a generated specification (#036).
            var thorough = index % 10 == 0;
            var specification = index % 2 == 1 ? RobustnessGenerator.SpecificationFor(robustnessCase, SweepFirstCase + index) : null;
            foreach (var graph in RobustnessGraphs.For(robustnessCase))
            {
                GraphRobustnessInvariants.Exercise(robustnessCase, graph, thorough || specification is not null ? GraphThemes.Light : null, repeat: thorough, specification);
            }
        }

        watch.Stop();
        Report($"{SweepCaseCount:N0} generated cases, {observations:N0} observations, in {watch.ElapsedMilliseconds:N0} ms");
    }

    // More rows than one read window, so every graph type's data crosses a window boundary on its way out of storage.
    [Fact(Explicit = true)]
    public async Task DataAcrossReadWindowsHoldsEveryInvariantEndToEnd()
    {
        var rows = (GraphDataQueryService.ReadChunkRowCount * 2) + 123;
        var robustnessCase = LargeCase("chunk-boundary", rows, tailEvery: 97, missingEvery: 11);
        double?[] second = [.. Enumerable.Range(0, rows).Select(row => row % 7 == 0 ? null : (double?)(0.5 + ((row % 13) * 0.01)))];

        foreach (var graph in (RobustnessGraph[])[.. RobustnessGraphs.UnivariateGraphs, RobustnessGraph.Scatter])
        {
            var watch = Stopwatch.StartNew();
            await GraphRobustnessPipeline.RunAsync(
                robustnessCase, graph, graph == RobustnessGraph.BoxPlot ? [("Reg3", second)] : [], exportPng: true);
            Report($"{RobustnessGraphs.Name(graph)}: {rows:N0} rows end to end in {watch.ElapsedMilliseconds:N0} ms");
        }
    }

    // A million observations of every graph type through a real project. The box plot's tail is heavy enough that its
    // outliers exceed the display budget, so outlier sampling runs at full scale.
    [Fact(Explicit = true)]
    public async Task AMillionObservationsHoldEveryInvariantEndToEnd()
    {
        const int Rows = 1_000_000;
        var robustnessCase = LargeCase("million", Rows, tailEvery: 5, missingEvery: 50);

        foreach (var graph in (RobustnessGraph[])[.. RobustnessGraphs.UnivariateGraphs, RobustnessGraph.Scatter])
        {
            var watch = Stopwatch.StartNew();
            var built = await GraphRobustnessPipeline.RunAsync(robustnessCase, graph, [], exportPng: true);
            var detail = built.Model is BoxPlotRenderModel boxPlot
                ? $", outliers {boxPlot.OutlierCount:N0} (drawn {boxPlot.RenderedOutlierCount:N0})"
                : string.Empty;
            Report($"{RobustnessGraphs.Name(graph)}: {built.Data.Count:N0} observations end to end in {watch.ElapsedMilliseconds:N0} ms{detail}");
        }
    }

    // A long, grouped, paired case of quantized readings: a tail row every tailEvery rows and an empty cell every
    // missingEvery rows. Deterministic, from the harness's own generator.
    private static RobustnessCase LargeCase(string name, int rows, int tailEvery, int missingEvery)
    {
        var random = new RobustnessRandom(RobustnessGenerator.PrimarySeed + (ulong)rows);
        var values = new double?[rows];
        var paired = new double?[rows];
        var groups = new string?[rows];

        for (var row = 0; row < rows; row++)
        {
            var value = Math.Round((100 + (2 * random.NextGaussian())) / 0.01) * 0.01;
            if (row % tailEvery == 0)
            {
                value += 500 + (500 * random.NextDouble());
            }

            values[row] = row % missingEvery == 0 ? null : value;
            paired[row] = row % (missingEvery + 3) == 0 ? null : (0.5 * value) + random.NextGaussian();
            groups[row] = row % 41 == 0 ? null : $"SITE{row % 4}";
        }

        return new RobustnessCase
        {
            Name = name,
            Family = "Large",
            Origin = "named",
            Parameters = $"rows={rows} tailEvery={tailEvery} missingEvery={missingEvery}",
            Values = values,
            PairedY = paired,
            Groups = groups
        };
    }
}
