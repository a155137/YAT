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

// #037: the probability plot with its fitted line hidden, on the cases where a line is most likely to matter - ordinary
// and grouped data, one and two observations, constant decimals, skew, tiny ranges, large offsets - alone, with a
// specification, and without the statistics panel. Hiding the line changes the lines and the horizontal axis they
// reach into, and nothing else.
public sealed class GraphRobustnessFittedLineTests
{
    private static readonly ProbabilityPlotOptions Hidden = new(ShowFittedLine: false);

    public static TheoryData<string> Cases =>
    [
        "quantized-readings", "bimodal", "uneven-groups", "missing-group-labels", "n-1", "n-2",
        "constant-decimal-grouped", "highly-skewed", "tiny-range", "large-offset-tiny-variation"
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void HidingTheFittedLineChangesOnlyTheLinesAndTheirAxis(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);

        // Both built, checked, rebuilt, sampled and drawn by the harness; hidden lines hold the invariants of hidden lines.
        var shown = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.ProbabilityPlot, GraphThemes.Light);
        var hidden = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.ProbabilityPlot, GraphThemes.Dark, probabilityPlotOptions: Hidden);

        var context = robustnessCase.Describe(RobustnessGraphs.Name(RobustnessGraph.ProbabilityPlot));
        Assert.All(((ProbabilityPlotRenderModel)hidden.Model!).Series, series => Assert.Null(series.FittedLine));
        Assert.True(GraphRobustnessInvariants.WithoutFittedLines(shown) == GraphRobustnessInvariants.WithoutFittedLines(hidden),
            $"{context}\n  hiding the fitted line changed more than the lines:\n    shown:  {GraphRobustnessInvariants.WithoutFittedLines(shown)}\n    hidden: {GraphRobustnessInvariants.WithoutFittedLines(hidden)}");

        // The line only ever widens the horizontal axis; hiding it never needs more room.
        var shownRange = shown.Frame!.XAxis.Range;
        var hiddenRange = hidden.Frame!.XAxis.Range;
        Assert.True(hiddenRange.Minimum >= shownRange.Minimum && hiddenRange.Maximum <= shownRange.Maximum, context);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HiddenLinesKeepTheSpecificationTheirOwn(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var (_, specification) = RobustnessCorpus.Specifications(robustnessCase).Single(item => item.Name == "outside-both");

        var shown = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.ProbabilityPlot, null, repeat: false, specification);
        var hidden = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.ProbabilityPlot, GraphThemes.Light, repeat: true, specification, Hidden);

        // The specification's lines are the same; each frame's axis is its own builder's axis reached out to them.
        Assert.Equal(shown.Frame!.ReferenceLines, hidden.Frame!.ReferenceLines);
        Assert.Equal(GraphRobustnessInvariants.WithoutFittedLines(shown), GraphRobustnessInvariants.WithoutFittedLines(hidden));
        var builderRange = RobustnessGraphs.BuilderFrame(hidden.Model!).XAxis.Range;
        Assert.Equal(
            GraphAxisRanges.Including(builderRange, [.. hidden.Frame.ReferenceLines.Select(line => line.Value)]),
            hidden.Frame.XAxis.Range);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void HiddenLinesWithoutTheStatisticsPanelStillDraw(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var (_, specification) = RobustnessCorpus.Specifications(robustnessCase).Single(item => item.Name == "at-min-and-max");
        var built = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.ProbabilityPlot, null, repeat: false, probabilityPlotOptions: Hidden);

        // The presentation the application applies with statistics off and a specification on, to the same builder
        // frame: no panel, the specification's lines, and still no fitted line.
        var configuration = new GraphConfiguration(GraphType.ProbabilityPlot, Guid.Empty, [])
        {
            StatisticsOptions = new GraphStatisticsOptions(GraphStatisticsMode.Hide),
            Specification = specification,
            ProbabilityPlotOptions = Hidden
        };
        var frame = GraphPresentation.Apply(RobustnessGraphs.BuilderFrame(built.Model!), built.Data, configuration, TestContext.Current.CancellationToken);

        Assert.Null(frame.StatisticsPanel);
        Assert.Equal(GraphSpecificationLinesBuilder.Lines(specification), frame.ReferenceLines);
        Assert.All(((ProbabilityPlotRenderModel)built.Model!).Series, series => Assert.Null(series.FittedLine));
        RobustnessGraphs.Render(built with { Frame = frame }, GraphThemes.Light);
        RobustnessGraphs.Render(built with { Frame = frame }, GraphThemes.Dark);
    }

    // The fitted-line invariant is not vacuous: a line where none should be, or none where one should be, is caught.
    [Fact]
    public void AFittedLineThatDisagreesWithTheOptionIsCaught()
    {
        var robustnessCase = RobustnessCorpus.Named("bimodal");
        var shown = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.ProbabilityPlot, null, repeat: false);
        var hidden = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.ProbabilityPlot, null, repeat: false, probabilityPlotOptions: Hidden);

        // Each model checked as if it had been built with the other option.
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("lines shown but hidden", shown with { ProbabilityPlotOptions = Hidden }));
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("lines hidden but shown", hidden with { ProbabilityPlotOptions = ProbabilityPlotOptions.Default }));
    }
}

// #039: every named univariate case through the histogram with every named Y scale and binning, built, checked,
// rebuilt and drawn. The bins, heights and fixed grid hold their invariants; the statistics panel and the
// specification lines are exactly what they are with the default options.
public sealed class GraphRobustnessHistogramTests
{
    public static TheoryData<string> UnivariateCases => GraphRobustnessNamedTests.UnivariateCases;

    private static readonly HashSet<string> Drawn = ["percent", "count-200-density", "fixed-start-far", "fixed-maximum-on-edge"];

    [Theory]
    [MemberData(nameof(UnivariateCases))]
    public void EveryHistogramOptionHoldsItsInvariants(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var (_, specification) = RobustnessCorpus.Specifications(robustnessCase).FirstOrDefault(item => item.Name == "outside-both");
        var data = RobustnessGraphs.DataFor(RobustnessGraph.Histogram, robustnessCase);
        var context = robustnessCase.Describe(RobustnessGraphs.Name(RobustnessGraph.Histogram));
        var plain = GraphRobustnessInvariants.Exercise(context, RobustnessGraph.Histogram, data, null, repeat: false, specification);

        foreach (var (optionsName, options) in RobustnessCorpus.HistogramOptionsFor(robustnessCase))
        {
            Assert.True(HistogramOptionsRules.IsValid(options), $"{optionsName} is not valid.");
            var built = GraphRobustnessInvariants.Exercise(
                $"{context} [{optionsName}]",
                RobustnessGraph.Histogram,
                data,
                Drawn.Contains(optionsName) ? GraphThemes.Light : null,
                repeat: true,
                specification,
                histogramOptions: options);

            if (built.Model is null)
            {
                continue;
            }

            // Neither the scale nor the bins reach the statistics or the specification's lines.
            Assert.Equal(plain.Frame!.StatisticsPanel?.Rows, built.Frame!.StatisticsPanel?.Rows);
            Assert.Equal(plain.Frame.ReferenceLines, built.Frame.ReferenceLines);
        }
    }

    // The same width and start over two different datasets: one grid, wherever the two overlap, to the last bit.
    [Theory]
    [InlineData("bimodal", "highly-skewed", 0.37, 0.5)]
    [InlineData("quantized-readings", "large-offset-tiny-variation", 0.0017, 1.2)]
    [InlineData("uneven-groups", "missing-group-labels", 3.3, -7)]
    public void TwoDatasetsWithTheSameWidthAndStartShareOneGrid(string first, string second, double width, double start)
    {
        var options = new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: width, BinStart: start);
        double[] Edges(string name)
        {
            var robustnessCase = RobustnessCorpus.Named(name);
            var data = RobustnessGraphs.DataFor(RobustnessGraph.Histogram, robustnessCase);
            var values = ((UnivariateGraphData)data).Values.ToArray();
            if ((values.Max() - values.Min()) / width >= HistogramOptions.MaximumBinCount)
            {
                return [];
            }

            var model = (HistogramRenderModel)GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.Histogram, null, repeat: false, histogramOptions: options).Model!;
            return [model.Bins[0].LowerEdge, .. model.Bins.Select(bin => bin.UpperEdge)];
        }

        var a = Edges(first);
        var b = Edges(second);

        // Every edge either grid has is start + k x width for its own k: two grids agree on every k they share.
        foreach (var edge in a.Concat(b))
        {
            var k = Math.Round((edge - start) / width);
            Assert.Equal(start + (k * width), edge);
        }
    }

    // What the user is told instead of a histogram, through the harness's production build.
    [Theory]
    [InlineData("bimodal", 1e-6, 0, "The selected bin width requires more than 200 bins. Choose a larger bin width.")]
    [InlineData("extreme-positive-tail", 1, 0, "The selected bin width requires more than 200 bins. Choose a larger bin width.")]
    [InlineData("large-offset-tiny-variation", 1e-15, 0, "The selected bin width requires more than 200 bins. Choose a larger bin width.")]
    [InlineData("constant", 1e-15, 0, "The selected bin width is too small for this data range. Choose a larger bin width.")]
    [InlineData("n-1", 1e-20, 1e10, "The selected bin width is too small for this data range. Choose a larger bin width.")]
    public void AGridThatDoesNotSuitTheDataIsRefusedWithItsReason(string name, double width, double start, string message)
    {
        var data = RobustnessGraphs.DataFor(RobustnessGraph.Histogram, RobustnessCorpus.Named(name));
        var options = new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: width, BinStart: start);

        var failure = Assert.Throws<YAT.app.Graphs.GraphPreparationException>(
            () => RobustnessGraphs.Build(RobustnessGraph.Histogram, data, cancellationToken: TestContext.Current.CancellationToken, histogramOptions: options));

        Assert.Equal(message, failure.Message);
    }

    // The histogram invariants are not vacuous: heights, counts and a fixed grid that disagree with their options are
    // caught.
    [Fact]
    public void AHistogramThatDisagreesWithItsOptionsIsCaught()
    {
        var robustnessCase = RobustnessCorpus.Named("uneven-groups");
        var percent = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.Histogram, null, repeat: false,
            histogramOptions: new HistogramOptions(HistogramYScale.Percent));
        var fixedGrid = GraphRobustnessInvariants.Exercise(robustnessCase, RobustnessGraph.Histogram, null, repeat: false,
            histogramOptions: new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: 50, BinStart: 3));

        // A percent histogram checked as density, as frequency, and as a fixed grid of another width.
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("percent as density", percent with { HistogramOptions = new HistogramOptions(HistogramYScale.Density) }));
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("percent as frequency", percent with { HistogramOptions = HistogramOptions.Default }));
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("grid of another width", fixedGrid with
        {
            HistogramOptions = new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart, BinWidth: 51, BinStart: 3)
        }));

        // Counts moved between bins no longer match an independent recount.
        var model = (HistogramRenderModel)percent.Model!;
        var series = model.Series[0];
        var moved = series.Counts.ToArray();
        var from = Array.FindIndex(moved, count => count > 0);
        var to = from == 0 ? 1 : 0;
        moved[from]--;
        moved[to]++;
        var tampered = new HistogramRenderModel(
            model.Frame, model.Bins,
            [new HistogramSeriesRenderModel(series.Label, series.SeriesIndex, moved, [.. moved.Select(count => 100d * count / series.ObservationCount)]), .. model.Series.Skip(1)],
            model.SourceObservationCount, model.YScale);
        Assert.ThrowsAny<Exception>(() => GraphRobustnessInvariants.Verify("moved counts", percent with { Model = tampered, Plot = new HistogramRenderer(tampered) }));
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

        // One case in ten draws its probability plot without fitted lines (#037); the explicit sweep, one in three.
        var probabilityPlotOptions = caseNumber % 10 == 5 ? new ProbabilityPlotOptions(ShowFittedLine: false) : null;

        // And one in ten draws its histogram with generated options (#039); the explicit sweep, one in four.
        var histogramOptions = caseNumber % 10 == 7 ? RobustnessGenerator.HistogramOptionsFor(robustnessCase, caseNumber) : null;

        foreach (var graph in RobustnessGraphs.For(robustnessCase))
        {
            GraphRobustnessInvariants.Exercise(
                robustnessCase, graph, renderTheme: null, specification: specification, probabilityPlotOptions: probabilityPlotOptions, histogramOptions: histogramOptions);
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

        // From Task #041 the distribution graphs read their variables as the box plot does, one slice each; a graph of
        // one variable is drawn from its slice, as the graph preparation draws it.
        if (graph != RobustnessGraph.BoxPlot && data is MultiVariableGraphData { Variables.Count: 1 } single)
        {
            data = single.Variables[0];
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
