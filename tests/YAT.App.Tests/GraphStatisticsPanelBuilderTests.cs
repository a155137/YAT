using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.app.Analyses;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The statistics panel of a histogram, a probability plot and an empirical CDF: Mean, StDev and N of every series,
// worked out once from the graph data the graph was prepared from. No storage here - the graph data is the input.
public class GraphStatisticsPanelBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(double[] values, GraphGroupData? group = null, GraphType graphType = GraphType.Histogram) =>
        new(graphType, Guid.NewGuid(), Column("Reg1"), values, group);

    private static StringGroupData Text(params string?[] values) => new(Column("SITE", WorksheetDataType.String), values);

    private static NumericGroupData Numbers(params double?[] values) => new(Column("SITE"), values);

    private static GraphStatisticsPanel Build(UnivariateGraphData data) =>
        GraphStatisticsPanelBuilder.Build(data, Token) ?? throw new InvalidOperationException("The panel has no rows.");

    private static GraphRenderModel Frame() =>
        new("Reg1", new GraphAxisModel(new GraphAxisRange(0, 1), GraphAxisTicks.Evenly(new GraphAxisRange(0, 1)), "Reg1"),
            new GraphAxisModel(new GraphAxisRange(0, 1), GraphAxisTicks.Evenly(new GraphAxisRange(0, 1)), "Count"), null);

    private static void AssertSamePanel(GraphStatisticsPanel expected, GraphStatisticsPanel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.GroupHeader, actual.GroupHeader);
        Assert.Equal(expected.Rows, actual.Rows);
    }

    // Equal but for rounding: the same statistic, summed in a different order.
    private static void AssertSameStatistic(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) <= 1e-12 * Math.Max(Math.Abs(expected), 1), $"{actual:R} is not {expected:R}.");

    // ---- Ungrouped ----

    [Fact]
    public void AnUngroupedGraphHasOneRowWithTheStatisticsOfAnalytics()
    {
        double[] values = [1.5, 2.25, 3, 10, -4.125];
        var panel = Build(Data(values));

        Assert.Equal(GraphStatisticsPanelBuilder.Title, panel.Title);
        Assert.False(panel.IsGrouped);
        Assert.Null(panel.GroupHeader);

        var row = Assert.Single(panel.Rows);
        Assert.Equal(string.Empty, row.Label);
        Assert.Null(row.SeriesIndex);
        Assert.Equal(5, row.Count);
        Assert.Equal(Descriptives.Mean(values), row.Mean);
        Assert.Equal(Descriptives.StandardDeviation(values), row.StandardDeviation);
    }

    [Fact]
    public void NumbersAreWrittenInTheAnalysisFormat()
    {
        var panel = Build(Data([1, 2, 2]));
        var row = Assert.Single(panel.Rows);

        Assert.Equal("1.6666667", row.MeanText);
        Assert.Equal("0.57735027", row.StandardDeviationText);
        Assert.Equal("3", row.CountText);
    }

    [Fact]
    public void ASingleObservationHasNoStandardDeviation()
    {
        var row = Assert.Single(Build(Data([42.5])).Rows);

        Assert.Equal(1, row.Count);
        Assert.Equal(42.5, row.Mean);
        Assert.Null(row.StandardDeviation);
        Assert.Equal(GraphStatisticsPanelBuilder.UndefinedText, row.StandardDeviationText);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.3)]
    [InlineData(1e-7)]
    [InlineData(-123456.789)]
    public void ConstantDataHasAStandardDeviationOfExactlyZero(double value)
    {
        var row = Assert.Single(Build(Data([.. Enumerable.Repeat(value, 7)])).Rows);

        Assert.Equal(0d, row.StandardDeviation);
        Assert.Equal("0", row.StandardDeviationText);
        Assert.Equal(7, row.Count);
    }

    [Fact]
    public void NoObservationsMeansNoPanel()
    {
        Assert.Null(GraphStatisticsPanelBuilder.Build(Data([]), Token));
        Assert.Null(GraphStatisticsPanelBuilder.Build(Data([], Text()), Token));
    }

    [Fact]
    public void AValueThatIsNotFiniteIsSkippedAsTheGraphsSkipIt()
    {
        var data = Data([1, double.NaN, 3, double.PositiveInfinity], Text("A", "A", "B", "B"));
        var histogram = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "SITE"), Token)!;
        var panel = Build(data);

        Assert.Equal(histogram.Series.Select(series => series.ObservationCount), panel.Rows.Select(row => row.Count));
        Assert.Equal([1d, 3d], panel.Rows.Select(row => row.Mean));

        var ungrouped = Assert.Single(Build(Data([2, double.NaN, 4])).Rows);
        Assert.Equal(2, ungrouped.Count);
        Assert.Equal(3, ungrouped.Mean);

        Assert.Null(GraphStatisticsPanelBuilder.Build(Data([double.NaN]), Token));
    }

    // ---- Grouped ----

    [Fact]
    public void GroupsFollowTheOrderTheyWereFirstObservedIn()
    {
        var panel = Build(Data([1, 2, 3, 4, 5, 6], Text("B", "A", "B", "C", "A", "B")));

        Assert.True(panel.IsGrouped);
        Assert.Equal("SITE", panel.GroupHeader);
        Assert.Equal(["B", "A", "C"], panel.Rows.Select(row => row.Label));
        Assert.Equal([0, 1, 2], panel.Rows.Select(row => row.SeriesIndex!.Value));
        Assert.Equal([3, 2, 1], panel.Rows.Select(row => row.Count));
        Assert.Equal([Descriptives.Mean([1d, 3, 6]), 3.5, 4], panel.Rows.Select(row => row.Mean));
    }

    [Fact]
    public void RowsWithoutAGroupValueAreCountedUnderMissingWhereTheyWereFirstObserved()
    {
        var panel = Build(Data([1, 2, 3, 4, 5], Text("A", null, "B", null, "A")));

        Assert.Equal(["A", GraphStatisticsPanelBuilder.MissingGroupLabel, "B"], panel.Rows.Select(row => row.Label));
        Assert.Equal([2, 2, 1], panel.Rows.Select(row => row.Count));
        Assert.Equal(3, panel.Rows[1].Mean);
    }

    [Fact]
    public void NumericGroupsAreLabelledAsTheGraphsLabelThem()
    {
        var panel = Build(Data([1, 2, 3, 4], Numbers(1.5, 2, null, 1.5)));

        Assert.Equal(["1.5", "2", GraphStatisticsPanelBuilder.MissingGroupLabel], panel.Rows.Select(row => row.Label));
        Assert.Equal([2, 1, 1], panel.Rows.Select(row => row.Count));
    }

    [Fact]
    public void AGroupOfOneHasNoStandardDeviationWhileTheOthersDo()
    {
        var panel = Build(Data([1, 2, 3, 10], Text("A", "A", "A", "B")));

        Assert.Equal(1d, panel.Rows[0].StandardDeviation);
        Assert.Null(panel.Rows[1].StandardDeviation);
        Assert.Equal(GraphStatisticsPanelBuilder.UndefinedText, panel.Rows[1].StandardDeviationText);
        Assert.Equal("1", panel.Rows[1].CountText);
    }

    [Fact]
    public void TheCountsOfTheGroupsAddUpToTheObservations()
    {
        var values = Enumerable.Range(0, 5000).Select(index => Math.Sin(index) * 100).ToArray();
        var groups = Enumerable.Range(0, 5000).Select(index => index % 13 == 0 ? null : $"G{index % 37}").ToArray();
        var panel = Build(Data(values, Text(groups)));

        Assert.Equal(values.Length, panel.Rows.Sum(row => row.Count));
        Assert.Equal(38, panel.Rows.Count);
    }

    [Fact]
    public void TheModelKeepsEveryGroupHoweverManyThereAre()
    {
        var groups = Enumerable.Range(0, 250).Select(index => $"Lot {index}").ToArray();
        var panel = Build(Data([.. Enumerable.Range(0, 250).Select(index => (double)index)], Text(groups)));

        Assert.Equal(250, panel.Rows.Count);
        Assert.Equal(groups, panel.Rows.Select(row => row.Label));
    }

    [Fact]
    public void ACancelledRequestIsNotBuilt()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => GraphStatisticsPanelBuilder.Build(Data([1, 2, 3]), cancellation.Token));
    }

    // ---- The same statistics in every graph ----

    private static readonly string?[] Lots = ["B", "A", null, "B", "A", "C", "B", null, "A", "B", "C", "A"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryGraphOfTheSameDataHasTheSamePanelAndItMatchesItsSeries(bool grouped)
    {
        var groups = grouped ? Lots : null;
        double[] values = [4.1, 5.2, 3.3, 7.7, 5.5, 2.25, 9.125, 1.5, 6, 6.5, 3.75, 8];
        GraphGroupData? group = groups is null ? null : Text(groups);

        var histogramData = Data(values, group, GraphType.Histogram);
        var probabilityData = Data(values, group, GraphType.ProbabilityPlot);
        var ecdfData = Data(values, group, GraphType.EmpiricalCdf);

        var histogram = new HistogramRenderModelBuilder().Build(histogramData, new HistogramPlotLabels("Reg1", "SITE"), Token)!;
        var probability = new ProbabilityPlotRenderModelBuilder().Build(probabilityData, new ProbabilityPlotLabels("Reg1", "SITE"), Token)!;
        var ecdf = new EmpiricalCdfRenderModelBuilder().Build(ecdfData, new EmpiricalCdfLabels("Reg1", "SITE"), Token)!;

        var panel = Build(histogramData);
        AssertSamePanel(panel, Build(probabilityData));
        AssertSamePanel(panel, Build(ecdfData));

        var series = histogram.Series.Select(item => (item.Label, item.SeriesIndex, item.ObservationCount)).ToArray();
        Assert.Equal(series, probability.Series.Select(item => (item.Label, item.SeriesIndex, item.ObservationCount)));
        Assert.Equal(series, ecdf.Series.Select(item => (item.Label, item.SeriesIndex, item.ObservationCount)));

        Assert.Equal(series.Select(item => item.Label), panel.Rows.Select(row => row.Label));
        Assert.Equal(series.Select(item => item.ObservationCount), panel.Rows.Select(row => row.Count));
        Assert.Equal(
            series.Select(item => groups is null ? (int?)null : item.SeriesIndex),
            panel.Rows.Select(row => row.SeriesIndex));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFittedLineOfAProbabilityPlotHasThePanelsMeanAndStandardDeviation(bool grouped)
    {
        var groups = grouped ? Lots : null;
        double[] values = [4.1, 5.2, 3.3, 7.7, 5.5, 2.25, 9.125, 1.5, 6, 6.5, 3.75, 8];
        var data = Data(values, groups is null ? null : Text(groups), GraphType.ProbabilityPlot);

        var plot = new ProbabilityPlotRenderModelBuilder().Build(data, new ProbabilityPlotLabels("Reg1", "SITE"), Token)!;
        var panel = Build(data);

        foreach (var (series, row) in plot.Series.Zip(panel.Rows))
        {
            if (series.FittedLine is { } line)
            {
                // The same Descriptives over the same observations. The plot sums them in ascending order (it sorts
                // them for the ranks) and the panel in worksheet order, so the two may differ in the last bits only.
                AssertSameStatistic(row.Mean, line.Mean);
                AssertSameStatistic(row.StandardDeviation!.Value, line.StandardDeviation);
                Assert.Equal(row.MeanText, AnalysisNumberFormat.Statistic(line.Mean));
                Assert.Equal(row.StandardDeviationText, AnalysisNumberFormat.Statistic(line.StandardDeviation));
            }
        }

        Assert.Contains(plot.Series, series => series.FittedLine is not null);
    }

    [Fact]
    public void SamplingTheDrawnPointsDoesNotChangeThePanel()
    {
        var values = Enumerable.Range(0, 4000).Select(index => Math.Cos(index * 0.37) * 50).ToArray();
        var groups = Enumerable.Range(0, 4000).Select(index => index % 3 == 0 ? "A" : "B").ToArray();
        var data = Data(values, Text(groups), GraphType.EmpiricalCdf);

        var sampled = new EmpiricalCdfRenderModelBuilder(maximumRenderedPoints: 200).Build(data, new EmpiricalCdfLabels("Reg1", "SITE"), Token)!;
        var full = new EmpiricalCdfRenderModelBuilder().Build(data, new EmpiricalCdfLabels("Reg1", "SITE"), Token)!;

        Assert.True(sampled.RenderedPointCount < full.RenderedPointCount);
        var panel = Build(data);
        Assert.Equal(sampled.Series.Select(series => series.ObservationCount), panel.Rows.Select(row => row.Count));
        Assert.Equal(values.Length, panel.Rows.Sum(row => row.Count));
    }

    // ---- Attaching the panel to a graph ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void AGraphTypeWithThePanelGetsItByDefault(GraphType graphType)
    {
        var data = Data([1, 2, 3], graphType: graphType);

        var frame = GraphStatisticsPanelBuilder.Attach(
            Frame(), data, GraphTypeDefinitions.For(graphType), Token);

        AssertSamePanel(Build(data), frame.StatisticsPanel);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void ThePanelIsWorkedOutEvenWhenItIsToBeHidden(GraphType graphType)
    {
        // Task #045: the panel is always worked out with the graph; Hide only keeps it off the frame that is drawn.
        var data = Data([1, 2, 3], graphType: graphType);
        var configuration = new GraphConfiguration(graphType, Guid.NewGuid(), [])
        {
            StatisticsOptions = new GraphStatisticsOptions(GraphStatisticsMode.Hide)
        };

        var state = GraphPresentation.Present(Frame(), data, configuration, Token);

        AssertSamePanel(Build(data), state.BaseFrame.StatisticsPanel);
        Assert.Null(state.Frame.StatisticsPanel);
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void AGraphTypeWithoutThePanelNeverGetsOne(GraphType graphType)
    {
        var original = Frame();

        var frame = GraphStatisticsPanelBuilder.Attach(
            original, Data([1, 2, 3]), GraphTypeDefinitions.For(graphType), Token);

        Assert.Same(original, frame);
    }

    [Fact]
    public void GraphDataThatIsNotOneVariableGetsNoPanel()
    {
        var original = Frame();
        var scatter = new ScatterGraphData(Guid.NewGuid(), Column("X"), Column("Y"), new double[] { 1, 2 }, new double[] { 3, 4 }, null);

        var frame = GraphStatisticsPanelBuilder.Attach(
            original, scatter, GraphTypeDefinitions.For(GraphType.Histogram), Token);

        Assert.Same(original, frame);
    }

    [Fact]
    public void AGraphWithoutObservationsGetsNoPanel()
    {
        var original = Frame();

        var frame = GraphStatisticsPanelBuilder.Attach(
            original, Data([]), GraphTypeDefinitions.For(GraphType.Histogram), Token);

        Assert.Null(frame.StatisticsPanel);
    }

    // ---- The model ----

    [Fact]
    public void ARowIsMadeOfFiniteStatistics()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphStatisticsRow("A", 0, 0, 1, null, "0", "1", "—"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GraphStatisticsRow("A", -1, 1, 1, null, "1", "1", "—"));
        Assert.Throws<ArgumentException>(() => new GraphStatisticsRow("A", 0, 2, double.NaN, 1, "2", "NaN", "1"));
        Assert.Throws<ArgumentException>(() => new GraphStatisticsRow("A", 0, 2, 1, -1, "2", "1", "-1"));
        Assert.Throws<ArgumentException>(() => new GraphStatisticsRow("A", 0, 2, 1, double.PositiveInfinity, "2", "1", "∞"));
    }

    [Fact]
    public void APanelHasRowsAndAnUngroupedOneHasExactlyOne()
    {
        var row = new GraphStatisticsRow(string.Empty, null, 1, 1, null, "1", "1", "—");

        Assert.Throws<ArgumentException>(() => new GraphStatisticsPanel("Statistics", null, []));
        Assert.Throws<ArgumentException>(() => new GraphStatisticsPanel("Statistics", null, [row, row]));
        Assert.True(new GraphStatisticsPanel("Statistics", "SITE", [row, row]).IsGrouped);
    }
}
