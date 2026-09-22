using System.Globalization;
using System.Text;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The histogram's own controls (Task #039): what its bars measure (frequency, percent, density) and how its bins are
// chosen (automatically, by number, or on a fixed width-and-start grid). The default is the histogram as it always was.
public class HistogramControlsTests
{
    private static readonly HistogramPlotLabels Labels = new("Reg1", "SITE");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(double[] values, string?[]? groups = null) =>
        new(GraphType.Histogram, Guid.NewGuid(), Column("Reg1"), values,
            groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups));

    private static HistogramRenderModel Build(UnivariateGraphData data, HistogramOptions options) =>
        new HistogramRenderModelBuilder().Build(data, Labels, options, Token)
        ?? throw new InvalidOperationException("The histogram has no data.");

    private static HistogramOptions Scale(HistogramYScale scale) => new(scale);

    private static HistogramOptions Count(int count, HistogramYScale scale = HistogramYScale.Frequency) =>
        new(scale, HistogramBinningMode.Count, BinCount: count);

    private static HistogramOptions Fixed(double width, double start, HistogramYScale scale = HistogramYScale.Frequency) =>
        new(scale, HistogramBinningMode.WidthAndStart, BinWidth: width, BinStart: start);

    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Axis(GraphAxisModel axis) =>
        $"{R(axis.Range.Minimum)}..{R(axis.Range.Maximum)} '{axis.Title}' [{string.Join(",", axis.Ticks.Select(tick => $"{R(tick.Value)}={tick.Label}"))}]";

    // Everything a histogram model says, bit for bit.
    private static string Describe(HistogramRenderModel model)
    {
        var text = new StringBuilder();
        text.Append(model.Frame.Title).Append(" x=").Append(Axis(model.Frame.XAxis)).Append(" y=").Append(Axis(model.Frame.YAxis));
        text.Append(" legend=").Append(model.Frame.Legend is { } legend
            ? legend.Title + ":" + string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}"))
            : "none");
        text.Append($" n={model.SourceObservationCount} max={model.MaximumCount} maxHeight={R(model.MaximumHeight)} scale={model.YScale}");
        text.Append(" bins=").AppendJoin(",", model.Bins.Select(bin => $"{R(bin.LowerEdge)}..{R(bin.UpperEdge)}"));
        foreach (var series in model.Series)
        {
            text.Append($" [{series.Label}#{series.SeriesIndex} n={series.ObservationCount} counts={string.Join(",", series.Counts)} heights={string.Join(",", series.Heights.Select(R))}]");
        }

        return text.ToString();
    }

    private static readonly double[] Wafer = [.. Enumerable.Range(0, 3000).Select(index => 15 + (0.1 * Math.Sin(index * 0.37) * Math.Cos(index * 0.011)) + (index % 17 * 0.001))];

    private static readonly string?[] Sites = [.. Enumerable.Range(0, 3000).Select(index => index % 11 == 0 ? null : $"Site {(index % 4 * 2) + 1}")];

    // ---- The default is the histogram as it always was ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheDefaultOptionsBuildTheLegacyHistogramBitForBit(bool grouped)
    {
        var data = Data(Wafer, grouped ? Sites : null);

        var legacy = new HistogramRenderModelBuilder().Build(data, Labels, Token)!;
        var defaults = Build(data, HistogramOptions.Default);
        var explicitDefault = Build(data, new HistogramOptions(HistogramYScale.Frequency, HistogramBinningMode.Auto));

        Assert.Equal(Describe(legacy), Describe(defaults));
        Assert.Equal(Describe(legacy), Describe(explicitDefault));
        Assert.All(defaults.Series, series => Assert.Equal(series.Counts.Select(count => (double)count), series.Heights));
        Assert.Equal(defaults.MaximumCount, defaults.MaximumHeight);
        Assert.Equal(HistogramRenderModelBuilder.FrequencyAxisTitle, defaults.Frame.YAxis.Title);
        Assert.Equal(GraphAxisTicks.NiceCounts(defaults.MaximumCount).Range, defaults.Frame.YAxis.Range);
    }

    [Fact]
    public void TheModelsOwnFrequencySeriesAreDrawnToTheirCounts()
    {
        var series = new HistogramSeriesRenderModel("A", 0, [3, 0, 5]);

        Assert.Equal([3d, 0d, 5d], series.Heights);
        Assert.Equal(8, series.ObservationCount);
    }

    [Fact]
    public void HeightsMustBeFiniteNonNegativeAndOnePerBin()
    {
        Assert.Throws<ArgumentException>(() => new HistogramSeriesRenderModel("A", 0, [1, 2], [1]));
        Assert.Throws<ArgumentException>(() => new HistogramSeriesRenderModel("A", 0, [1, 2], [1, double.NaN]));
        Assert.Throws<ArgumentException>(() => new HistogramSeriesRenderModel("A", 0, [1, 2], [1, double.PositiveInfinity]));
        Assert.Throws<ArgumentException>(() => new HistogramSeriesRenderModel("A", 0, [1, 2], [1, -0.5]));
    }

    // ---- Y scales ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PercentBarsAreEachSeriesOwnShareAndAddUpToAHundred(bool grouped)
    {
        var data = Data(Wafer, grouped ? Sites : null);
        var frequency = Build(data, HistogramOptions.Default);
        var percent = Build(data, Scale(HistogramYScale.Percent));

        Assert.Equal(HistogramYScale.Percent, percent.YScale);
        Assert.Equal(HistogramRenderModelBuilder.PercentAxisTitle, percent.Frame.YAxis.Title);
        Assert.Equal(Describe(frequency).Split(" bins=")[1].Split(" [")[0], Describe(percent).Split(" bins=")[1].Split(" [")[0]);
        foreach (var series in percent.Series)
        {
            Assert.Equal(frequency.Series[series.SeriesIndex].Counts, series.Counts);
            for (var index = 0; index < series.Counts.Count; index++)
            {
                Assert.Equal(100d * series.Counts[index] / series.ObservationCount, series.Heights[index]);
            }

            Assert.Equal(100, series.Heights.Sum(), 1e-9);
        }

        Assert.True(percent.Frame.YAxis.Range.Minimum == 0 && percent.Frame.YAxis.Range.Maximum >= percent.MaximumHeight);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DensityBarsHaveUnitAreaPerSeries(bool grouped)
    {
        var density = Build(Data(Wafer, grouped ? Sites : null), Scale(HistogramYScale.Density));

        Assert.Equal(HistogramRenderModelBuilder.DensityAxisTitle, density.Frame.YAxis.Title);
        foreach (var series in density.Series)
        {
            var area = 0d;
            for (var index = 0; index < series.Counts.Count; index++)
            {
                var width = density.Bins[index].Width;
                Assert.Equal(series.Counts[index] / (series.ObservationCount * width), series.Heights[index]);
                area += series.Heights[index] * width;
            }

            Assert.Equal(1, area, 1e-9);
        }

        Assert.True(density.Frame.YAxis.Range.Maximum >= density.MaximumHeight);
    }

    [Fact]
    public void GroupsOfDifferentSizesAreComparableInPercent()
    {
        // Ten in one group, a thousand in the other, the same shape: the same percents.
        double[] values = [.. Enumerable.Range(0, 10).Select(index => (double)(index % 5)), .. Enumerable.Range(0, 1000).Select(index => (double)(index % 5))];
        string?[] groups = [.. Enumerable.Repeat<string?>("small", 10), .. Enumerable.Repeat<string?>("large", 1000)];

        var model = Build(Data(values, groups), Scale(HistogramYScale.Percent));

        Assert.Equal(model.Series[0].Heights, model.Series[1].Heights);
        Assert.NotEqual(model.Series[0].Counts, model.Series[1].Counts);
    }

    [Fact]
    public void TheYScaleChangesNeitherBinsNorCountsNorTheHorizontalAxis()
    {
        var data = Data(Wafer, Sites);
        var models = new[] { HistogramYScale.Frequency, HistogramYScale.Percent, HistogramYScale.Density }.Select(scale => Build(data, Scale(scale))).ToArray();

        Assert.All(models, model => Assert.Equal(models[0].Bins, model.Bins));
        Assert.All(models, model => Assert.Equal(models[0].Frame.XAxis.Range, model.Frame.XAxis.Range));
        Assert.All(models, model => Assert.Equal(models[0].Series.Select(series => series.Counts.ToArray()), model.Series.Select(series => series.Counts.ToArray())));
        Assert.All(models, model => Assert.Equal(models[0].Frame.Legend!.Entries, model.Frame.Legend!.Entries));
    }

    [Fact]
    public void PercentAndDensityAxesStartAtZeroOnNiceSteps()
    {
        var percent = Build(Data([1, 2, 2, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7]), Scale(HistogramYScale.Percent));
        var ticks = percent.Frame.YAxis.Ticks.Select(tick => tick.Value).ToArray();

        Assert.Equal(0, ticks[0]);
        Assert.Equal(percent.Frame.YAxis.Range.Maximum, ticks[^1]);
        var step = ticks[1] - ticks[0];
        Assert.All(ticks.Zip(ticks.Skip(1)), pair => Assert.Equal(step, pair.Second - pair.First, 1e-12));
    }

    // ---- Count ----

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(200)]
    public void CountDividesTheDataRangeIntoExactlyThatManyBins(int count)
    {
        var model = Build(Data(Wafer), Count(count));

        Assert.Equal(count, model.Bins.Count);
        Assert.Equal(Wafer.Min(), model.Bins[0].LowerEdge);
        Assert.Equal(Wafer.Max(), model.Bins[^1].UpperEdge);
        Assert.Equal(model.Bins[0].LowerEdge, model.Frame.XAxis.Range.Minimum);
        Assert.Equal(model.Bins[^1].UpperEdge, model.Frame.XAxis.Range.Maximum);
        Assert.Equal(Wafer.Length, model.Series[0].Counts.Sum());
    }

    [Fact]
    public void CountKeepsTheAutomaticBoundaryConventionIncludingTheLargestValue()
    {
        // 0..30 in three bins: 0, 10, 20, 30. 10 and 20 go right; 30 stays in the last bin.
        var model = Build(Data([0, 10, 20, 30]), Count(3));

        Assert.Equal([0d, 10d, 20d, 30d], [model.Bins[0].LowerEdge, .. model.Bins.Select(bin => bin.UpperEdge)]);
        Assert.Equal([1, 1, 2], model.Series[0].Counts);
    }

    [Fact]
    public void CountOnlyReplacesTheStatisticsChoiceOfHowManyBins()
    {
        var automatic = Build(Data(Wafer, Sites), HistogramOptions.Default);
        var counted = Build(Data(Wafer, Sites), Count(automatic.Bins.Count));

        Assert.Equal(Describe(automatic), Describe(counted));
    }

    [Fact]
    public void GroupsShareTheCountedBins()
    {
        var model = Build(Data(Wafer, Sites), Count(12));

        Assert.Equal(12, model.Bins.Count);
        Assert.All(model.Series, series => Assert.Equal(12, series.Counts.Count));
        Assert.Equal(Wafer.Length, model.Series.Sum(series => series.ObservationCount));
    }

    [Theory]
    [InlineData(HistogramBinningMode.Auto)]
    [InlineData(HistogramBinningMode.Count)]
    public void ConstantDataIsOneBinOverItsWindowWhateverTheCount(HistogramBinningMode mode)
    {
        var options = mode == HistogramBinningMode.Count ? Count(30, HistogramYScale.Density) : Scale(HistogramYScale.Density);

        var model = Build(Data([100, 100, 100, 100]), options);

        var bin = Assert.Single(model.Bins);
        Assert.Equal(GraphAxisRanges.FromValues(100, 100).Minimum, bin.LowerEdge);
        Assert.Equal(GraphAxisRanges.FromValues(100, 100).Maximum, bin.UpperEdge);
        Assert.Equal([4], model.Series[0].Counts);
        Assert.Equal(1 / bin.Width, Assert.Single(model.Series[0].Heights));
        Assert.True(double.IsFinite(model.MaximumHeight));
    }

    [Fact]
    public void CountOnATinyRangeKeepsTheSafeFallback()
    {
        // Two values one ulp apart cannot be divided into two hundred distinct bins: one bin, as for automatic bins.
        var tiny = Math.BitIncrement(15000d);
        var model = Build(Data([15000, tiny, 15000, tiny]), Count(200));

        var bin = Assert.Single(model.Bins);
        Assert.Equal(15000, bin.LowerEdge);
        Assert.Equal(tiny, bin.UpperEdge);
        Assert.Equal([4], model.Series[0].Counts);
    }

    // ---- Width and start ----

    [Fact]
    public void EdgesAreStartPlusWholeWidthsReachingJustPastTheData()
    {
        var model = Build(Data([14120, 14250, 14399]), Fixed(100, 14000));

        Assert.Equal([14100d, 14200d, 14300d, 14400d], [model.Bins[0].LowerEdge, .. model.Bins.Select(bin => bin.UpperEdge)]);
        Assert.Equal([1, 1, 1], model.Series[0].Counts);
        Assert.Equal(14100, model.Frame.XAxis.Range.Minimum);
        Assert.Equal(14400, model.Frame.XAxis.Range.Maximum);
    }

    [Fact]
    public void TheGridReachesBothSidesOfStart()
    {
        var model = Build(Data([13850, 14050]), Fixed(100, 14000));

        Assert.Equal([13800d, 13900d, 14000d, 14100d], [model.Bins[0].LowerEdge, .. model.Bins.Select(bin => bin.UpperEdge)]);
        Assert.Equal([1, 0, 1], model.Series[0].Counts);
    }

    [Fact]
    public void AValueOnStartBelongsToTheBinThatBeginsThere()
    {
        var model = Build(Data([14000, 14050]), Fixed(100, 14000));

        var bin = Assert.Single(model.Bins);
        Assert.Equal(14000, bin.LowerEdge);
        Assert.Equal(14100, bin.UpperEdge);
    }

    [Fact]
    public void AValueOnAnInnerEdgeBelongsToTheBinOnItsRight()
    {
        var model = Build(Data([14050, 14100, 14150]), Fixed(100, 14000));

        Assert.Equal([14000d, 14100d, 14200d], [model.Bins[0].LowerEdge, .. model.Bins.Select(bin => bin.UpperEdge)]);
        Assert.Equal([1, 2], model.Series[0].Counts);
    }

    // The fixed grid has no closed last bin: the largest value on an edge gets a bin of its own to the right, so a
    // value's bin never depends on which other values it is drawn with.
    [Fact]
    public void TheLargestValueOnAnEdgeGetsTheBinToItsRight()
    {
        var model = Build(Data([14050, 14200]), Fixed(100, 14000));

        Assert.Equal([14000d, 14100d, 14200d, 14300d], [model.Bins[0].LowerEdge, .. model.Bins.Select(bin => bin.UpperEdge)]);
        Assert.Equal([1, 0, 1], model.Series[0].Counts);
        Assert.True(14200 < model.Bins[^1].UpperEdge);

        // The same value, not the largest in another dataset, is in the same bin.
        var other = Build(Data([14200, 14290]), Fixed(100, 14000));
        Assert.Equal(new HistogramBin(14200, 14300), Assert.Single(other.Bins));
    }

    [Fact]
    public void AStartFarFromTheDataOnlyAddsTheBinsTheDataNeeds()
    {
        var model = Build(Data([1e6 + 3, 1e6 + 7]), Fixed(1, -5e8));

        Assert.Equal(5, model.Bins.Count);
        Assert.Equal(1e6 + 3, model.Bins[0].LowerEdge);
        Assert.Equal(1e6 + 8, model.Bins[^1].UpperEdge);
    }

    [Fact]
    public void EveryEdgeIsComputedFromStartAndItsWholeNumberOfWidths()
    {
        const double Width = 0.1;
        const double Start = -0.35;
        var model = Build(Data([.. Enumerable.Range(0, 200).Select(index => Math.Sin(index) * 2)]), Fixed(Width, Start));

        var first = Math.Round((model.Bins[0].LowerEdge - Start) / Width);
        for (var index = 0; index < model.Bins.Count; index++)
        {
            Assert.Equal(Start + ((first + index) * Width), model.Bins[index].LowerEdge);
            Assert.Equal(Start + ((first + index + 1) * Width), model.Bins[index].UpperEdge);
        }
    }

    [Fact]
    public void TwoDatasetsWithTheSameWidthAndStartShareTheirEdgesExactly()
    {
        var options = Fixed(0.07, 14.93);
        var a = Build(Data([.. Enumerable.Range(0, 400).Select(index => 14.9 + (Math.Sin(index) * 0.3))]), options);
        var b = Build(Data([.. Enumerable.Range(0, 400).Select(index => 15.1 + (Math.Cos(index) * 0.5))]), options);

        var edgesA = new[] { a.Bins[0].LowerEdge }.Concat(a.Bins.Select(bin => bin.UpperEdge)).ToArray();
        var edgesB = new[] { b.Bins[0].LowerEdge }.Concat(b.Bins.Select(bin => bin.UpperEdge)).ToArray();
        var shared = edgesA.Intersect(edgesB).ToArray();

        // Wherever the two grids overlap, they are the very same numbers - and every edge of one inside the other's span
        // is one of the other's edges.
        Assert.True(shared.Length > 5);
        Assert.All(edgesA.Where(edge => edge >= edgesB[0] && edge <= edgesB[^1]), edge => Assert.Contains(edge, edgesB));
        Assert.NotEqual(a.Frame.XAxis.Range, b.Frame.XAxis.Range);
    }

    [Fact]
    public void ConstantDataSitsInItsFixedBinWithDensityOneOverTheWidth()
    {
        var model = Build(Data([14123, 14123, 14123]), Fixed(100, 14000, HistogramYScale.Density));

        var bin = Assert.Single(model.Bins);
        Assert.Equal(new HistogramBin(14100, 14200), bin);
        Assert.Equal(1 / 100d, Assert.Single(model.Series[0].Heights));
    }

    [Fact]
    public void GroupsShareTheFixedGrid()
    {
        var model = Build(Data(Wafer, Sites), Fixed(0.01, 15));

        Assert.All(model.Series, series => Assert.Equal(model.Bins.Count, series.Counts.Count));
        Assert.Equal(Wafer.Length, model.Series.Sum(series => series.ObservationCount));
        Assert.All(Wafer, value => Assert.True(value >= model.Bins[0].LowerEdge && value < model.Bins[^1].UpperEdge));
    }

    [Theory]
    [InlineData(0.001)]
    [InlineData(1e-9)]
    public void AWidthNeedingMoreThanTwoHundredBinsIsRefused(double width)
    {
        var failure = Assert.Throws<GraphPreparationException>(() => Build(Data([10, 11]), Fixed(width, 10)));

        Assert.Equal("The selected bin width requires more than 200 bins. Choose a larger bin width.", failure.Message);
        Assert.Equal(HistogramRenderModelBuilder.TooManyBinsMessage, failure.Message);
    }

    [Fact]
    public void JustTwoHundredBinsAreAllowedAndTwoHundredAndOneAreNot()
    {
        Assert.Equal(200, Build(Data([0.5, 199.5]), Fixed(1, 0)).Bins.Count);
        Assert.Throws<GraphPreparationException>(() => Build(Data([0.5, 200.5]), Fixed(1, 0)));

        // A largest value on the 200th edge needs a 201st bin.
        Assert.Throws<GraphPreparationException>(() => Build(Data([0, 200]), Fixed(1, 0)));
    }

    [Fact]
    public void AWidthTheValuesCannotResolveIsRefused()
    {
        // One value at 15000, whose neighbouring doubles are 1.8e-12 apart: a width of 1e-13 has no distinct edges there.
        var failure = Assert.Throws<GraphPreparationException>(() => Build(Data([15000, 15000]), Fixed(1e-13, 0)));

        Assert.Equal("The selected bin width is too small for this data range. Choose a larger bin width.", failure.Message);
        Assert.Equal(HistogramRenderModelBuilder.WidthTooSmallMessage, failure.Message);
    }

    [Fact]
    public void AStartTooFarForItsWidthIsRefusedAsTooSmall() =>
        Assert.Throws<GraphPreparationException>(() => Build(Data([1, 1]), Fixed(1e-10, 1e10)));

    [Fact]
    public void TheWidthIsNeverChangedToFitTheData()
    {
        var model = Build(Data([.. Enumerable.Range(0, 150).Select(index => index * 1.3)]), Fixed(1.3, 0));

        // Every value is on an edge k x 1.3 (k = 0..149), and each opens the bin that begins there: the largest one
        // included, so 150 bins, the last starting at it.
        Assert.All(model.Bins, bin => Assert.Equal(1.3, bin.Width, 1e-9));
        Assert.Equal(150, model.Bins.Count);
        Assert.Equal(149 * 1.3, model.Bins[^1].LowerEdge);
        Assert.All(model.Series[0].Counts, count => Assert.Equal(1, count));
    }

    // ---- Statistics and specification ----

    [Fact]
    public void NeitherScaleNorBinsChangeTheStatisticsOrTheSpecification()
    {
        var data = Data(Wafer, Sites);
        var specification = new Specification(14.8, 15, 15.2);
        HistogramOptions[] options =
        [
            HistogramOptions.Default,
            Scale(HistogramYScale.Percent),
            Scale(HistogramYScale.Density),
            Count(40, HistogramYScale.Percent),
            Fixed(0.02, 14.9, HistogramYScale.Density)
        ];

        var frames = options.Select(option =>
        {
            var configuration = new GraphConfiguration(GraphType.Histogram, Guid.Empty, []) { HistogramOptions = option, Specification = specification };
            return GraphPresentation.Apply(Build(data, option).Frame, data, configuration, Token);
        }).ToArray();

        Assert.All(frames, frame => Assert.Equal(frames[0].StatisticsPanel!.Rows, frame.StatisticsPanel!.Rows));
        Assert.All(frames, frame => Assert.Equal(frames[0].ReferenceLines, frame.ReferenceLines));
        Assert.Equal(Descriptives.Mean(Wafer.Where((_, index) => Sites[index] == "Site 1").ToArray()),
            frames[4].StatisticsPanel!.Rows.Single(row => row.Label == "Site 1").Mean);
        Assert.All(frames, frame => Assert.Equal(["LSL 14.8", "Target 15", "USL 15.2"], frame.ReferenceLines.Select(line => line.Label)));
    }

    // ---- Guards ----

    [Fact]
    public void TheBuilderRefusesOptionsThatWereNotValidated()
    {
        var builder = new HistogramRenderModelBuilder();

        Assert.Throws<ArgumentNullException>(() => builder.Build(Data([1]), Labels, null!, Token));
        Assert.Throws<ArgumentException>(() => builder.Build(Data([1]), Labels, Count(0), Token));
        Assert.Throws<ArgumentException>(() => builder.Build(Data([1]), Labels, new HistogramOptions(BinningMode: HistogramBinningMode.WidthAndStart), Token));
    }

    [Fact]
    public void TheLimitMatchesTheAutomaticBinCountLimit()
    {
        Assert.Equal(HistogramBinCount.MaximumBinCount, HistogramOptions.MaximumBinCount);
        Assert.Equal(HistogramBinCount.MinimumBinCount, HistogramOptions.MinimumBinCount);
    }
}
