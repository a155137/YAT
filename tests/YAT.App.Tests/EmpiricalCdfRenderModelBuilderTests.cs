using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Turning the observations of one variable into empirical cumulative distributions: series, steps, percentages and
// axes. No worksheet and no storage here - the graph data is the input.
public class EmpiricalCdfRenderModelBuilderTests
{
    private static readonly EmpiricalCdfLabels Labels = new("Reg1", "SITE");

    private const double Tolerance = 1e-9;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(double[] values, GraphGroupData? group = null) =>
        new(GraphType.EmpiricalCdf, Guid.NewGuid(), Column("Reg1"), values, group);

    private static StringGroupData Text(params string?[] values) =>
        new(Column("SITE", WorksheetDataType.String), values);

    private static NumericGroupData Numbers(params double?[] values) =>
        new(Column("SITE"), values);

    private static EmpiricalCdfRenderModel Build(
        UnivariateGraphData data,
        int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints) =>
        new EmpiricalCdfRenderModelBuilder(maximumRenderedPoints).Build(data, Labels, Token)
        ?? throw new InvalidOperationException("The empirical CDF has no data.");

    private static EmpiricalCdfSeriesRenderModel SeriesOf(EmpiricalCdfRenderModel model, string label) =>
        model.Series.Single(series => series.Label == label);

    // 1
    [Fact]
    public void TheExampleFromTheSpecification()
    {
        // 1, 1, 1, 2, 3 -> one step at each distinct value, at the share of the sample it reaches.
        var model = Build(Data([1, 1, 1, 2, 3]));
        var points = Assert.Single(model.Series).Points.ToArray();

        Assert.Equal(3, points.Length);
        Assert.Equal(1, points[0].Value);
        Assert.Equal(60, points[0].CumulativePercent, Tolerance);
        Assert.Equal(2, points[1].Value);
        Assert.Equal(80, points[1].CumulativePercent, Tolerance);
        Assert.Equal(3, points[2].Value);
        Assert.Equal(100, points[2].CumulativePercent, Tolerance);
    }

    // 2
    [Fact]
    public void RepeatedValuesAreOneStepButStillCountAsObservations()
    {
        var model = Build(Data([1, 1, 1, 2, 3]));
        var series = Assert.Single(model.Series);

        Assert.Equal(5, series.ObservationCount);
        Assert.Equal(3, series.UniquePointCount);
        Assert.Equal(5, model.SourceObservationCount);
        Assert.False(model.WasSampled);
    }

    // 3
    [Fact]
    public void EveryStepIsTheShareOfTheSampleAtOrBelowItsValue()
    {
        double[] values = [5, 1, 4, 2, 3, 2, 5, 5, 1, 2];
        var points = Assert.Single(Build(Data(values)).Series).Points.ToArray();

        foreach (var point in points)
        {
            var atOrBelow = values.Count(value => value <= point.Value);
            Assert.Equal(atOrBelow * 100d / values.Length, point.CumulativePercent, Tolerance);
        }

        Assert.Equal([1, 2, 3, 4, 5], points.Select(point => point.Value));
        Assert.Equal([20, 50, 60, 70, 100], points.Select(point => point.CumulativePercent));
    }

    // 4
    [Fact]
    public void WithoutAGroupColumnThereIsOneSeriesAndNoLegend()
    {
        var model = Build(Data([3, 1, 2]));

        var series = Assert.Single(model.Series);
        Assert.Equal(string.Empty, series.Label);
        Assert.Equal(0, series.SeriesIndex);
        Assert.Null(model.Frame.Legend);
    }

    // 5
    [Fact]
    public void EveryGroupCountsAgainstItsOwnSize()
    {
        // Three observations of "A" and seven of "B": both reach a hundred percent at their own largest value.
        var values = new double[10];
        var groups = new string?[10];
        for (var index = 0; index < 10; index++)
        {
            values[index] = index;
            groups[index] = index < 3 ? "A" : "B";
        }

        var model = Build(Data(values, Text(groups)));
        var a = SeriesOf(model, "A");
        var b = SeriesOf(model, "B");

        Assert.Equal(3, a.ObservationCount);
        Assert.Equal(7, b.ObservationCount);
        Assert.Equal([100d / 3, 200d / 3, 100], a.Points.ToArray().Select(point => point.CumulativePercent), new PercentComparer());
        Assert.Equal(100, b.Points.Span[^1].CumulativePercent, Tolerance);
        Assert.Equal(100d / 7, b.Points.Span[0].CumulativePercent, Tolerance);
    }

    // 6
    [Fact]
    public void AStringGroupBecomesOneSeriesPerValueInFirstObservedOrder()
    {
        var model = Build(Data([1, 2, 3, 4], Text("B", "A", "B", null)));

        Assert.Equal(["B", "A", EmpiricalCdfRenderModelBuilder.MissingGroupLabel], model.Series.Select(series => series.Label));
        Assert.Equal([0, 1, 2], model.Series.Select(series => series.SeriesIndex));
        Assert.Equal([4], SeriesOf(model, EmpiricalCdfRenderModelBuilder.MissingGroupLabel).Points.ToArray().Select(point => point.Value));
    }

    // 7
    [Fact]
    public void ANumericGroupBecomesOneSeriesPerValue()
    {
        var model = Build(Data([1, 2, 3], Numbers(2, 1.5, 2)));

        Assert.Equal(["2", "1.5"], model.Series.Select(series => series.Label));
        Assert.Equal([1, 3], SeriesOf(model, "2").Points.ToArray().Select(point => point.Value));
    }

    // 8
    [Fact]
    public void EverySeriesEndsAtAHundredPercent()
    {
        var values = new double[31];
        var groups = new string?[31];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = index % 7;
            groups[index] = $"G{index % 3}";
        }

        var model = Build(Data(values, Text(groups)));

        Assert.All(model.Series, series => Assert.Equal(100, series.Points.Span[^1].CumulativePercent, Tolerance));
    }

    // 9
    [Fact]
    public void TheStepsRiseWithTheirValues()
    {
        var model = Build(Data([.. Enumerable.Range(0, 200).Select(index => Math.Sin(index / 20d))]));
        var points = Assert.Single(model.Series).Points.ToArray();

        Assert.Equal(points.Select(point => point.Value).Order(), points.Select(point => point.Value));
        Assert.Equal(points.Select(point => point.CumulativePercent).Order(), points.Select(point => point.CumulativePercent));
    }

    // 10
    [Fact]
    public void OneObservationIsOneStepAtAHundredPercent()
    {
        var model = Build(Data([7.5]));
        var point = Assert.Single(Assert.Single(model.Series).Points.ToArray());

        Assert.Equal(7.5, point.Value);
        Assert.Equal(100, point.CumulativePercent);
        Assert.True(model.Frame.XAxis.Range.IsValid);
    }

    // 11
    [Fact]
    public void ObservationsWithNoSpreadAreOneStepAtAHundredPercent()
    {
        var model = Build(Data([100, 100, 100, 100]));
        var series = Assert.Single(model.Series);

        Assert.Equal([new EmpiricalCdfPoint(100, 100)], series.Points.ToArray());
        Assert.Equal(4, series.ObservationCount);
        Assert.Equal(GraphAxisRanges.FromValues(100, 100), model.Frame.XAxis.Range);
    }

    // 12
    [Fact]
    public void TheFrameIsTitledAfterTheVariableItDescribes()
    {
        var model = Build(Data([1, 2, 3]));

        Assert.Equal("Empirical CDF of Reg1", model.Frame.Title);
        Assert.Equal("Reg1", model.Frame.XAxis.Title);
        Assert.Equal(PercentAxis.Title, model.Frame.YAxis.Title);
    }

    // 13
    [Fact]
    public void TheVerticalAxisIsThePlainPercentageOne()
    {
        var model = Build(Data([1, 2, 3]));

        Assert.Equal(PercentAxis.Axis().Range, model.Frame.YAxis.Range);
        Assert.Equal([0, 20, 40, 60, 80, 100], model.Frame.YAxis.Ticks.Select(tick => tick.Value));
    }

    // 14
    [Fact]
    public void EveryGroupIsReadAgainstTheSameValues()
    {
        var values = new double[20];
        var groups = new string?[20];
        for (var index = 0; index < 20; index++)
        {
            values[index] = index < 10 ? index : 100 + index;
            groups[index] = index < 10 ? "A" : "B";
        }

        var model = Build(Data(values, Text(groups)));

        // One X axis over every group's values, not one per group.
        Assert.Equal(GraphAxisRanges.FromValues(0, 119), model.Frame.XAxis.Range);
        Assert.NotEmpty(model.Frame.XAxis.Ticks);
    }

    // 15
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

    // 16
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
        Assert.Equal(100, Assert.Single(model.Series).Points.Span[^1].CumulativePercent, Tolerance);
    }

    // 17
    [Fact]
    public void NoObservationsMeansNoDistributionAtAll()
    {
        Assert.Null(new EmpiricalCdfRenderModelBuilder().Build(Data([]), Labels, Token));
        Assert.Null(new EmpiricalCdfRenderModelBuilder().Build(Data([double.NaN, double.PositiveInfinity]), Labels, Token));
    }

    // 18
    [Fact]
    public void ObservationsThatCannotBePlottedAreLeftOut()
    {
        var model = Build(Data([1, double.NaN, 3, double.NegativeInfinity, 5]));

        Assert.Equal(3, model.SourceObservationCount);
        Assert.Equal([1, 3, 5], Assert.Single(model.Series).Points.ToArray().Select(point => point.Value));
    }

    // 19
    [Fact]
    public void TheDrawingIsCappedWithoutChangingTheDistribution()
    {
        var values = Enumerable.Range(0, 1_000).Select(index => (double)index).ToArray();

        var full = Build(Data(values));
        var capped = Build(Data(values), maximumRenderedPoints: 100);

        Assert.Equal(1_000, capped.SourceObservationCount);
        Assert.Equal(1_000, capped.UniquePointCount);
        Assert.Equal(100, capped.RenderedPointCount);
        Assert.True(capped.WasSampled);
        Assert.False(full.WasSampled);
        Assert.Equal(full.Frame.XAxis.Range, capped.Frame.XAxis.Range);
        Assert.Equal(1_000, capped.Series[0].ObservationCount);

        var points = capped.Series[0].Points.ToArray();
        Assert.Equal(full.Series[0].Points.Span[0], points[0]);
        Assert.Equal(full.Series[0].Points.Span[^1], points[^1]);
        Assert.Equal(100, points[^1].CumulativePercent, Tolerance);
        Assert.Equal(points.Select(point => point.Value).Order(), points.Select(point => point.Value));
        Assert.Equal(points.Select(point => point.CumulativePercent).Order(), points.Select(point => point.CumulativePercent));
    }

    // 20
    [Fact]
    public void EveryGroupKeepsStepsWhileTheBudgetAllows()
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
        Assert.All(capped.Series, series =>
        {
            Assert.True(series.Points.Length >= 1);
            Assert.Equal(100, series.Points.Span[^1].CumulativePercent, Tolerance);
        });
    }

    // 21
    [Fact]
    public void TheDistributionIsTheSameEveryTime()
    {
        var data = Data([.. Enumerable.Range(0, 120).Select(value => (double)(value % 37))], Text([.. Enumerable.Range(0, 120).Select(value => (string?)$"S{value % 3}")]));

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
            new EmpiricalCdfRenderModelBuilder().Build(Data([1, 2, 3]), Labels, cancellation.Token));
    }

    // 23
    [Fact]
    public void TheBuilderNeedsDataAndLabels()
    {
        var builder = new EmpiricalCdfRenderModelBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!, Labels, Token));
        Assert.Throws<ArgumentNullException>(() => builder.Build(Data([1]), null!, Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmpiricalCdfRenderModelBuilder(0));
    }

    // ---- Hotfix #034.2A: a sampled distribution still ends at 100 % ----

    // A budget of one step per series: the one step kept is the last, because a distribution ends at 100 %.
    [Fact]
    public void ASeriesDrawingOneStepKeepsItsFinalStep()
    {
        var model = Build(Data([10, 20]), maximumRenderedPoints: 1);

        var point = Assert.Single(Assert.Single(model.Series).Points.ToArray());
        Assert.Equal(20, point.Value);
        Assert.Equal(100, point.CumulativePercent);
        Assert.Equal(1, model.RenderedPointCount);
    }

    // With a few steps to spend, the sample still starts at the first step and ends at the final one.
    [Fact]
    public void ASampledSeriesOfSeveralStepsEndsAt100Percent()
    {
        var model = Build(Data([.. Enumerable.Range(1, 10).Select(value => (double)value)]), maximumRenderedPoints: 3);

        var points = Assert.Single(model.Series).Points.ToArray();
        Assert.Equal(3, points.Length);
        Assert.Equal(1, points[0].Value);
        Assert.Equal(10, points[^1].Value);
        Assert.Equal(100, points[^1].CumulativePercent);
    }

    // Two groups over a small budget: the small group's share is a single step, and that step is its 100 %.
    [Fact]
    public void ASmallGroupGivenOneStepStillEndsAt100Percent()
    {
        double[] values = [.. Enumerable.Range(0, 50).Select(value => (double)value), 10, 20];
        string?[] groups = [.. Enumerable.Repeat("A", 50), "B", "B"];

        var model = Build(Data(values, Text(groups)), maximumRenderedPoints: 10);

        var small = SeriesOf(model, "B").Points.ToArray();
        Assert.Equal([(20d, 100d)], small.Select(point => (point.Value, point.CumulativePercent)));
        Assert.Equal(100, SeriesOf(model, "A").Points.Span[^1].CumulativePercent);
        Assert.True(model.RenderedPointCount <= 10);
    }

    // The case the robustness harness found, at the production display budget: a small group next to a group with
    // more distinct values than the budget. The small group used to draw one step at 50 % and stop there.
    [Fact]
    public void ASmallGroupBesideAMillionDistinctValuesEndsAt100PercentAtTheDefaultBudget()
    {
        const int Large = 1_000_000;
        var values = new double[Large + 2];
        var groups = new string?[Large + 2];
        for (var index = 0; index < Large; index++)
        {
            values[index] = index;
            groups[index] = "A";
        }

        (values[Large], groups[Large]) = (10, "B");
        (values[Large + 1], groups[Large + 1]) = (20, "B");

        var model = Build(Data(values, Text(groups)));

        var small = SeriesOf(model, "B");
        Assert.Equal(2, small.UniquePointCount);
        Assert.Equal(100, small.Points.Span[^1].CumulativePercent);
        Assert.Equal(100, SeriesOf(model, "A").Points.Span[^1].CumulativePercent);
        Assert.True(model.RenderedPointCount <= DisplaySampling.DefaultMaximumRenderedPoints);
    }

    // A distribution the budget does not touch is drawn step for step, exactly as before.
    [Fact]
    public void AnUnsampledDistributionIsUnchanged()
    {
        var model = Build(Data([2, 1, 2, 3]));

        var points = Assert.Single(model.Series).Points.ToArray();
        Assert.Equal([(1d, 25d), (2d, 75d), (3d, 100d)], points.Select(point => (point.Value, point.CumulativePercent)));
        Assert.False(model.WasSampled);
    }

    private sealed class PercentComparer : IEqualityComparer<double>
    {
        public bool Equals(double first, double second) => Math.Abs(first - second) < Tolerance;

        public int GetHashCode(double value) => 0;
    }
}
