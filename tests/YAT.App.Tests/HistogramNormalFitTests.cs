using System.Globalization;
using SkiaSharp;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The histogram's normal fit (Task #042): the curve of each series' own mean and sample standard deviation on the bars'
// Y scale, prepared by the builder and only drawn by the renderer. With the fit off the histogram is exactly what it
// was; with it on, only the curves - and the Y axis, where a curve rises above every bar - are added.
public class HistogramNormalFitTests
{
    private static readonly HistogramPlotLabels Labels = new("Reg1", "Lot");

    private static readonly double SquareRootOfTwoPi = Math.Sqrt(2 * Math.PI);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(double[] values, string?[]? groups = null, string name = "Reg1") =>
        new(GraphType.Histogram, Guid.NewGuid(), Column(name), values,
            groups is null ? null : new StringGroupData(Column("Lot", WorksheetDataType.String), groups));

    private static HistogramRenderModel Build(UnivariateGraphData data, HistogramOptions options) =>
        new HistogramRenderModelBuilder().Build(data, Labels, options, Token)!;

    private static HistogramOptions Options(HistogramYScale scale, HistogramBinningMode mode, bool fit) => mode switch
    {
        HistogramBinningMode.Count => new HistogramOptions(scale, mode, BinCount: 23, ShowNormalFit: fit),
        HistogramBinningMode.WidthAndStart => new HistogramOptions(scale, mode, BinWidth: 0.02, BinStart: 14.8, ShowNormalFit: fit),
        _ => new HistogramOptions(scale, ShowNormalFit: fit)
    };

    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Axis(GraphAxisModel axis) =>
        $"{axis.Title} {R(axis.Range.Minimum)}..{R(axis.Range.Maximum)} [{string.Join(",", axis.Ticks.Select(tick => $"{R(tick.Value)}={tick.Label}"))}]";

    // Everything but the fits and the Y axis: what a normal fit must never change.
    private static string WithoutFits(HistogramRenderModel model) =>
        $"{model.Frame.Title}|x={Axis(model.Frame.XAxis)}|ytitle={model.Frame.YAxis.Title}|scale={model.YScale}" +
        $"|n={model.SourceObservationCount}|max={model.MaximumCount}/{R(model.MaximumHeight)}" +
        $"|bins={string.Join(",", model.Bins.Select(bin => $"{R(bin.LowerEdge)}~{R(bin.UpperEdge)}"))}" +
        $"|series={string.Join(";", model.Series.Select(series => $"{series.Label}#{series.SeriesIndex} n={series.ObservationCount} " +
            $"{string.Join(",", series.Counts)} {string.Join(",", series.Heights.Select(R))}"))}" +
        $"|legend={(model.Frame.Legend is { } legend ? legend.Title + ":" + string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}")) : "none")}";

    // Normal scores of evenly spread probabilities: a sample shaped exactly like a normal distribution.
    private static double[] Normal(int count, double mean, double standardDeviation) =>
        [.. Enumerable.Range(0, count).Select(index =>
            mean + (standardDeviation * NormalDistribution.InverseCdf((index + 0.5) / count)))];

    private static readonly double[] Wafer =
        [.. Enumerable.Range(0, 3000).Select(index => 15 + (0.1 * Math.Sin(index * 0.37) * Math.Cos(index * 0.011)) + (index % 17 * 0.001))];

    private static readonly string?[] Lots =
        [.. Enumerable.Range(0, 3000).Select(index => index % 13 == 0 ? null : $"Lot {index % 3}")];

    private static double Factor(HistogramYScale scale, int count, double width) => scale switch
    {
        HistogramYScale.Percent => 100 * width,
        HistogramYScale.Density => 1,
        _ => count * width
    };

    // ---- The fit ----

    [Theory]
    [InlineData(HistogramYScale.Frequency)]
    [InlineData(HistogramYScale.Percent)]
    [InlineData(HistogramYScale.Density)]
    public void TheCurveIsTheNormalDensityOfTheSeriesOnTheBarsScale(HistogramYScale scale)
    {
        var values = Normal(500, 15, 0.1);
        const double Width = 0.025;

        var fit = HistogramNormalFit.Fit(values, scale, Width)!;

        Assert.Equal(Descriptives.Mean(values), fit.Mean);
        Assert.Equal(Descriptives.StandardDeviation(values), fit.StandardDeviation);
        Assert.Equal(HistogramNormalFit.PointCount, fit.Points.Count);

        var factor = Factor(scale, values.Length, Width);
        foreach (var point in fit.Points)
        {
            var z = (point.X - fit.Mean) / fit.StandardDeviation;
            var expected = factor * NormalDistribution.Pdf(z) / fit.StandardDeviation;
            Assert.Equal(expected, point.Height, expected * 1e-9);
        }

        // The peak is sampled at the mean itself, and nothing is taller.
        Assert.Equal(fit.Mean, fit.Points[HistogramNormalFit.PointCount / 2].X);
        Assert.Equal(factor / (fit.StandardDeviation * SquareRootOfTwoPi), fit.MaximumHeight, fit.MaximumHeight * 1e-12);
        Assert.Equal(fit.Points[HistogramNormalFit.PointCount / 2].Height, fit.MaximumHeight);
    }

    [Fact]
    public void TheCurveRunsOverFourStandardDeviationsEitherSideInStrictlyIncreasingFiniteSteps()
    {
        var fit = HistogramNormalFit.Fit(Normal(200, -3, 2.5), HistogramYScale.Frequency, 0.5)!;

        Assert.Equal(fit.Mean - (4 * fit.StandardDeviation), fit.Points[0].X, 1e-12);
        Assert.Equal(fit.Mean + (4 * fit.StandardDeviation), fit.Points[^1].X, 1e-12);
        for (var index = 1; index < fit.Points.Count; index++)
        {
            Assert.True(fit.Points[index].X > fit.Points[index - 1].X);
            Assert.True(double.IsFinite(fit.Points[index].Height) && fit.Points[index].Height >= 0);
        }

        // Symmetric about the mean, point for point.
        for (var offset = 1; offset <= HistogramNormalFit.PointCount / 2; offset++)
        {
            Assert.Equal(fit.Points[100 - offset].Height, fit.Points[100 + offset].Height);
        }
    }

    // The area under the curve is the area of the bars it describes: N x width, 100 x width, or 1.
    [Theory]
    [InlineData(HistogramYScale.Frequency)]
    [InlineData(HistogramYScale.Percent)]
    [InlineData(HistogramYScale.Density)]
    public void TheCurveHasTheAreaOfItsBars(HistogramYScale scale)
    {
        var values = Normal(1000, 50, 4);
        const double Width = 1.5;
        var fit = HistogramNormalFit.Fit(values, scale, Width)!;

        var area = 0d;
        for (var index = 1; index < fit.Points.Count; index++)
        {
            area += (fit.Points[index].Height + fit.Points[index - 1].Height) * (fit.Points[index].X - fit.Points[index - 1].X) / 2;
        }

        // Within four standard deviations lies 99.994 % of a normal distribution.
        var expected = Factor(scale, values.Length, Width) * 0.99993666;
        Assert.Equal(expected, area, expected * 1e-4);
    }

    // The sampling depends on the fit alone: the same values give the same points, whatever they are drawn on.
    [Fact]
    public void TheSameValuesAlwaysGiveTheSamePoints() =>
        Assert.Equal(
            HistogramNormalFit.Fit(Wafer, HistogramYScale.Percent, 0.01)!.Points,
            HistogramNormalFit.Fit(Wafer, HistogramYScale.Percent, 0.01)!.Points);

    public static TheoryData<string, double[]> Unfittable => new()
    {
        { "no values", [] },
        { "N = 1", [15.2] },
        { "constant", [3.25, 3.25, 3.25, 3.25] },
        { "constant 0.1", [0.1, 0.1, 0.1] },
        // The squared deviations overflow: no finite standard deviation.
        { "non-finite standard deviation", [1e308, -1e308, 1e308] },
        // The mean overflows.
        { "non-finite mean", [1.7e308, 1.7e308, 1.6e308] },
        // A spread below the resolution of the mean: the sampled X values cannot be told apart.
        { "spread below the mean's resolution", [1e16, 1e16 + 2, 1e16, 1e16 + 2] },
        // A spread so small the peak overflows a double.
        { "peak overflow", [0, 1e-310, 0, 1e-310] }
    };

    [Theory]
    [MemberData(nameof(Unfittable))]
    public void ValuesWithoutADrawableFitHaveNone(string reason, double[] values)
    {
        foreach (var scale in new[] { HistogramYScale.Frequency, HistogramYScale.Percent, HistogramYScale.Density })
        {
            Assert.True(HistogramNormalFit.Fit(values, scale, 1) is null, $"{reason} on {scale}");
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ABinWidthThatCannotScaleTheCurveLeavesNoFitButDensityNeedsNone(double width)
    {
        var values = Normal(100, 0, 1);

        Assert.Null(HistogramNormalFit.Fit(values, HistogramYScale.Frequency, width));
        Assert.Null(HistogramNormalFit.Fit(values, HistogramYScale.Percent, width));
        Assert.NotNull(HistogramNormalFit.Fit(values, HistogramYScale.Density, width));
    }

    [Fact]
    public void ATinyButResolvableSpreadHasATallFiniteFit()
    {
        double[] values = [.. Enumerable.Range(0, 100).Select(index => 15 + (index % 5 * 1e-9))];

        var fit = HistogramNormalFit.Fit(values, HistogramYScale.Density, 1)!;

        Assert.InRange(fit.StandardDeviation, 1e-9, 2e-9);
        Assert.True(double.IsFinite(fit.MaximumHeight) && fit.MaximumHeight > 1e8);
    }

    [Fact]
    public void DataFarFromZeroKeepsItsShape()
    {
        double[] values = [.. Normal(2000, 0, 0.5).Select(value => 1e9 + value)];

        var fit = HistogramNormalFit.Fit(values, HistogramYScale.Frequency, 0.1)!;

        Assert.Equal(1e9, fit.Mean, 1e-6);
        Assert.Equal(0.5, fit.StandardDeviation, 1e-3);
        Assert.Equal(2000 * 0.1 / (fit.StandardDeviation * SquareRootOfTwoPi), fit.MaximumHeight, 1e-9);
        for (var offset = 1; offset <= 100; offset++)
        {
            Assert.True(fit.Points[100 + offset].X > fit.Points[99 + offset].X);
            Assert.Equal(fit.Points[100 - offset].Height, fit.Points[100 + offset].Height);
        }
    }

    [Fact]
    public void AFitIsOnlyMadeOfFiniteIncreasingNonNegativePoints()
    {
        HistogramCurvePoint[] line = [new(0, 1), new(1, 2)];

        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(double.NaN, 1, line));
        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(0, 0, line));
        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(0, double.PositiveInfinity, line));
        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(0, 1, [new(0, 1)]));
        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(0, 1, [new(0, 1), new(0, 2)]));
        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(0, 1, [new(1, 1), new(0, 2)]));
        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(0, 1, [new(0, -1), new(1, 2)]));
        Assert.Throws<ArgumentException>(() => new HistogramNormalFit(0, 1, [new(0, double.NaN), new(1, 2)]));
        Assert.Throws<ArgumentNullException>(() => new HistogramNormalFit(0, 1, null!));
        Assert.Equal(2, new HistogramNormalFit(0, 1, line).MaximumHeight);
    }

    // ---- The histogram with its fits ----

    public static TheoryData<HistogramYScale, HistogramBinningMode, bool> ScalesBinsAndGroups()
    {
        var data = new TheoryData<HistogramYScale, HistogramBinningMode, bool>();
        foreach (var scale in new[] { HistogramYScale.Frequency, HistogramYScale.Percent, HistogramYScale.Density })
        {
            foreach (var mode in new[] { HistogramBinningMode.Auto, HistogramBinningMode.Count, HistogramBinningMode.WidthAndStart })
            {
                data.Add(scale, mode, false);
                data.Add(scale, mode, true);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ScalesBinsAndGroups))]
    public void TheFitChangesNothingButItsCurvesAndAYAxisItRisesAbove(HistogramYScale scale, HistogramBinningMode mode, bool grouped)
    {
        var data = Data(Wafer, grouped ? Lots : null);
        var off = Build(data, Options(scale, mode, fit: false));
        var on = Build(data, Options(scale, mode, fit: true));

        Assert.All(off.Series, series => Assert.Null(series.NormalFit));
        Assert.All(on.Series, series => Assert.NotNull(series.NormalFit));
        Assert.Equal(WithoutFits(off), WithoutFits(on));

        // The Y axis reaches the tallest bar and every curve, and is the axis without fits when no curve rises above
        // the bars.
        var highestFit = on.Series.Max(series => series.NormalFit!.MaximumHeight);
        Assert.True(on.Frame.YAxis.Range.Maximum >= Math.Max(on.MaximumHeight, highestFit));
        if (highestFit <= off.MaximumHeight)
        {
            Assert.Equal(Axis(off.Frame.YAxis), Axis(on.Frame.YAxis));
        }

        // Every series' fit is its own: its N, the bins' width, and the mean and standard deviation the statistics
        // panel shows for it, to the last bit.
        var panel = GraphStatisticsPanelBuilder.Build(data, Token)!;
        var width = on.Bins[0].Width;
        for (var index = 0; index < on.Series.Count; index++)
        {
            var series = on.Series[index];
            var fit = series.NormalFit!;
            Assert.Equal(panel.Rows[index].Label, grouped ? series.Label : string.Empty);
            Assert.Equal(panel.Rows[index].Mean, fit.Mean);
            Assert.Equal(panel.Rows[index].StandardDeviation, fit.StandardDeviation);
            var peak = Factor(scale, series.ObservationCount, width) / (fit.StandardDeviation * SquareRootOfTwoPi);
            Assert.Equal(peak, fit.MaximumHeight, peak * 1e-6);
        }
    }

    // A flat distribution: its normal curve peaks about 1.38 times above its bars, so the axis is stretched to it.
    [Theory]
    [InlineData(HistogramYScale.Frequency)]
    [InlineData(HistogramYScale.Percent)]
    [InlineData(HistogramYScale.Density)]
    public void TheYAxisReachesACurveThatRisesAboveEveryBar(HistogramYScale scale)
    {
        var data = Data([.. Enumerable.Range(0, 1000).Select(index => index / 10.0)]);
        var off = Build(data, new HistogramOptions(scale, HistogramBinningMode.Count, BinCount: 10));
        var on = Build(data, new HistogramOptions(scale, HistogramBinningMode.Count, BinCount: 10, ShowNormalFit: true));

        var peak = on.Series[0].NormalFit!.MaximumHeight;
        Assert.InRange(peak / on.MaximumHeight, 1.35, 1.4);
        Assert.True(off.Frame.YAxis.Range.Maximum < peak);
        Assert.True(on.Frame.YAxis.Range.Maximum >= peak);
        Assert.Equal(WithoutFits(off), WithoutFits(on));

        // The axis is still the nice axis of its scale, reaching this height: whole counts on the frequency axis.
        var expected = scale == HistogramYScale.Frequency
            ? Axis(new GraphAxisModel(GraphAxisTicks.NiceCounts((int)Math.Ceiling(peak)).Range, GraphAxisTicks.NiceCounts((int)Math.Ceiling(peak)).Ticks, "Frequency"))
            : Axis(new GraphAxisModel(GraphAxisTicks.NiceFromZero(peak).Range, GraphAxisTicks.NiceFromZero(peak).Ticks, on.Frame.YAxis.Title));
        Assert.Equal(expected, Axis(on.Frame.YAxis));
        if (scale == HistogramYScale.Frequency)
        {
            Assert.All(on.Frame.YAxis.Ticks, tick => Assert.Equal(Math.Round(tick.Value), tick.Value));
        }
    }

    // A peaked distribution: its bars rise above its normal curve, and the axis stays exactly as without the fit.
    [Theory]
    [InlineData(HistogramYScale.Frequency)]
    [InlineData(HistogramYScale.Percent)]
    [InlineData(HistogramYScale.Density)]
    public void ACurveBelowTheBarsLeavesTheYAxisAsItWas(HistogramYScale scale)
    {
        double[] values = [.. Enumerable.Range(0, 900).Select(index => index % 3 * 0.01), .. Enumerable.Range(0, 100).Select(index => -5 + (index / 10.0))];
        var data = Data(values);
        var off = Build(data, new HistogramOptions(scale));
        var on = Build(data, new HistogramOptions(scale, ShowNormalFit: true));

        Assert.True(on.Series[0].NormalFit!.MaximumHeight < on.MaximumHeight);
        Assert.Equal(Axis(off.Frame.YAxis), Axis(on.Frame.YAxis));
        Assert.Equal(WithoutFits(off), WithoutFits(on));
    }

    // Constant data has no fit at all: the histogram with the option on is the histogram without it.
    [Theory]
    [InlineData(HistogramYScale.Frequency, HistogramBinningMode.Auto)]
    [InlineData(HistogramYScale.Percent, HistogramBinningMode.Count)]
    [InlineData(HistogramYScale.Density, HistogramBinningMode.WidthAndStart)]
    public void WithoutAnyDrawableFitTheHistogramIsTheOneWithoutFits(HistogramYScale scale, HistogramBinningMode mode)
    {
        var data = Data(Enumerable.Repeat(14.9, 40).ToArray());
        var off = Build(data, Options(scale, mode, fit: false));
        var on = Build(data, Options(scale, mode, fit: true));

        Assert.Null(Assert.Single(on.Series).NormalFit);
        Assert.Equal(Axis(off.Frame.YAxis), Axis(on.Frame.YAxis));
        Assert.Equal(WithoutFits(off), WithoutFits(on));
    }

    // Three series, only some of them with a fit: the whole histogram is drawn, and only the others' curves are left out.
    [Fact]
    public void ASeriesWithoutAFitOnlyGoesWithoutItsCurve()
    {
        var values = new List<double>();
        var groups = new List<string?>();
        void Add(string group, IEnumerable<double> items)
        {
            foreach (var item in items)
            {
                values.Add(item);
                groups.Add(group);
            }
        }

        Add("Normal", Normal(300, 15, 0.1));
        Add("Constant", Enumerable.Repeat(15.05, 50));
        Add("Single", [15.1]);
        Add("Tiny", Enumerable.Range(0, 60).Select(index => 14.95 + (index % 4 * 1e-7)));
        var data = Data([.. values], [.. groups]);

        var on = Build(data, new HistogramOptions(ShowNormalFit: true));
        var off = Build(data, HistogramOptions.Default);

        Assert.Equal(["Normal", "Constant", "Single", "Tiny"], on.Series.Select(series => series.Label));
        Assert.NotNull(on.Series[0].NormalFit);
        Assert.Null(on.Series[1].NormalFit);
        Assert.Null(on.Series[2].NormalFit);
        Assert.NotNull(on.Series[3].NormalFit);
        Assert.Equal(WithoutFits(off), WithoutFits(on));
        Assert.Equal(values.Count, on.Series.Sum(series => series.Counts.Sum()));

        // The tiny spread's curve is far taller than any bar, and the axis holds all of it.
        Assert.True(on.Frame.YAxis.Range.Maximum >= on.Series[3].NormalFit!.MaximumHeight);
        Assert.True(on.Series[3].NormalFit!.MaximumHeight > 100 * on.MaximumCount);
    }

    // More series than the palette has colours: each still has its fit, and the legend is the series and nothing more.
    [Fact]
    public void EverySeriesOfManyHasItsOwnFitAndNoLegendEntryIsAdded()
    {
        var values = new double[1200];
        var groups = new string?[1200];
        for (var index = 0; index < values.Length; index++)
        {
            groups[index] = $"Site {index % 12}";
            values[index] = 10 + (index % 12 * 0.5) + (Math.Sin(index * 0.7) * 0.4);
        }

        var data = Data(values, groups);
        var on = Build(data, new HistogramOptions(ShowNormalFit: true));
        var off = Build(data, HistogramOptions.Default);

        Assert.Equal(12, on.Series.Count);
        Assert.All(on.Series, series => Assert.NotNull(series.NormalFit));
        Assert.Equal(Enumerable.Range(0, 12), on.Series.Select(series => series.SeriesIndex));
        Assert.Equal(12, on.Frame.Legend!.Entries.Count);
        Assert.Equal(off.Frame.Legend!.Entries, on.Frame.Legend.Entries);
        Assert.Equal(off.Frame.Legend.Title, on.Frame.Legend.Title);
    }

    // Several variables drawn together: every "Variable / Group" series is fitted on its own, and agrees with its row
    // of the statistics panel to the last bit.
    [Fact]
    public void TogetherEverySeriesIsFittedOnItsOwnAndAgreesWithThePanel()
    {
        var reg2 = Wafer.Select(value => value + 0.05).Reverse().ToArray();
        var parts = new[]
        {
            Data(Wafer, Lots, "Reg1"),
            Data(reg2, [.. Lots.Reverse()], "Reg2"),
            Data([.. Wafer.Take(1000).Select(value => value * 1.01)], [.. Lots.Take(1000)], "Reg3")
        };
        var together = GraphVariablesTogether.Combine(new MultiVariableGraphData(GraphType.Histogram, Guid.NewGuid(), parts), Token);

        var on = Build(together, new HistogramOptions(HistogramYScale.Percent, ShowNormalFit: true));
        var off = Build(together, new HistogramOptions(HistogramYScale.Percent));
        var panel = GraphStatisticsPanelBuilder.Build(together, Token)!;

        Assert.Equal(12, on.Series.Count);
        Assert.Equal(WithoutFits(off), WithoutFits(on));
        for (var index = 0; index < on.Series.Count; index++)
        {
            Assert.Equal(panel.Rows[index].Label, on.Series[index].Label);
            Assert.Equal(panel.Rows[index].SeriesIndex, on.Series[index].SeriesIndex);
            Assert.Equal(panel.Rows[index].Mean, on.Series[index].NormalFit!.Mean);
            Assert.Equal(panel.Rows[index].StandardDeviation, on.Series[index].NormalFit!.StandardDeviation);
        }
    }

    [Fact]
    public void ANumericGroupsFitsAgreeWithThePanel()
    {
        var sites = new NumericGroupData(Column("Site"), (double?[])[.. Enumerable.Range(0, 3000).Select(index => index % 7 == 0 ? (double?)null : index % 4)]);
        var data = new UnivariateGraphData(GraphType.Histogram, Guid.NewGuid(), Column("Reg1"), Wafer, sites);

        var on = Build(data, new HistogramOptions(HistogramYScale.Density, ShowNormalFit: true));
        var panel = GraphStatisticsPanelBuilder.Build(data, Token)!;

        Assert.Equal(panel.Rows.Select(row => (row.Mean, row.StandardDeviation)),
            on.Series.Select(series => (series.NormalFit!.Mean, (double?)series.NormalFit!.StandardDeviation)));
    }

    // A curve too tall for any count axis (a spread far below its bins' width) stretches the axis as far as a count
    // axis can safely go; the plot area clips the rest of its peak.
    [Fact]
    public void AFrequencyAxisIsStretchedOnlyAsFarAsWholeCountsCanSafelyGo()
    {
        Assert.Equal(2, HistogramRenderModelBuilder.FitCountReach(1.5));
        Assert.Equal(7, HistogramRenderModelBuilder.FitCountReach(7));
        Assert.Equal(HistogramRenderModelBuilder.MaximumFitCount, HistogramRenderModelBuilder.FitCountReach(HistogramRenderModelBuilder.MaximumFitCount + 1e6));
        Assert.Equal(HistogramRenderModelBuilder.MaximumFitCount, HistogramRenderModelBuilder.FitCountReach(1e300));

        var axis = GraphAxisTicks.NiceCounts(HistogramRenderModelBuilder.MaximumFitCount);
        Assert.True(axis.Range.Maximum >= HistogramRenderModelBuilder.MaximumFitCount);
        Assert.True(axis.Ticks.Count is >= 2 and <= 12);

        var values = new List<double>();
        var groups = new List<string?>();
        for (var index = 0; index < 2000; index++)
        {
            values.Add(index % 2 == 0 ? 15 + (index % 3 * 1e-12) : index * 0.05);
            groups.Add(index % 2 == 0 ? "Tight" : "Wide");
        }

        var model = Build(Data([.. values], [.. groups]), new HistogramOptions(ShowNormalFit: true));
        Assert.True(model.Series[0].NormalFit!.MaximumHeight > HistogramRenderModelBuilder.MaximumFitCount);
        Assert.Equal(GraphAxisTicks.NiceCounts(HistogramRenderModelBuilder.MaximumFitCount).Range, model.Frame.YAxis.Range);
    }

    // ---- Drawing ----

    private static SKBitmap Render(HistogramRenderModel model, GraphTheme theme, int width = 800, int height = 560, List<SKRectI>? clips = null)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        IGraphPlotRenderer plot = new HistogramRenderer(model);
        if (clips is not null)
        {
            plot = new ClipRecorder(plot, clips);
        }

        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, width, height), theme, plot);
        return bitmap;
    }

    // Records where the plot was allowed to draw.
    private sealed class ClipRecorder(IGraphPlotRenderer inner, List<SKRectI> clips) : IGraphPlotRenderer
    {
        public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
        {
            clips.Add(canvas.DeviceClipBounds);
            inner.RenderPlot(canvas, transform, theme);
        }
    }

    // A model whose series carry the given fits in place of the builder's, so what is drawn is only what the model says.
    private static HistogramRenderModel WithFits(HistogramRenderModel model, Func<HistogramSeriesRenderModel, HistogramNormalFit?> fit) =>
        new(model.Frame, model.Bins, [.. model.Series.Select(series => series with { NormalFit = fit(series) })],
            model.SourceObservationCount, model.YScale);

    // A "curve" that is a straight horizontal line at the given height across the whole X axis: nothing a normal density
    // would draw, so it can only be where it is because the renderer drew the model's points.
    private static HistogramNormalFit Flat(HistogramRenderModel model, double height)
    {
        var from = model.Frame.XAxis.Range.Minimum;
        var to = model.Frame.XAxis.Range.Maximum;
        return new HistogramNormalFit(1, 1, [.. Enumerable.Range(0, 11).Select(index => new HistogramCurvePoint(from + ((to - from) * index / 10), height))]);
    }

    private static int Count(SKBitmap bitmap, Func<int, int, bool> where)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (where(x, y))
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheCurveIsDrawnOverTheBarsOnAHaloOfThePlotBackground(bool dark)
    {
        var theme = dark ? GraphThemes.Dark : GraphThemes.Light;
        var data = Data(Normal(2000, 15, 0.1));
        using var off = Render(Build(data, HistogramOptions.Default), theme);
        using var on = Render(Build(data, new HistogramOptions(ShowNormalFit: true)), theme);

        // The solid bars of an ungrouped histogram are the series' own colour; where the curve crosses them its halo
        // shows as the plot background, and where it runs over the background its line shows in the series colour.
        var bar = theme.SeriesColor(0);
        Assert.True(Count(on, (x, y) => off.GetPixel(x, y) == bar && Near(on.GetPixel(x, y), theme.PlotBackground)) > 50);
        Assert.True(Count(on, (x, y) => off.GetPixel(x, y) == theme.PlotBackground && Near(on.GetPixel(x, y), bar)) > 50);
    }

    // Antialiased lines rarely cover a pixel exactly: close enough to a colour is what a curve drawn in it looks like.
    // Every colour compared here differs from the others by far more in at least one channel.
    private static bool Near(SKColor actual, SKColor expected) =>
        Math.Abs(actual.Red - expected.Red) <= 64 && Math.Abs(actual.Green - expected.Green) <= 64
        && Math.Abs(actual.Blue - expected.Blue) <= 64;

    [Fact]
    public void TheRendererDrawsTheModelsPointsInTheirSeriesColourCyclingThroughThePalette()
    {
        var values = new double[1000];
        var groups = new string?[1000];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = index % 10;
            groups[index] = $"G{index % 10}";
        }

        var built = Build(Data(values, groups), HistogramOptions.Default);
        var top = built.Frame.YAxis.Range.Maximum;

        // Only the tenth series (index 9) has a fit: a flat line above every bar.
        var model = WithFits(built, series => series.SeriesIndex == 9 ? Flat(built, top * 0.9) : null);
        using var off = Render(built, GraphThemes.Light);
        using var on = Render(model, GraphThemes.Light);

        var colour = GraphThemes.Light.SeriesColor(9);
        Assert.Equal(GraphThemes.Light.SeriesColor(1), colour);
        var changed = new List<SKPointI>();
        for (var y = 0; y < on.Height; y++)
        {
            for (var x = 0; x < on.Width; x++)
            {
                if (on.GetPixel(x, y) != off.GetPixel(x, y))
                {
                    changed.Add(new SKPointI(x, y));
                }
            }
        }

        // One thin horizontal band, in the series' colour: the line the model holds, not a curve worked out again.
        Assert.NotEmpty(changed);
        Assert.InRange(changed.Max(point => point.Y) - changed.Min(point => point.Y), 1, 6);
        var columns = changed.GroupBy(point => point.X).ToList();
        Assert.True(columns.Count > on.Width / 2);
        Assert.All(columns, column => Assert.Contains(column, point => Near(on.GetPixel(point.X, point.Y), colour)));
    }

    [Fact]
    public void ACurveIsClippedToThePlotArea()
    {
        var built = Build(Data(Normal(500, 15, 0.1)), HistogramOptions.Default);
        var range = built.Frame.XAxis.Range;
        var span = range.Maximum - range.Minimum;

        // A curve that dips into the middle of the plot and runs far past both ends of the X axis and far above the Y
        // axis on its way there.
        var top = built.Frame.YAxis.Range.Maximum;
        var wide = new HistogramNormalFit(15, 1,
            [new(range.Minimum - (10 * span), top * 20), new(15, top * 0.5), new(range.Maximum + (10 * span), top * 20)]);
        var clips = new List<SKRectI>();
        using var off = Render(built, GraphThemes.Light);
        using var on = Render(WithFits(built, _ => wide), GraphThemes.Light, clips: clips);

        var plot = Assert.Single(clips);
        Assert.True(Count(on, (x, y) => !plot.Contains(x, y) && on.GetPixel(x, y) != off.GetPixel(x, y)) == 0);
        Assert.True(Count(on, (x, y) => plot.Contains(x, y) && on.GetPixel(x, y) != off.GetPixel(x, y)) > 0);
    }

    [Fact]
    public void WithoutFitsNothingIsDrawnBeyondTheBars()
    {
        var built = Build(Data(Wafer, Lots), HistogramOptions.Default);

        using var drawn = Render(built, GraphThemes.Dark);
        using var nulls = Render(WithFits(built, _ => null), GraphThemes.Dark);

        Assert.Equal(nulls.Bytes, drawn.Bytes);
    }

    [Fact]
    public void ThePngCarriesTheCurves()
    {
        var service = new GraphExportService();
        var data = Data(Wafer, Lots);
        byte[] Png(HistogramOptions options)
        {
            var model = Build(data, options);
            return service.RenderPng(new GraphExportSnapshot(model.Frame, new HistogramRenderer(model), GraphThemes.Light));
        }

        var off = Png(new HistogramOptions(HistogramYScale.Density));
        var on = Png(new HistogramOptions(HistogramYScale.Density, ShowNormalFit: true));

        Assert.NotEqual(off, on);
        Assert.Equal(on, Png(new HistogramOptions(HistogramYScale.Density, ShowNormalFit: true)));
        using var decoded = SKBitmap.Decode(on);
        Assert.Equal(GraphExportService.ExportWidth, decoded.Width);
    }
}
