using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Turning the observations of one variable into a normal probability plot: series, ranks, scores, fitted lines and
// axes. No worksheet and no storage here - the graph data is the input.
public class ProbabilityPlotRenderModelBuilderTests
{
    private static readonly ProbabilityPlotLabels Labels = new("Reg1", "SITE");

    private const double Tolerance = 1e-9;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(double[] values, GraphGroupData? group = null) =>
        new(GraphType.ProbabilityPlot, Guid.NewGuid(), Column("Reg1"), values, group);

    private static StringGroupData Text(params string?[] values) =>
        new(Column("SITE", WorksheetDataType.String), values);

    private static NumericGroupData Numbers(params double?[] values) =>
        new(Column("SITE"), values);

    private static ProbabilityPlotRenderModel Build(
        UnivariateGraphData data,
        int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints) =>
        new ProbabilityPlotRenderModelBuilder(maximumRenderedPoints).Build(data, Labels, Token)
        ?? throw new InvalidOperationException("The probability plot has no data.");

    private static ProbabilityPlotSeriesRenderModel SeriesOf(ProbabilityPlotRenderModel model, string label) =>
        model.Series.Single(series => series.Label == label);

    // The score Benard's position gives rank i of n.
    private static double Score(int rank, int sampleCount) =>
        NormalDistribution.InverseCdf(ProbabilityPlotPositions.Benard(rank, sampleCount));

    // 1
    [Fact]
    public void WithoutAGroupColumnThereIsOneSeriesAndNoLegend()
    {
        var model = Build(Data([3, 1, 2]));

        var series = Assert.Single(model.Series);
        Assert.Equal(string.Empty, series.Label);
        Assert.Equal(0, series.SeriesIndex);
        Assert.Equal(3, series.ObservationCount);
        Assert.Null(model.Frame.Legend);
    }

    // 2
    [Fact]
    public void TheObservationsAreSortedAndScoredByTheirRank()
    {
        var model = Build(Data([30, 10, 20, 40]));
        var points = Assert.Single(model.Series).Points.ToArray();

        Assert.Equal([10, 20, 30, 40], points.Select(point => point.Value));
        Assert.Equal(Score(1, 4), points[0].Score, Tolerance);
        Assert.Equal(Score(2, 4), points[1].Score, Tolerance);
        Assert.Equal(Score(3, 4), points[2].Score, Tolerance);
        Assert.Equal(Score(4, 4), points[3].Score, Tolerance);
    }

    // 3
    [Fact]
    public void EveryGroupIsRankedOnItsOwn()
    {
        // Three observations of "A" and seven of "B": A's ranks are 1..3 of 3, not 1..3 of 10.
        var values = new double[10];
        var groups = new string?[10];
        for (var index = 0; index < 10; index++)
        {
            values[index] = index;
            groups[index] = index < 3 ? "A" : "B";
        }

        var model = Build(Data(values, Text(groups)));
        var a = SeriesOf(model, "A").Points.ToArray();
        var b = SeriesOf(model, "B").Points.ToArray();

        Assert.Equal(3, a.Length);
        Assert.Equal(7, b.Length);
        Assert.Equal(Score(1, 3), a[0].Score, Tolerance);
        Assert.Equal(Score(3, 3), a[^1].Score, Tolerance);
        Assert.Equal(Score(1, 7), b[0].Score, Tolerance);
        Assert.Equal(Score(7, 7), b[^1].Score, Tolerance);

        // Both series start at the same score even though their observations are far apart: that is what ranking each
        // group on its own means.
        Assert.Equal(a[0].Score, -a[^1].Score, Tolerance);
    }

    // 4
    [Fact]
    public void RepeatedValuesStaySeparateObservations()
    {
        var model = Build(Data([1, 1, 1, 2]));
        var points = Assert.Single(model.Series).Points.ToArray();

        Assert.Equal(4, points.Length);
        Assert.Equal([1, 1, 1, 2], points.Select(point => point.Value));
        Assert.Distinct(points.Select(point => point.Score));
        Assert.Equal(points.Select(point => point.Score).Order(), points.Select(point => point.Score));
    }

    // 5
    [Fact]
    public void ObservationsWithoutAGroupValueAreKeptInTheirOwnSeries()
    {
        var model = Build(Data([1, 2, 3], Text("A", null, "A")));

        Assert.Equal(["A", ProbabilityPlotRenderModelBuilder.MissingGroupLabel], model.Series.Select(series => series.Label));
        Assert.Equal([2], SeriesOf(model, ProbabilityPlotRenderModelBuilder.MissingGroupLabel).Points.ToArray().Select(point => point.Value));
    }

    // 6
    [Fact]
    public void ANumericGroupBecomesOneSeriesPerValueFromTheSmallestUp()
    {
        var model = Build(Data([1, 2, 3], Numbers(2, 1.5, 2)));

        // Seen as 2, 1.5; drawn from the smallest value up (Task #059).
        Assert.Equal(["1.5", "2"], model.Series.Select(series => series.Label));
        Assert.Equal([0, 1], model.Series.Select(series => series.SeriesIndex));
    }

    // 7
    [Fact]
    public void TheFittedLineComesFromTheSeriesOwnMeanAndStandardDeviation()
    {
        double[] values = [2, 4, 4, 4, 5, 5, 7, 9];
        var model = Build(Data(values));
        var series = Assert.Single(model.Series);

        var line = series.FittedLine;
        Assert.NotNull(line);
        Assert.Equal(Descriptives.Mean(values), line.Mean, Tolerance);
        Assert.Equal(Descriptives.StandardDeviation(values), line.StandardDeviation, Tolerance);

        // x = mean + standard deviation * score, at both ends of the axis.
        Assert.Equal(model.Frame.YAxis.Range.Minimum, line.FromScore, Tolerance);
        Assert.Equal(model.Frame.YAxis.Range.Maximum, line.ToScore, Tolerance);
        Assert.Equal(line.Mean + (line.StandardDeviation * line.FromScore), line.FromValue, Tolerance);
        Assert.Equal(line.Mean + (line.StandardDeviation * line.ToScore), line.ToValue, Tolerance);
    }

    // 8
    [Fact]
    public void EveryGroupGetsItsOwnLine()
    {
        var values = new double[20];
        var groups = new string?[20];
        for (var index = 0; index < 20; index++)
        {
            values[index] = index < 10 ? index : 100 + (index * 3);
            groups[index] = index < 10 ? "A" : "B";
        }

        var model = Build(Data(values, Text(groups)));

        Assert.NotEqual(SeriesOf(model, "A").FittedLine!.Mean, SeriesOf(model, "B").FittedLine!.Mean);
        Assert.NotEqual(SeriesOf(model, "A").FittedLine!.StandardDeviation, SeriesOf(model, "B").FittedLine!.StandardDeviation);
    }

    // 9
    [Fact]
    public void ObservationsWithNoSpreadArePlottedWithoutALine()
    {
        var model = Build(Data([100, 100, 100, 100]));
        var series = Assert.Single(model.Series);

        Assert.Equal(4, series.Points.Length);
        Assert.All(series.Points.ToArray(), point => Assert.Equal(100, point.Value));
        Assert.Null(series.FittedLine);
        Assert.True(model.Frame.XAxis.Range.IsValid);
    }

    // 10
    [Fact]
    public void OneObservationIsOnePointInTheMiddleAndNoLine()
    {
        var model = Build(Data([7.5]));
        var series = Assert.Single(model.Series);
        var point = Assert.Single(series.Points.ToArray());

        Assert.Equal(7.5, point.Value);
        Assert.Equal(0, point.Score, Tolerance);
        Assert.Null(series.FittedLine);
        Assert.True(model.Frame.XAxis.Range.IsValid);
        Assert.True(model.Frame.YAxis.Range.IsValid);
    }

    // 11
    [Fact]
    public void TheFrameIsTitledAfterTheVariableItPlots()
    {
        var model = Build(Data([1, 2, 3]));

        Assert.Equal("Normal Probability Plot of Reg1", model.Frame.Title);
        Assert.Equal("Reg1", model.Frame.XAxis.Title);
        Assert.Equal(ProbabilityAxis.Title, model.Frame.YAxis.Title);
    }

    // 12
    [Fact]
    public void TheVerticalAxisIsTheProbabilityAxis()
    {
        var model = Build(Data([.. Enumerable.Range(0, 50).Select(value => (double)value)]));

        Assert.Equal(ProbabilityAxis.Axis(model.Series[0].Points.Span[0].Score, model.Series[0].Points.Span[^1].Score).Range, model.Frame.YAxis.Range);
        Assert.Contains(model.Frame.YAxis.Ticks, tick => tick.Label == "50");
        Assert.Equal(0, Assert.Single(model.Frame.YAxis.Ticks, tick => tick.Label == "50").Value, Tolerance);
    }

    // 13
    [Fact]
    public void TheHorizontalAxisCoversTheDataAndTheWholeFittedLine()
    {
        double[] values = [10, 12, 14, 16, 18, 20];
        var model = Build(Data(values));
        var line = Assert.Single(model.Series).FittedLine!;

        Assert.True(model.Frame.XAxis.Range.Minimum <= Math.Min(values.Min(), line.FromValue));
        Assert.True(model.Frame.XAxis.Range.Maximum >= Math.Max(values.Max(), line.ToValue));
        Assert.NotEmpty(model.Frame.XAxis.Ticks);
    }

    // 14
    [Fact]
    public void TheLegendMirrorsTheSeriesAndCarriesTheGroupColumnName()
    {
        var model = Build(Data([1, 2, 3], Text("A", "B", null)));

        Assert.NotNull(model.Frame.Legend);
        Assert.Equal("SITE", model.Frame.Legend.Title);
        Assert.Equal(
            model.Series.Select(series => (series.Label, series.SeriesIndex)),
            model.Frame.Legend.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
    }

    // 15
    [Fact]
    public void NoObservationsMeansNoPlotAtAll()
    {
        Assert.Null(new ProbabilityPlotRenderModelBuilder().Build(Data([]), Labels, Token));
        Assert.Null(new ProbabilityPlotRenderModelBuilder().Build(Data([double.NaN, double.PositiveInfinity]), Labels, Token));
    }

    // 16
    [Fact]
    public void ObservationsThatCannotBePlottedAreLeftOut()
    {
        var model = Build(Data([1, double.NaN, 3, double.NegativeInfinity, 5]));

        Assert.Equal(3, model.SourceObservationCount);
        Assert.Equal([1, 3, 5], Assert.Single(model.Series).Points.ToArray().Select(point => point.Value));
    }

    // 17
    [Theory]
    [InlineData(-1000, -1)]
    [InlineData(0.0001, 0.0009)]
    [InlineData(1e8, 1e9)]
    public void ValuesOfAnySizeAreHandled(double from, double to)
    {
        var values = Enumerable.Range(0, 25).Select(index => from + ((to - from) * index / 24)).ToArray();

        var model = Build(Data(values));

        Assert.Equal(25, model.SourceObservationCount);
        Assert.True(model.Frame.XAxis.Range.IsValid);
        Assert.All(Assert.Single(model.Series).Points.ToArray(), point => Assert.True(double.IsFinite(point.Score)));
    }

    // 18
    [Fact]
    public void AnOutlierStaysAnObservationOfItsOwn()
    {
        var values = Enumerable.Range(0, 30).Select(index => (double)index).Append(10_000).ToArray();

        var model = Build(Data(values));
        var points = Assert.Single(model.Series).Points.ToArray();

        Assert.Equal(10_000, points[^1].Value);
        Assert.Equal(Score(31, 31), points[^1].Score, Tolerance);
        Assert.True(model.Frame.XAxis.Range.Maximum >= 10_000);
    }

    // 19
    [Fact]
    public void ThePlotIsCappedWithoutChangingAnythingItWasComputedFrom()
    {
        var values = Enumerable.Range(0, 1_000).Select(index => Math.Sin(index / 50d) * 10).ToArray();

        var full = Build(Data(values));
        var capped = Build(Data(values), maximumRenderedPoints: 100);

        Assert.Equal(1_000, capped.SourceObservationCount);
        Assert.Equal(100, capped.RenderedPointCount);
        Assert.True(capped.WasSampled);
        Assert.False(full.WasSampled);

        // The statistics, the fitted line and both axes are the same: only the drawing was thinned out.
        Assert.Equal(full.Frame.XAxis.Range, capped.Frame.XAxis.Range);
        Assert.Equal(full.Frame.YAxis.Range, capped.Frame.YAxis.Range);
        Assert.Equal(full.Series[0].FittedLine!.Mean, capped.Series[0].FittedLine!.Mean);
        Assert.Equal(full.Series[0].FittedLine!.StandardDeviation, capped.Series[0].FittedLine!.StandardDeviation);
        Assert.Equal(1_000, capped.Series[0].ObservationCount);

        // The extremes are what a probability plot is read by, so they are the points that must survive.
        var points = capped.Series[0].Points.ToArray();
        Assert.Equal(full.Series[0].Points.Span[0], points[0]);
        Assert.Equal(full.Series[0].Points.Span[^1], points[^1]);
    }

    // 20
    [Fact]
    public void EveryGroupKeepsAPointWhileTheBudgetAllows()
    {
        var values = new double[1_000];
        var groups = new string?[1_000];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = index;
            groups[index] = index % 10 == 0 ? "rare" : "common";
        }

        var capped = Build(Data(values, Text(groups)), maximumRenderedPoints: 50);

        Assert.True(capped.RenderedPointCount <= 50);
        Assert.Equal(2, capped.Series.Count);
        Assert.All(capped.Series, series => Assert.True(series.Points.Length >= 1));
    }

    // 21
    [Fact]
    public void ThePreparedPlotIsTheSameEveryTime()
    {
        var data = Data([.. Enumerable.Range(0, 60).Select(value => Math.Sqrt(value))], Text([.. Enumerable.Range(0, 60).Select(value => (string?)$"S{value % 3}")]));

        var first = Build(data);
        var second = Build(data);

        Assert.Equal(first.Series.Select(series => series.Label), second.Series.Select(series => series.Label));
        Assert.Equal(
            first.Series.SelectMany(series => series.Points.ToArray()),
            second.Series.SelectMany(series => series.Points.ToArray()));
        Assert.Equal(first.Frame.XAxis.Range, second.Frame.XAxis.Range);
    }

    // 22
    [Fact]
    public void PreparationCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ProbabilityPlotRenderModelBuilder().Build(Data([1, 2, 3]), Labels, cancellation.Token));
    }

    // Hotfix #034.2B: repeated decimals that are not exact in binary are still constant, so there is no spread to fit
    // a line to.
    [Fact]
    public void RepeatedDecimalsHaveNoFittedLine()
    {
        var model = Build(Data([0.1, 0.1, 0.1]));

        Assert.Null(Assert.Single(model.Series).FittedLine);
    }

    // 23
    [Fact]
    public void TheBuilderNeedsDataAndLabels()
    {
        var builder = new ProbabilityPlotRenderModelBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!, Labels, Token));
        Assert.Throws<ArgumentNullException>(() => builder.Build(Data([1]), null!, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProbabilityPlotRenderModelBuilder(0));
    }

    // ---- Show fitted line (Task #037) ----

    private static readonly ProbabilityPlotOptions Off = new(ShowFittedLine: false);

    private static ProbabilityPlotRenderModel Build(
        UnivariateGraphData data,
        ProbabilityPlotOptions options,
        int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints) =>
        new ProbabilityPlotRenderModelBuilder(maximumRenderedPoints).Build(data, Labels, options, Token)
        ?? throw new InvalidOperationException("The probability plot has no data.");

    private static string Axis(GraphAxisModel axis) =>
        FormattableString.Invariant($"{axis.Range.Minimum:R}..{axis.Range.Maximum:R} '{axis.Title}' [")
        + string.Join(",", axis.Ticks.Select(tick => FormattableString.Invariant($"{tick.Value:R}={tick.Label}"))) + "]";

    private static string Line(ProbabilityPlotFittedLine? line) =>
        line is null
            ? "none"
            : FormattableString.Invariant($"{line.Mean:R}/{line.StandardDeviation:R}/{line.FromScore:R}..{line.ToScore:R}/{line.FromValue:R}..{line.ToValue:R}");

    // Everything a model says, bit for bit: the frame (title, both axes with every tick, legend), and every series
    // with its label, index, counts, points and fitted line. Without the lines and the horizontal axis when asked.
    private static string Describe(ProbabilityPlotRenderModel model, bool withLinesAndXAxis = true)
    {
        var text = new System.Text.StringBuilder();
        text.Append(model.Frame.Title).Append(" y=").Append(Axis(model.Frame.YAxis));
        if (withLinesAndXAxis)
        {
            text.Append(" x=").Append(Axis(model.Frame.XAxis));
        }

        text.Append(" legend=").Append(model.Frame.Legend is { } legend
            ? legend.Title + ":" + string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}"))
            : "none");
        text.Append(FormattableString.Invariant($" n={model.SourceObservationCount} drawn={model.RenderedPointCount}"));

        foreach (var series in model.Series)
        {
            text.Append(FormattableString.Invariant($" [{series.Label}#{series.SeriesIndex} n={series.ObservationCount} points="));
            text.AppendJoin(",", series.Points.ToArray().Select(point => FormattableString.Invariant($"{point.Value:R}:{point.Score:R}")));
            if (withLinesAndXAxis)
            {
                text.Append(" line=").Append(Line(series.FittedLine));
            }

            text.Append(']');
        }

        return text.ToString();
    }

    private static readonly double[] Skewed = [.. Enumerable.Range(1, 400).Select(index => Math.Exp(Math.Sin(index * 0.37) * 1.5))];

    private static readonly string?[] SkewedGroups = [.. Enumerable.Range(1, 400).Select(index => index % 11 == 0 ? null : $"Lot {index % 3}")];

    // A and B: the options-free call is the default options, bit for bit - the backward-compatible path.
    [Theory]
    [InlineData(false, DisplaySampling.DefaultMaximumRenderedPoints)]
    [InlineData(true, DisplaySampling.DefaultMaximumRenderedPoints)]
    [InlineData(true, 40)]
    public void WithoutOptionsThePlotIsTheDefaultOptionsPlot(bool grouped, int maximumRenderedPoints)
    {
        var data = Data(Skewed, grouped ? Text(SkewedGroups) : null);

        var withoutOptions = Build(data, maximumRenderedPoints);
        var withDefault = Build(data, ProbabilityPlotOptions.Default, maximumRenderedPoints);
        var explicitOn = Build(data, new ProbabilityPlotOptions(ShowFittedLine: true), maximumRenderedPoints);

        Assert.Equal(Describe(withoutOptions), Describe(withDefault));
        Assert.Equal(Describe(withoutOptions), Describe(explicitOn));
        Assert.All(withDefault.Series, series => Assert.NotNull(series.FittedLine));
    }

    // C: on, the line is today's line - the series' own mean and standard deviation across the vertical axis.
    [Fact]
    public void OnTheFittedLineIsTheSeriesOwnMeanAndStandardDeviation()
    {
        double[] values = [3, 1, 4, 1, 5, 9, 2, 6];
        var model = Build(Data(values), ProbabilityPlotOptions.Default);

        var line = Assert.Single(model.Series).FittedLine!;
        double[] sorted = [.. values.Order()];
        Assert.Equal(Descriptives.Mean(sorted), line.Mean);
        Assert.Equal(Descriptives.StandardDeviation(sorted), line.StandardDeviation);
        Assert.Equal(model.Frame.YAxis.Range.Minimum, line.FromScore);
        Assert.Equal(model.Frame.YAxis.Range.Maximum, line.ToScore);
    }

    // D and E: off, no series has a line, grouped or not.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffNoSeriesHasALine(bool grouped)
    {
        var model = Build(Data(Skewed, grouped ? Text(SkewedGroups) : null), Off);

        Assert.Equal(grouped ? 4 : 1, model.Series.Count);
        Assert.All(model.Series, series => Assert.Null(series.FittedLine));
    }

    // F to K: whether a series can have a line is unchanged; off, none has one.
    [Theory]
    [InlineData(new[] { 42.0 }, false)]
    [InlineData(new[] { 0.1, 0.1, 0.1, 0.1 }, false)]
    [InlineData(new[] { 10.0, 20.0 }, true)]
    [InlineData(new[] { 1.0, 2.0, 2.0, 7.0 }, true)]
    public void ALineNeedsASpreadAndTheOption(double[] values, bool hasSpread)
    {
        var on = Assert.Single(Build(Data(values), ProbabilityPlotOptions.Default).Series);
        var off = Assert.Single(Build(Data(values), Off).Series);

        Assert.Equal(hasSpread, on.FittedLine is not null);
        Assert.Null(off.FittedLine);
    }

    // On and off differ in the fitted lines and the horizontal axis, and in nothing else: labels, order, series
    // indices, counts, points, scores, the vertical axis and the legend are the same.
    [Theory]
    [InlineData(false, DisplaySampling.DefaultMaximumRenderedPoints)]
    [InlineData(true, DisplaySampling.DefaultMaximumRenderedPoints)]
    [InlineData(true, 40)]
    public void OffChangesOnlyTheLinesAndTheHorizontalAxis(bool grouped, int maximumRenderedPoints)
    {
        var data = Data(Skewed, grouped ? Text(SkewedGroups) : null);

        var on = Build(data, ProbabilityPlotOptions.Default, maximumRenderedPoints);
        var off = Build(data, Off, maximumRenderedPoints);

        Assert.Equal(Describe(on, withLinesAndXAxis: false), Describe(off, withLinesAndXAxis: false));
        Assert.Equal(on.Frame.YAxis.Range, off.Frame.YAxis.Range);
        Assert.Equal(on.WasSampled, off.WasSampled);
        Assert.Equal(on.RenderedPointCount, off.RenderedPointCount);
    }

    // Off, the horizontal axis is the one the points alone ask for - no line reaches into it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OffTheHorizontalAxisIsChosenFromThePointsAlone(bool grouped)
    {
        var off = Build(Data(Skewed, grouped ? Text(SkewedGroups) : null), Off);

        var expected = GraphAxisRanges.FromValues(Skewed.Min(), Skewed.Max());
        Assert.Equal(expected, off.Frame.XAxis.Range);
        Assert.Equal(GraphAxisTicks.Nice(expected).Select(tick => tick.Value), off.Frame.XAxis.Ticks.Select(tick => tick.Value));
        Assert.Equal("Reg1", off.Frame.XAxis.Title);
    }

    // A small sample is where the line reaches furthest past the points: on, the axis shows the line's ends; off, it
    // closes in on the points.
    [Fact]
    public void OffASmallSampleIsNotDrawnOnTheLinesWiderAxis()
    {
        double[] values = [10, 12, 14, 16, 18, 20];

        var on = Build(Data(values), ProbabilityPlotOptions.Default);
        var off = Build(Data(values), Off);
        var line = Assert.Single(on.Series).FittedLine!;

        Assert.True(line.FromValue < values.Min() && line.ToValue > values.Max());
        Assert.Equal(GraphAxisRanges.FromValues(line.FromValue, line.ToValue), on.Frame.XAxis.Range);
        Assert.Equal(GraphAxisRanges.FromValues(10, 20), off.Frame.XAxis.Range);
        Assert.True(off.Frame.XAxis.Range.Span < on.Frame.XAxis.Range.Span / 2);
    }

    // Off, the points that are drawn are the same points, so sampling picked the same observations.
    [Fact]
    public void OffSamplesExactlyTheSamePoints()
    {
        var values = Enumerable.Range(0, 1_000).Select(index => Math.Sin(index / 50d) * 10).ToArray();

        var on = Build(Data(values), ProbabilityPlotOptions.Default, maximumRenderedPoints: 100);
        var off = Build(Data(values), Off, maximumRenderedPoints: 100);

        Assert.True(off.WasSampled);
        Assert.Equal(on.Series[0].Points.ToArray(), off.Series[0].Points.ToArray());
        Assert.Equal(1_000, off.Series[0].ObservationCount);
    }

    [Fact]
    public void TheBuilderNeedsOptions() =>
        Assert.Throws<ArgumentNullException>(() => new ProbabilityPlotRenderModelBuilder().Build(Data([1]), Labels, null!, Token));
}
