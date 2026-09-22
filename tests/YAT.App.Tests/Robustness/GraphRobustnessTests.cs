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

    // Task #035: the statistics panel of an exactly constant decimal is exactly 0 wide where a group has two or more
    // observations, and has no standard deviation for the group of one.
    [Theory]
    [InlineData(RobustnessGraph.Histogram)]
    [InlineData(RobustnessGraph.ProbabilityPlot)]
    [InlineData(RobustnessGraph.EmpiricalCdf)]
    public void AConstantDecimalHasAStatisticsPanelOfExactlyNoSpread(RobustnessGraph graph)
    {
        var built = GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named("constant-decimal-grouped"), graph, GraphThemes.Light);

        var panel = built.Frame!.StatisticsPanel!;
        Assert.Equal(["A", "B", "single"], panel.Rows.Select(row => row.Label));
        Assert.Equal([0d, 0d, null], panel.Rows.Select(row => row.StandardDeviation));
    }

    // The panel invariants are not vacuous: a panel that disagrees with its graph is caught.
    [Fact]
    public void AStatisticsPanelThatDisagreesWithItsGraphIsCaught()
    {
        var built = GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named("uneven-groups"), RobustnessGraph.Histogram, null);
        var panel = built.Frame!.StatisticsPanel!;
        var first = panel.Rows[0];

        GraphStatisticsPanel Tampered(GraphStatisticsRow row) =>
            new(panel.Title, panel.GroupHeader, [row, .. panel.Rows.Skip(1)]);

        GraphStatisticsRow Row(string label, int count, double mean, double? spread) =>
            new(label, first.SeriesIndex, count, mean, spread, count.ToString(System.Globalization.CultureInfo.InvariantCulture), first.MeanText, first.StandardDeviationText);

        GraphStatisticsPanel?[] wrong =
        [
            null,
            Tampered(Row("renamed", first.Count, first.Mean, first.StandardDeviation)),
            Tampered(Row(first.Label, first.Count + 1, first.Mean, first.StandardDeviation)),
            Tampered(Row(first.Label, first.Count, first.Mean + 1e6, first.StandardDeviation)),
            Tampered(Row(first.Label, first.Count, first.Mean, 1)),
            new GraphStatisticsPanel(panel.Title, panel.GroupHeader, [.. panel.Rows.Reverse()])
        ];

        foreach (var candidate in wrong)
        {
            var tampered = built with { Frame = built.Frame.WithStatisticsPanel(candidate) };
            Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("tampered", tampered));
        }

        var scatter = GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named("paired-n-2"), RobustnessGraph.Scatter, null);
        var withPanel = scatter with { Frame = scatter.Frame!.WithStatisticsPanel(panel) };
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("scatter with a panel", withPanel));
    }

    // A group whose rows all lack a value never reaches the graph data, so it is not drawn - and nothing else breaks.
    [Fact]
    public void AGroupWithoutValuesIsNotDrawnAndBreaksNothing()
    {
        var built = GraphRobustnessInvariants.Exercise(RobustnessCorpus.Named("group-without-values"), RobustnessGraph.BoxPlot, GraphThemes.Light);

        Assert.Equal(["Reg1 / A", "Reg1 / B"], ((BoxPlotRenderModel)built.Model!).Categories);
    }
}

// #036: every named univariate case with every named specification, through every univariate graph and the one
// presentation pipeline the application uses. The specification lines hold their invariants, and the plot - bins,
// points, fitted lines, statistics, panel - is exactly what it is without a specification.
public sealed class GraphRobustnessSpecificationTests
{
    public static TheoryData<string> UnivariateCases => GraphRobustnessNamedTests.UnivariateCases;

    // Drawing is where the time goes, so only a few specifications per case are drawn, and only a few are also
    // rebuilt and sampled; every one of them is built and checked.
    private static readonly HashSet<string> Drawn = ["at-min-and-max", "outside-both", "close-together", "unreachable"];
    private static readonly HashSet<string> Repeated = ["outside-both", "around-zero"];

    [Theory]
    [MemberData(nameof(UnivariateCases))]
    public void EverySpecificationHoldsItsInvariantsAndLeavesThePlotAlone(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var specifications = RobustnessCorpus.Specifications(robustnessCase);

        foreach (var graph in RobustnessGraphs.UnivariateGraphs)
        {
            var data = RobustnessGraphs.DataFor(graph, robustnessCase);
            var context = robustnessCase.Describe(RobustnessGraphs.Name(graph));
            var plain = GraphRobustnessInvariants.BuildOrFail(context, graph, data);
            var plot = GraphRobustnessInvariants.PlotFingerprint(plain);

            foreach (var (specificationName, specification) in specifications)
            {
                Assert.True(YAT.Application.Specifications.SpecificationRules.IsValid(specification), $"{specificationName} is not a valid specification.");

                var built = GraphRobustnessInvariants.Exercise(
                    $"{context} [{specificationName}]",
                    graph,
                    data,
                    Drawn.Contains(specificationName) ? GraphThemes.Light : null,
                    repeat: Repeated.Contains(specificationName),
                    specification: specification);

                var actual = GraphRobustnessInvariants.PlotFingerprint(built);
                Assert.True(actual == plot,
                    $"{context} [{specificationName}]\n  the specification changed the plot:\n    without: {plot}\n    with:    {actual}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(GraphRobustnessNamedTests.PairedCases), MemberType = typeof(GraphRobustnessNamedTests))]
    public void AScatterPlotIgnoresEverySpecification(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        foreach (var (_, specification) in RobustnessCorpus.Specifications(robustnessCase))
        {
            var built = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.Scatter, null, repeat: false, specification);
            Assert.True(built.Model is null || built.Frame!.ReferenceLines.Count == 0);
        }
    }

    // Named on their own: a constant column and a column of tiny values, with limits exactly on the data.
    [Theory]
    [InlineData("constant", RobustnessGraph.Histogram)]
    [InlineData("tiny-range", RobustnessGraph.ProbabilityPlot)]
    [InlineData("constant-decimal-grouped", RobustnessGraph.EmpiricalCdf)]
    public void LimitsExactlyOnTheDataSitInsideTheAxis(string name, RobustnessGraph graph)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var (_, specification) = RobustnessCorpus.Specifications(robustnessCase).Single(item => item.Name == "at-min-and-max");

        var built = GraphRobustnessInvariants.Exercise(robustnessCase, graph, GraphThemes.Dark, repeat: true, specification);

        var range = built.Frame!.XAxis.Range;
        Assert.All(built.Frame.ReferenceLines, line => Assert.True(line.Value > range.Minimum && line.Value < range.Maximum));
    }

    // The specification invariants are not vacuous: a frame whose lines or axis disagree with its specification is caught.
    [Fact]
    public void AFrameThatDisagreesWithItsSpecificationIsCaught()
    {
        var robustnessCase = RobustnessCorpus.Named("uneven-groups");
        var specification = new YAT.Application.Specifications.Specification(-100, 500, 2000);
        var built = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.Histogram, null, repeat: false, specification);
        var frame = built.Frame!;
        var builder = RobustnessGraphs.BuilderFrame(built.Model!);
        var lines = frame.ReferenceLines;

        GraphRenderModel[] wrong =
        [
            frame.WithReferenceLines([]),
            frame.WithReferenceLines([.. lines.Take(2)]),
            frame.WithReferenceLines([.. lines.Reverse()]),
            frame.WithReferenceLines([new GraphReferenceLine(GraphReferenceAxis.X, -100, "LSL -99", GraphReferenceLineKind.SpecificationLimit), .. lines.Skip(1)]),
            frame.WithReferenceLines([new GraphReferenceLine(GraphReferenceAxis.Y, -100, "LSL -100", GraphReferenceLineKind.SpecificationLimit), .. lines.Skip(1)]),
            frame.WithXAxis(builder.XAxis),
            frame.WithXAxis(new GraphAxisModel(frame.XAxis.Range, frame.XAxis.Ticks, "renamed"))
        ];

        foreach (var candidate in wrong)
        {
            Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("tampered", built with { Frame = candidate }));
        }

        // Lines on a graph that does not draw a specification are caught too.
        var boxPlot = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.BoxPlot, null, repeat: false, specification);
        Assert.Empty(boxPlot.Frame!.ReferenceLines);
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("box plot with lines", boxPlot with { Frame = boxPlot.Frame.WithReferenceLines(lines) }));
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

        // One case in ten also carries a generated specification (#036); the explicit sweep gives one to half of its cases.
        var specification = caseNumber % 10 == 0 ? RobustnessGenerator.SpecificationFor(robustnessCase, caseNumber) : null;

        foreach (var graph in RobustnessGraphs.For(robustnessCase))
        {
            GraphRobustnessInvariants.Exercise(robustnessCase, graph, renderTheme: null, specification: specification);
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
