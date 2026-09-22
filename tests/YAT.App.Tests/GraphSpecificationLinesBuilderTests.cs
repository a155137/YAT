using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// A specification put on a graph's frame: one line per value, labelled, and an X axis that shows them all - without
// touching the plot, its statistics or its sampling. Also the one presentation pipeline every graph goes through.
public class GraphSpecificationLinesBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly GraphTypeDefinition HistogramDefinition = GraphTypeDefinitions.For(GraphType.Histogram);

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(GraphType graphType, double[] values, string?[]? groups = null) =>
        new(graphType, Guid.NewGuid(), Column("Reg1"), values,
            groups is null ? null : new StringGroupData(Column("Lot", WorksheetDataType.String), groups));

    private static GraphRenderModel Frame(double minimum = 10, double maximum = 20)
    {
        var range = new GraphAxisRange(minimum, maximum);
        return new GraphRenderModel(
            "Histogram of Reg1",
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "Reg1"),
            new GraphAxisModel(new GraphAxisRange(0, 50), GraphAxisTicks.Nice(new GraphAxisRange(0, 50)), "Frequency"),
            new GraphLegendModel([new GraphLegendEntry("A", 0)], "Lot"));
    }

    private static GraphConfiguration Configuration(GraphType graphType, Specification specification, bool showStatistics = true) =>
        new(graphType, Guid.NewGuid(), [])
        {
            PresentationOptions = new GraphPresentationOptions(showStatistics),
            Specification = specification
        };

    // ---- No-op cases ----

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void AGraphWithoutTheCapabilityKeepsItsFrame(GraphType graphType)
    {
        var frame = Frame();

        Assert.Same(frame, GraphSpecificationLinesBuilder.Attach(frame, GraphTypeDefinitions.For(graphType), new Specification(0, 15, 30)));
    }

    [Fact]
    public void AnEmptySpecificationKeepsTheFrame()
    {
        var frame = Frame();

        Assert.Same(frame, GraphSpecificationLinesBuilder.Attach(frame, HistogramDefinition, Specification.None));
        Assert.Same(frame, GraphSpecificationLinesBuilder.Attach(frame, HistogramDefinition, new Specification()));
    }

    // ---- Lines and labels ----

    [Fact]
    public void AllThreeValuesBecomeLabelledLinesInSpecificationOrder()
    {
        var framed = GraphSpecificationLinesBuilder.Attach(Frame(), HistogramDefinition, new Specification(14.5, 15, 15.5));

        Assert.Equal(
            [
                new GraphReferenceLine(GraphReferenceAxis.X, 14.5, "LSL 14.5", GraphReferenceLineKind.SpecificationLimit),
                new GraphReferenceLine(GraphReferenceAxis.X, 15, "Target 15", GraphReferenceLineKind.Target),
                new GraphReferenceLine(GraphReferenceAxis.X, 15.5, "USL 15.5", GraphReferenceLineKind.SpecificationLimit)
            ],
            framed.ReferenceLines);
    }

    [Theory]
    [InlineData(14.5, null, null, "LSL 14.5")]
    [InlineData(null, 15.0, null, "Target 15")]
    [InlineData(null, null, 15.5, "USL 15.5")]
    public void OneValueBecomesOneLine(double? lower, double? target, double? upper, string label)
    {
        var line = Assert.Single(GraphSpecificationLinesBuilder.Attach(Frame(), HistogramDefinition, new Specification(lower, target, upper)).ReferenceLines);

        Assert.Equal(label, line.Label);
        Assert.Equal(GraphReferenceAxis.X, line.Axis);
    }

    [Theory]
    [InlineData(0.1 + 0.2, "LSL 0.3")]
    [InlineData(1.23456789012, "LSL 1.2345679")]
    [InlineData(-0.0000157, "LSL -1.57E-05")]
    [InlineData(123456789.0, "LSL 1.2345679E+08")]
    [InlineData(0.0, "LSL 0")]
    public void LabelsUseTheAnalysisNumberFormat(double value, string label) =>
        Assert.Equal(label, GraphSpecificationLinesBuilder.Label(GraphSpecificationLinesBuilder.LowerLimitLabel, value));

    [Fact]
    public void AValueThatIsNotFiniteGetsNoLine()
    {
        // Validation keeps these out of a configuration; the attacher still never builds a line it cannot place.
        var framed = GraphSpecificationLinesBuilder.Attach(Frame(), HistogramDefinition, new Specification(double.NaN, 15, double.PositiveInfinity));

        Assert.Equal(15, Assert.Single(framed.ReferenceLines).Value);
    }

    [Fact]
    public void AttachingAgainReplacesTheLinesInsteadOfAddingMore()
    {
        var specification = new Specification(14.5, 15, 15.5);
        var once = GraphSpecificationLinesBuilder.Attach(Frame(), HistogramDefinition, specification);
        var twice = GraphSpecificationLinesBuilder.Attach(once, HistogramDefinition, specification);

        Assert.Equal(once.ReferenceLines, twice.ReferenceLines);
        Assert.Equal(once.XAxis.Range, twice.XAxis.Range);
    }

    [Fact]
    public void AGroupedFrameStillGetsOneLinePerValue()
    {
        var data = Data(GraphType.Histogram, [1, 2, 3, 4, 5, 6], ["A", "B", "C", "A", "B", "C"]);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;

        var framed = GraphSpecificationLinesBuilder.Attach(model.Frame, HistogramDefinition, new Specification(2, 3, 4));

        Assert.Equal(3, framed.ReferenceLines.Count);
        Assert.Equal(3, framed.Legend!.Entries.Count);
    }

    // ---- The displayed X axis ----

    [Fact]
    public void ValuesInsideTheAxisLeaveItAlone()
    {
        var frame = Frame();
        var framed = GraphSpecificationLinesBuilder.Attach(frame, HistogramDefinition, new Specification(12, 15, 18));

        Assert.Same(frame.XAxis, framed.XAxis);
        Assert.Same(frame.YAxis, framed.YAxis);
    }

    [Fact]
    public void ValuesOutsideTheAxisWidenItAndItsTicksWhileKeepingItsTitle()
    {
        var frame = Frame();
        var framed = GraphSpecificationLinesBuilder.Attach(frame, HistogramDefinition, new Specification(5, null, 30));

        Assert.Equal(GraphAxisRanges.Including(frame.XAxis.Range, [5, 30]), framed.XAxis.Range);
        Assert.True(framed.XAxis.Range.Minimum < 5 && framed.XAxis.Range.Maximum > 30);
        Assert.Equal(GraphAxisTicks.Nice(framed.XAxis.Range), framed.XAxis.Ticks);
        Assert.All(framed.XAxis.Ticks, tick => Assert.InRange(tick.Value, framed.XAxis.Range.Minimum, framed.XAxis.Range.Maximum));
        Assert.Equal("Reg1", framed.XAxis.Title);
    }

    [Fact]
    public void ATargetOutsideTheDataWidensTheAxisLikeALimit()
    {
        var framed = GraphSpecificationLinesBuilder.Attach(Frame(), HistogramDefinition, new Specification(null, 25, null));

        Assert.True(framed.XAxis.Range.Maximum > 25);
        Assert.Equal(10, framed.XAxis.Range.Minimum);
    }

    [Fact]
    public void AValueOnTheAxisEdgeMovesTheEdgeOffTheLine()
    {
        var framed = GraphSpecificationLinesBuilder.Attach(Frame(), HistogramDefinition, new Specification(10, null, 20));

        Assert.True(framed.XAxis.Range.Minimum < 10);
        Assert.True(framed.XAxis.Range.Maximum > 20);
    }

    [Fact]
    public void AnUnreachableValueKeepsTheAxisAndStillCarriesItsLine()
    {
        var frame = Frame();
        var framed = GraphSpecificationLinesBuilder.Attach(frame, HistogramDefinition, new Specification(null, null, double.MaxValue));

        Assert.Same(frame.XAxis, framed.XAxis);
        Assert.True(double.IsFinite(framed.XAxis.Range.Minimum) && double.IsFinite(framed.XAxis.Range.Maximum));
        Assert.Single(framed.ReferenceLines);
    }

    [Fact]
    public void EverythingButTheXAxisAndTheLinesIsKept()
    {
        var frame = Frame().WithStatisticsPanel(GraphStatisticsPanelBuilder.Build(Data(GraphType.Histogram, [1, 2, 3]), Token));
        var framed = GraphSpecificationLinesBuilder.Attach(frame, HistogramDefinition, new Specification(0, null, 100));

        Assert.Equal(frame.Title, framed.Title);
        Assert.Same(frame.YAxis, framed.YAxis);
        Assert.Same(frame.Legend, framed.Legend);
        Assert.Same(frame.StatisticsPanel, framed.StatisticsPanel);
    }

    // ---- The presentation pipeline ----

    [Fact]
    public void ApplyAddsThePanelAndThenTheLines()
    {
        var data = Data(GraphType.Histogram, [11, 12, 13, 19]);
        var frame = Frame();

        var applied = GraphPresentation.Apply(frame, data, Configuration(GraphType.Histogram, new Specification(5, 15, 25)), Token);

        var expected = GraphSpecificationLinesBuilder.Attach(
            GraphStatisticsPanelBuilder.Attach(frame, data, HistogramDefinition, GraphPresentationOptions.Default, Token),
            HistogramDefinition,
            new Specification(5, 15, 25));
        Assert.Equal(expected.StatisticsPanel!.Rows, applied.StatisticsPanel!.Rows);
        Assert.Equal(expected.ReferenceLines, applied.ReferenceLines);
        Assert.Equal(expected.XAxis.Range, applied.XAxis.Range);
        Assert.Equal(expected.XAxis.Ticks, applied.XAxis.Ticks);
        Assert.Equal(expected.XAxis.Title, applied.XAxis.Title);
    }

    [Fact]
    public void ApplyWithoutPanelOrSpecificationKeepsTheFrame()
    {
        var frame = Frame();

        Assert.Same(frame, GraphPresentation.Apply(
            frame, Data(GraphType.Histogram, [11, 12]), Configuration(GraphType.Histogram, Specification.None, showStatistics: false), Token));
    }

    [Fact]
    public void StatisticsOffAndASpecificationOnGivesLinesWithoutAPanel()
    {
        var applied = GraphPresentation.Apply(
            Frame(), Data(GraphType.Histogram, [11, 12]), Configuration(GraphType.Histogram, new Specification(12, null, 18), showStatistics: false), Token);

        Assert.Null(applied.StatisticsPanel);
        Assert.Equal(2, applied.ReferenceLines.Count);
    }

    [Fact]
    public void ApplyingTheSameConfigurationTwiceChangesNothing()
    {
        var data = Data(GraphType.Histogram, [11, 12, 13, 19]);
        var configuration = Configuration(GraphType.Histogram, new Specification(10, 15, 40));

        var once = GraphPresentation.Apply(Frame(), data, configuration, Token);
        var twice = GraphPresentation.Apply(once, data, configuration, Token);

        Assert.Same(once.XAxis, twice.XAxis);
        Assert.Equal(once.ReferenceLines, twice.ReferenceLines);
        Assert.Equal(once.StatisticsPanel!.Rows, twice.StatisticsPanel!.Rows);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void ApplyGivesUnsupportedGraphsNoLines(GraphType graphType)
    {
        var frame = Frame();

        Assert.Same(frame, GraphPresentation.Apply(frame, Data(graphType, [11, 12]), Configuration(graphType, new Specification(0, 15, 30)), Token));
    }

    // ---- Regression: the plot itself never changes ----

    private static readonly double[] Values = [.. Enumerable.Range(0, 400).Select(index => 15 + (Math.Sin(index * 0.7) * 0.2) + (index % 7 * 0.01))];

    private static readonly string?[] Groups = [.. Enumerable.Range(0, 400).Select(index => index % 9 == 0 ? null : $"Lot {index % 3}")];

    private static readonly Specification FarSpecification = new(14, 15, 16);

    [Fact]
    public void HistogramBinsAndCountsDoNotDependOnTheSpecification()
    {
        var data = Data(GraphType.Histogram, Values, Groups);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;
        var before = model.Bins.ToArray();
        var counts = model.Series.Select(series => series.Counts.ToArray()).ToArray();

        var frame = GraphPresentation.Apply(model.Frame, data, Configuration(GraphType.Histogram, FarSpecification), Token);

        Assert.NotEqual(model.Frame.XAxis.Range, frame.XAxis.Range);
        Assert.Equal(before, model.Bins.ToArray());
        Assert.Equal(counts, model.Series.Select(series => series.Counts.ToArray()).ToArray());
        Assert.Equal(model.Frame.YAxis, frame.YAxis);
    }

    [Fact]
    public void ProbabilityPlotPointsAndFittedLinesDoNotDependOnTheSpecification()
    {
        var data = Data(GraphType.ProbabilityPlot, Values, Groups);
        var builder = new ProbabilityPlotRenderModelBuilder(maximumRenderedPoints: 50);
        var plain = builder.Build(data, new ProbabilityPlotLabels("Reg1", "Lot"), Token)!;
        var again = builder.Build(data, new ProbabilityPlotLabels("Reg1", "Lot"), Token)!;

        var frame = GraphPresentation.Apply(again.Frame, data, Configuration(GraphType.ProbabilityPlot, FarSpecification), Token);

        Assert.NotEqual(again.Frame.XAxis.Range, frame.XAxis.Range);
        Assert.Equal(again.Frame.YAxis, frame.YAxis);
        Assert.Equal(plain.Series.Count, again.Series.Count);
        for (var index = 0; index < plain.Series.Count; index++)
        {
            Assert.Equal(plain.Series[index].Points.ToArray(), again.Series[index].Points.ToArray());
            Assert.Equal(plain.Series[index].FittedLine, again.Series[index].FittedLine);
            Assert.Equal(plain.Series[index].ObservationCount, again.Series[index].ObservationCount);
        }
    }

    [Fact]
    public void EmpiricalCdfPointsDoNotDependOnTheSpecification()
    {
        var data = Data(GraphType.EmpiricalCdf, Values, Groups);
        var builder = new EmpiricalCdfRenderModelBuilder(maximumRenderedPoints: 50);
        var plain = builder.Build(data, new EmpiricalCdfLabels("Reg1", "Lot"), Token)!;
        var again = builder.Build(data, new EmpiricalCdfLabels("Reg1", "Lot"), Token)!;

        var frame = GraphPresentation.Apply(again.Frame, data, Configuration(GraphType.EmpiricalCdf, FarSpecification), Token);

        Assert.NotEqual(again.Frame.XAxis.Range, frame.XAxis.Range);
        Assert.Equal(again.Frame.YAxis, frame.YAxis);
        for (var index = 0; index < plain.Series.Count; index++)
        {
            Assert.Equal(plain.Series[index].Points.ToArray(), again.Series[index].Points.ToArray());
        }
    }

    [Fact]
    public void ThePanelStatisticsDoNotDependOnTheSpecification()
    {
        var data = Data(GraphType.Histogram, Values, Groups);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;

        var without = GraphPresentation.Apply(model.Frame, data, Configuration(GraphType.Histogram, Specification.None), Token);
        var with = GraphPresentation.Apply(model.Frame, data, Configuration(GraphType.Histogram, FarSpecification), Token);

        Assert.Equal(without.StatisticsPanel!.Rows, with.StatisticsPanel!.Rows);
        var ungrouped = GraphPresentation.Apply(model.Frame, Data(GraphType.Histogram, Values), Configuration(GraphType.Histogram, FarSpecification), Token);
        var row = Assert.Single(ungrouped.StatisticsPanel!.Rows);
        Assert.Equal(Descriptives.Mean(Values), row.Mean);
        Assert.Equal(Descriptives.StandardDeviation(Values), row.StandardDeviation);
        Assert.Equal(Values.Length, row.Count);
    }
}
