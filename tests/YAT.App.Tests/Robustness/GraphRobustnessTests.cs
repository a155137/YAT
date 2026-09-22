using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// The statistical graph robustness harness, normal suite: every named case of the corpus through every graph it applies
// to, a deterministic set of generated cases, a few exports, and a few cases through a real project end to end. It runs
// with every "dotnet test"; the large workload is in GraphRobustnessTortureTests, run with "--explicit only".
//
// A failure names the graph and the case (and how to rebuild it: "named", or the generator seed and case number), and
// small cases carry their values.

// L1 + L2: the named corpus, built, checked and drawn off screen.
public sealed class GraphRobustnessNamedTests
{
    public static TheoryData<string> UnivariateCases => [.. RobustnessCorpus.Univariate.Select(robustnessCase => robustnessCase.Name)];

    public static TheoryData<string> PairedCases => [.. RobustnessCorpus.Paired.Select(robustnessCase => robustnessCase.Name)];

    [Theory]
    [MemberData(nameof(UnivariateCases))]
    public void EveryUnivariateGraphHoldsItsInvariants(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);

        foreach (var graph in RobustnessGraphs.UnivariateGraphs)
        {
            GraphRobustnessInvariants.Exercise(robustnessCase, graph, GraphThemes.Light);
        }
    }

    [Theory]
    [MemberData(nameof(PairedCases))]
    public void TheScatterPlotHoldsItsInvariants(string name)
    {
        GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named(name), RobustnessGraph.Scatter, GraphThemes.Light);
    }

    // The Task #034.1 regression, named on its own so it can never be lost in the corpus: the reported column, the
    // four values once, and the mirror image all build a box whose whisker ends inside it.
    [Theory]
    [InlineData("issue-0341-repeated", true)]
    [InlineData("issue-0341-minimal", true)]
    [InlineData("issue-0341-mirrored", false)]
    public void TheTask0341DatasetsBuildABoxWithAWhiskerInsideIt(string name, bool upper)
    {
        var built = GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named(name), RobustnessGraph.BoxPlot, GraphThemes.Light);

        var box = Assert.Single(((BoxPlotRenderModel)built.Model!).Boxes);
        Assert.True(upper ? box.UpperWhisker < box.ThirdQuartile : box.LowerWhisker > box.FirstQuartile);
    }

    // A group whose rows all lack a value never reaches the graph data, so it is not drawn - and nothing else breaks.
    [Fact]
    public void AGroupWithoutValuesIsNotDrawnAndBreaksNothing()
    {
        var built = GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named("group-without-values"), RobustnessGraph.BoxPlot, GraphThemes.Light);

        Assert.Equal(["Reg1 / A", "Reg1 / B"], ((BoxPlotRenderModel)built.Model!).Categories);
    }
}

// L1: deterministic generated cases, biased towards semiconductor-like data.
public sealed class GraphRobustnessGeneratedTests
{
    public const int CaseCount = 200;
    public const int MaximumRows = 5000;

    public static TheoryData<int> Cases => [.. Enumerable.Range(0, CaseCount)];

    // Rebuild a failing case with RobustnessGenerator.Create(caseNumber, MaximumRows) - the seed is the harness's fixed
    // primary seed.
    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryApplicableGraphHoldsItsInvariants(int caseNumber)
    {
        var robustnessCase = RobustnessGenerator.Create(caseNumber, MaximumRows);

        foreach (var graph in RobustnessGraphs.For(robustnessCase))
        {
            GraphRobustnessInvariants.Exercise(robustnessCase, graph, renderTheme: null);
        }
    }

    // The generator is what makes a case number a replay: the same number is the same data, every time.
    [Fact]
    public void AGeneratedCaseIsTheSameEveryTimeItIsCreated()
    {
        foreach (var caseNumber in (int[])[0, 1, 17, 199])
        {
            var first = RobustnessGenerator.Create(caseNumber, MaximumRows);
            var second = RobustnessGenerator.Create(caseNumber, MaximumRows);

            Assert.Equal(first.Values, second.Values);
            Assert.Equal(first.PairedY, second.PairedY);
            Assert.Equal(first.Groups, second.Groups);
            Assert.Equal(first.Parameters, second.Parameters);
            Assert.All(first.Values, value => Assert.True(value is null || double.IsFinite(value.Value)));
        }
    }
}

// L2 in the other theme, and export, on a small representative subset: rendering is where the time goes.
public sealed class GraphRobustnessExportTests
{
    public static TheoryData<string, RobustnessGraph> DarkThemeCases => new()
    {
        { "issue-0341-repeated", RobustnessGraph.BoxPlot },
        { "extreme-positive-tail", RobustnessGraph.Histogram },
        { "tiny-cross-zero", RobustnessGraph.ProbabilityPlot },
        { "missing-group-labels", RobustnessGraph.EmpiricalCdf },
        { "paired-uneven-groups", RobustnessGraph.Scatter }
    };

    public static TheoryData<string, RobustnessGraph> PngCases => new()
    {
        { "issue-0341-repeated", RobustnessGraph.BoxPlot },
        { "issue-0341-mirrored", RobustnessGraph.BoxPlot },
        { "constant", RobustnessGraph.Histogram },
        { "tiny-range", RobustnessGraph.EmpiricalCdf },
        { "large-offset-tiny-variation", RobustnessGraph.ProbabilityPlot },
        { "uneven-groups", RobustnessGraph.BoxPlot },
        { "paired-tiny-range", RobustnessGraph.Scatter },
        { "n-1", RobustnessGraph.Histogram }
    };

    [Theory]
    [MemberData(nameof(DarkThemeCases))]
    public void TheGraphRendersInTheDarkTheme(string name, RobustnessGraph graph)
    {
        GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named(name), graph, GraphThemes.Dark, repeat: false);
    }

    [Theory]
    [MemberData(nameof(PngCases))]
    public void TheGraphExportsToPng(string name, RobustnessGraph graph)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var built = GraphRobustnessInvariants.Exercise(robustnessCase, graph, renderTheme: null, repeat: false);

        GraphRobustnessPipeline.AssertExports(robustnessCase.Describe(RobustnessGraphs.Name(graph)), built);
    }
}

// L3: a few cases written to a real project, read back by the graph data query, then built, checked, drawn and
// exported - the cases whose trouble would start in extraction (empty cells, empty groups, repeated values).
public sealed class GraphRobustnessPipelineTests
{
    public static TheoryData<string, RobustnessGraph> Cases => new()
    {
        { "missing-heavy", RobustnessGraph.Histogram },
        { "missing-heavy", RobustnessGraph.BoxPlot },
        { "group-without-values", RobustnessGraph.BoxPlot },
        { "group-without-values", RobustnessGraph.EmpiricalCdf },
        { "issue-0341-repeated", RobustnessGraph.BoxPlot },
        { "issue-0341-repeated", RobustnessGraph.ProbabilityPlot },
        { "numeric-groups", RobustnessGraph.BoxPlot },
        { "paired-missing", RobustnessGraph.Scatter },
        { "paired-uneven-groups", RobustnessGraph.Scatter }
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ACaseThroughARealProjectHoldsEveryInvariant(string name, RobustnessGraph graph)
    {
        var robustnessCase = RobustnessCorpus.Named(name);

        var built = await GraphRobustnessPipeline.RunAsync(robustnessCase, graph, [], exportPng: true);

        if (name == "group-without-values")
        {
            // Rows without a value are dropped at extraction, so the group they alone make up is not there at all.
            var variable = built.Data is MultiVariableGraphData multi ? multi.Variables[0] : (UnivariateGraphData)built.Data;
            Assert.DoesNotContain(
                GraphRobustnessInvariants.ExpectedSeries(variable.Group, variable.Values.Span),
                series => series.Label == "C");
        }
    }

    // Several variables of one worksheet in one box plot: each keeps its own rows, and a variable without any value
    // keeps its category and draws no box.
    [Fact]
    public async Task AMultiVariableGroupedBoxPlotThroughARealProjectHoldsEveryInvariant()
    {
        var robustnessCase = RobustnessCorpus.Named("missing-group-labels");
        double?[] empty = [.. Enumerable.Repeat<double?>(null, robustnessCase.RowCount)];
        double?[] partial = [.. Enumerable.Range(0, robustnessCase.RowCount).Select(row => row % 5 == 0 ? null : (double?)(row * 0.25))];

        var built = await GraphRobustnessPipeline.RunAsync(
            robustnessCase, RobustnessGraph.BoxPlot, [("Reg3", empty), ("Reg4", partial)], exportPng: true);

        var model = (BoxPlotRenderModel)built.Model!;
        Assert.Contains("Reg3", model.Categories);
        Assert.DoesNotContain(model.Boxes, box => box.Label == "Reg3");
        Assert.Equal(3, ((MultiVariableGraphData)built.Data).Variables.Count);
    }
}

// The end-to-end path shared by the normal and the torture suite.
internal static class GraphRobustnessPipeline
{
    public static async Task<BuiltGraph> RunAsync(
        RobustnessCase robustnessCase,
        RobustnessGraph graph,
        IReadOnlyList<(string Name, double?[] Values)> extraVariables,
        bool exportPng,
        GraphTheme? renderTheme = null)
    {
        var context = robustnessCase.Describe(RobustnessGraphs.Name(graph) + " end to end");

        GraphData data;
        try
        {
            data = await RobustnessGraphs.LoadThroughProjectAsync(graph, robustnessCase, extraVariables, TestContext.Current.CancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Assert.Fail($"{context}\n  reading the case back threw: {exception}");
            throw;
        }

        // What extraction keeps is fixed by the null rules: the rows with a value (with both values, for a scatter plot).
        switch (data)
        {
            case ScatterGraphData scatter:
                GraphRobustnessInvariants.That(scatter.Count == robustnessCase.PairCount, context,
                    $"extraction kept {scatter.Count} of {robustnessCase.PairCount} complete pairs");
                break;
            case MultiVariableGraphData multi:
                GraphRobustnessInvariants.That(multi.Variables[0].Count == robustnessCase.ObservationCount, context,
                    $"extraction kept {multi.Variables[0].Count} of {robustnessCase.ObservationCount} observations");
                for (var index = 0; index < extraVariables.Count; index++)
                {
                    var expected = extraVariables[index].Values.Count(value => value is not null);
                    GraphRobustnessInvariants.That(multi.Variables[index + 1].Count == expected, context,
                        $"extraction kept {multi.Variables[index + 1].Count} of {expected} observations of {extraVariables[index].Name}");
                }

                break;
            case UnivariateGraphData univariate:
                GraphRobustnessInvariants.That(univariate.Count == robustnessCase.ObservationCount, context,
                    $"extraction kept {univariate.Count} of {robustnessCase.ObservationCount} observations");
                break;
        }

        var built = GraphRobustnessInvariants.Exercise(context, graph, data, renderTheme ?? GraphThemes.Light);
        if (exportPng)
        {
            AssertExports(context, built);
        }

        return built;
    }

    // The graph exports through the shared snapshot path and the PNG decodes at the export size.
    public static void AssertExports(string context, BuiltGraph built)
    {
        if (built.Model is null)
        {
            return;
        }

        byte[] png;
        try
        {
            png = RobustnessGraphs.ExportPng(built, GraphThemes.Light);
        }
        catch (Exception exception)
        {
            Assert.Fail($"{context}\n  PNG export threw: {exception}");
            throw;
        }

        using var bitmap = SKBitmap.Decode(png);
        GraphRobustnessInvariants.That(bitmap is not null, context, "the exported PNG must decode");
        GraphRobustnessInvariants.That(
            bitmap!.Width == GraphExportService.ExportWidth && bitmap.Height == GraphExportService.ExportHeight,
            context,
            $"the exported PNG is {bitmap.Width}x{bitmap.Height}");
    }
}
