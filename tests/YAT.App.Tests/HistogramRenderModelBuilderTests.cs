using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Turning the observations of one variable into a histogram: series, shared bins, counts, axes. No worksheet and no
// storage here - the graph data is the input.
public class HistogramRenderModelBuilderTests
{
    private static readonly HistogramPlotLabels Labels = new("Reg1", "SITE");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(double[] values, GraphGroupData? group = null) =>
        new(GraphType.Histogram, Guid.NewGuid(), Column("Reg1"), values, group);

    private static StringGroupData Text(params string?[] values) =>
        new(Column("SITE", WorksheetDataType.String), values);

    private static NumericGroupData Numbers(params double?[] values) =>
        new(Column("SITE"), values);

    private static HistogramRenderModel Build(UnivariateGraphData data) =>
        new HistogramRenderModelBuilder().Build(data, Labels, Token)
        ?? throw new InvalidOperationException("The histogram has no data.");

    private static IReadOnlyList<int> CountsOf(HistogramRenderModel model, string label) =>
        model.Series.Single(series => series.Label == label).Counts;

    // Four values over 0..30: Sturges asks for three bins, so the edges are 0, 10, 20, 30.
    private static HistogramRenderModel Cutpoints(params double[] values) => Build(Data(values));

    // 1
    [Fact]
    public void WithoutAGroupColumnThereIsOneSeriesAndNoLegend()
    {
        var model = Build(Data([1, 2, 3, 4, 5, 6, 7, 8]));

        var series = Assert.Single(model.Series);
        Assert.Equal(string.Empty, series.Label);
        Assert.Equal(0, series.SeriesIndex);
        Assert.Equal(8, series.ObservationCount);
        Assert.Null(model.Frame.Legend);
    }

    // 2
    [Fact]
    public void TheBinsAreEqualWidthMonotonicAndFinite()
    {
        var model = Build(Data([.. Enumerable.Range(0, 200).Select(value => (double)value)]));
        var width = model.Bins[0].Width;

        Assert.All(model.Bins, bin =>
        {
            Assert.True(double.IsFinite(bin.LowerEdge) && double.IsFinite(bin.UpperEdge));
            Assert.Equal(width, bin.Width, width * 1e-9);
        });

        Assert.All(model.Bins.Zip(model.Bins.Skip(1)), pair => Assert.Equal(pair.First.UpperEdge, pair.Second.LowerEdge));
        Assert.Equal(0, model.Bins[0].LowerEdge);
        Assert.Equal(199, model.Bins[^1].UpperEdge);
    }

    // 3
    [Fact]
    public void TheEdgesOfTheSpecificationExample()
    {
        var model = Cutpoints(0, 10, 20, 30);

        Assert.Equal([0, 10, 20, 30], model.Bins.Select(bin => bin.LowerEdge).Append(model.Bins[^1].UpperEdge));
    }

    // 4
    [Fact]
    public void AValueOnAnInnerEdgeBelongsToTheBinOnTheRight()
    {
        // 0 -> bin 0, 10 -> bin 1, 20 -> bin 2, 30 -> bin 2 (the last bin keeps its upper edge).
        Assert.Equal([1, 1, 2], Assert.Single(Cutpoints(0, 10, 20, 30).Series).Counts);
    }

    // 5
    [Fact]
    public void AValueJustBelowAnEdgeBelongsToTheBinOnTheLeft()
    {
        // 0 -> bin 0, 9.999 -> bin 0, 19.999 -> bin 1, 30 -> bin 2.
        Assert.Equal([2, 1, 1], Assert.Single(Cutpoints(0, 9.999, 19.999, 30).Series).Counts);
    }

    // 6
    [Fact]
    public void TheEdgesDecideEvenAHairAwayFromThem()
    {
        Assert.Equal([2, 1, 1], Assert.Single(Cutpoints(0, 9.9999999, 10.0000001, 30).Series).Counts);
        Assert.Equal([1, 2, 1], Assert.Single(Cutpoints(0, 10, 19.9999999, 30).Series).Counts);
    }

    // 7
    [Fact]
    public void TheLargestObservationIsAlwaysInTheLastBin()
    {
        // A width that cannot be written down exactly: the last edge is still the largest value itself.
        var model = Build(Data([0, 0.1, 0.2, 0.3, 1d / 3d]));

        Assert.Equal(1d / 3d, model.Bins[^1].UpperEdge);
        Assert.True(model.Series[0].Counts[^1] >= 1);
        Assert.Equal(5, model.Series[0].ObservationCount);
    }

    // 8
    [Fact]
    public void EveryGroupIsCountedIntoTheVerySameBins()
    {
        // Two groups that do not overlap at all: they must still share one set of bins.
        var values = new double[40];
        var groups = new string?[40];
        for (var index = 0; index < 40; index++)
        {
            values[index] = index < 20 ? index : 100 + index;
            groups[index] = index < 20 ? "A" : "B";
        }

        var model = Build(Data(values, Text(groups)));

        Assert.Equal(2, model.Series.Count);
        Assert.All(model.Series, series => Assert.Equal(model.Bins.Count, series.Counts.Count));
        Assert.Equal(0, model.Bins[0].LowerEdge);
        Assert.Equal(139, model.Bins[^1].UpperEdge);

        // Group A lives at the bottom of the axis and B at the top, so their counts cannot be in the same bins.
        var a = CountsOf(model, "A");
        var b = CountsOf(model, "B");
        Assert.Equal(20, a.Sum());
        Assert.Equal(20, b.Sum());
        Assert.Equal(0, a[^1]);
        Assert.Equal(0, b[0]);
    }

    // 9
    [Fact]
    public void AStringGroupBecomesOneSeriesPerValueInFirstObservedOrder()
    {
        var model = Build(Data([1, 2, 3, 4], Text("B", "A", "B", null)));

        Assert.Equal(["B", "A", HistogramRenderModelBuilder.MissingGroupLabel], model.Series.Select(series => series.Label));
        Assert.Equal([0, 1, 2], model.Series.Select(series => series.SeriesIndex));
        Assert.Equal(2, model.Series.Single(series => series.Label == "B").ObservationCount);
        Assert.Equal(1, model.Series.Single(series => series.Label == HistogramRenderModelBuilder.MissingGroupLabel).ObservationCount);
    }

    // 10
    [Fact]
    public void ANumericGroupBecomesOneSeriesPerValue()
    {
        var model = Build(Data([1, 2, 3], Numbers(2, 1.5, null)));

        Assert.Equal(["2", "1.5", HistogramRenderModelBuilder.MissingGroupLabel], model.Series.Select(series => series.Label));
    }

    // 11
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

    // 12
    [Fact]
    public void EveryObservationIsCountedExactlyOnce()
    {
        var values = new double[500];
        var groups = new string?[500];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = Math.Sin(index / 25d) * 40;
            groups[index] = index % 7 == 0 ? null : $"Lot{index % 5}";
        }

        var model = Build(Data(values, Text(groups)));

        Assert.Equal(500, model.SourceObservationCount);
        Assert.Equal(500, model.Series.Sum(series => series.Counts.Sum()));
        Assert.Equal(500, model.Series.Sum(series => series.ObservationCount));
    }

    // 13
    [Fact]
    public void TheFrameIsTitledAfterTheVariableItCounts()
    {
        var model = Build(Data([1, 2, 3]));

        Assert.Equal("Histogram of Reg1", model.Frame.Title);
        Assert.Equal("Reg1", model.Frame.XAxis.Title);
        Assert.Equal(HistogramRenderModelBuilder.FrequencyAxisTitle, model.Frame.YAxis.Title);
    }

    // 14
    [Fact]
    public void TheHorizontalAxisIsTheBinsThemselves()
    {
        var model = Build(Data([.. Enumerable.Range(0, 100).Select(value => value * 0.5)]));

        Assert.Equal(model.Bins[0].LowerEdge, model.Frame.XAxis.Range.Minimum);
        Assert.Equal(model.Bins[^1].UpperEdge, model.Frame.XAxis.Range.Maximum);
        Assert.NotEmpty(model.Frame.XAxis.Ticks);
    }

    // 15
    [Fact]
    public void TheFrequencyAxisStartsAtZeroAndCoversTheTallestBar()
    {
        var model = Build(Data([1, 1, 1, 1, 1, 2, 3]));

        Assert.Equal(0, model.Frame.YAxis.Range.Minimum);
        Assert.True(model.Frame.YAxis.Range.Maximum >= model.MaximumCount);
        Assert.All(model.Frame.YAxis.Ticks, tick => Assert.Equal(Math.Round(tick.Value), tick.Value));
    }

    // 16
    [Fact]
    public void ConstantObservationsBecomeOneBinAroundTheirValue()
    {
        var model = Build(Data([100, 100, 100, 100]));

        var bin = Assert.Single(model.Bins);
        Assert.Equal(GraphAxisRanges.FromValues(100, 100).Minimum, bin.LowerEdge);
        Assert.Equal(GraphAxisRanges.FromValues(100, 100).Maximum, bin.UpperEdge);
        Assert.Equal([4], Assert.Single(model.Series).Counts);
        Assert.True(model.Frame.XAxis.Range.IsValid);
        Assert.True(model.Frame.YAxis.Range.Maximum >= 4);
    }

    // 17
    [Fact]
    public void OneObservationIsStillAHistogram()
    {
        var model = Build(Data([7.5]));

        Assert.Single(model.Bins);
        Assert.Equal([1], Assert.Single(model.Series).Counts);
        Assert.True(model.Frame.XAxis.Range.IsValid);
        Assert.True(model.Frame.YAxis.Range.IsValid);
    }

    // 18
    [Fact]
    public void ANarrowMiddleHalfWithAFarOutlierStaysWithinTheBinLimit()
    {
        var values = new double[1_000];
        for (var index = 0; index < values.Length - 1; index++)
        {
            values[index] = index % 2 == 0 ? 0 : 1;
        }

        values[^1] = 1e9;

        var model = Build(Data(values));

        Assert.InRange(model.Bins.Count, 1, 200);
        Assert.Equal(1_000, model.Series[0].ObservationCount);
    }

    // 19
    [Fact]
    public void NoObservationsMeansNoHistogramAtAll()
    {
        Assert.Null(new HistogramRenderModelBuilder().Build(Data([]), Labels, Token));
        Assert.Null(new HistogramRenderModelBuilder().Build(Data([double.NaN, double.PositiveInfinity]), Labels, Token));
    }

    // 20
    [Fact]
    public void ObservationsThatCannotBeCountedAreLeftOut()
    {
        var model = Build(Data([1, double.NaN, 3, double.NegativeInfinity, 5]));

        Assert.Equal(3, model.SourceObservationCount);
        Assert.Equal(1, model.Bins[0].LowerEdge);
        Assert.Equal(5, model.Bins[^1].UpperEdge);
    }

    // 21
    [Fact]
    public void PreparationCanBeCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new HistogramRenderModelBuilder().Build(Data([1, 2, 3]), Labels, cancellation.Token));
    }

    // 22
    [Fact]
    public void TheSameObservationsAlwaysGiveTheSameHistogram()
    {
        var data = Data([.. Enumerable.Range(0, 120).Select(value => Math.Sqrt(value))], Text([.. Enumerable.Range(0, 120).Select(value => (string?)$"S{value % 3}")]));

        var first = Build(data);
        var second = Build(data);

        Assert.Equal(first.Bins.Select(bin => bin.LowerEdge), second.Bins.Select(bin => bin.LowerEdge));
        Assert.Equal(first.Series.Select(series => series.Label), second.Series.Select(series => series.Label));
        Assert.Equal(first.Series.SelectMany(series => series.Counts), second.Series.SelectMany(series => series.Counts));
    }

    // 23
    [Fact]
    public void TheBuilderNeedsDataAndLabels()
    {
        var builder = new HistogramRenderModelBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.Build(null!, Labels, Token));
        Assert.Throws<ArgumentNullException>(() => builder.Build(Data([1]), null!, Token));
    }
}
