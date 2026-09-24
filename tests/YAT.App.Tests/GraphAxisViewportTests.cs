using System.Globalization;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Axis ranges on a presented graph (Task #043): a viewport over the frame, put on after the statistics panel and the
// specification and before the labels. An Auto axis keeps the graph's own range - the very frame when both are Auto -
// a chosen end replaces its end, and the ticks follow the axis's own scale. Nothing of the graph itself changes: its
// plot model, bins, fits, points, statistics, specification, legend and labels are what they were.
public class GraphAxisViewportTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 600;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)) + ((i % 7) * 0.003))];
    private static readonly double[] Reg2 = [.. Enumerable.Range(0, Count).Select(i => 14.9 + (0.15 * Math.Cos(i * 0.23)))];

    private static readonly Specification Inside = new(14.8, 15, 15.3);

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData Lots() =>
        new(Column("Lot", WorksheetDataType.String), Enumerable.Range(0, Count).Select(i => (string?)$"Lot {i % 3}").ToArray());

    private sealed record Built(GraphRenderModel Frame, IGraphPlotRenderer Plot, GraphData Data, object Model);

    private static Built Build(GraphType type, HistogramOptions? histogram = null)
    {
        switch (type)
        {
            case GraphType.ScatterPlot:
            {
                var data = new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), Reg1, Reg2, Lots());
                var model = new ScatterRenderModelBuilder().Build(data, new ScatterPlotLabels("Reg1", "Reg2", "Lot"), Token)!;
                return new Built(model.Frame, new ScatterRenderer(model), data, model);
            }

            case GraphType.BoxPlot:
            {
                var data = new MultiVariableGraphData(type, Guid.Empty,
                [
                    new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, Lots()),
                    new UnivariateGraphData(type, Guid.Empty, Column("Reg2"), Reg2, Lots())
                ]);
                var model = new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels(["Reg1", "Reg2"], "Lot"), Token)!;
                return new Built(model.Frame, new BoxPlotRenderer(model), data, model);
            }

            case GraphType.Histogram:
            {
                var data = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, Lots());
                var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), histogram ?? HistogramOptions.Default, Token)!;
                return new Built(model.Frame, new HistogramRenderer(model), data, model);
            }

            case GraphType.ProbabilityPlot:
            {
                var data = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, Lots());
                var model = new ProbabilityPlotRenderModelBuilder().Build(data, new ProbabilityPlotLabels("Reg1", "Lot"), Token)!;
                return new Built(model.Frame, new ProbabilityPlotRenderer(model), data, model);
            }

            default:
            {
                var data = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, Lots());
                var model = new EmpiricalCdfRenderModelBuilder().Build(data, new EmpiricalCdfLabels("Reg1", "Lot"), Token)!;
                return new Built(model.Frame, new EmpiricalCdfRenderer(model), data, model);
            }
        }
    }

    private static GraphConfiguration Configuration(GraphType type, GraphAxisRangeOptions ranges, Specification? specification = null,
        GraphLabelOptions? labels = null, HistogramOptions? histogram = null) =>
        new(type, Guid.Empty, [])
        {
            Specification = specification ?? Specification.None,
            AxisRangeOptions = ranges,
            LabelOptions = labels ?? GraphLabelOptions.Default,
            HistogramOptions = histogram ?? HistogramOptions.Default
        };

    private static GraphPresentationState Present(Built built, GraphType type, GraphAxisRangeOptions ranges, Specification? specification = null,
        GraphLabelOptions? labels = null, HistogramOptions? histogram = null) =>
        GraphPresentation.Present(built.Frame, built.Data, Configuration(type, ranges, specification, labels, histogram), Token);

    private static GraphAxisRangeOptions X(double? minimum, double? maximum) => new(new GraphAxisRangeOption(minimum, maximum), GraphAxisRangeOption.Auto);

    private static GraphAxisRangeOptions Y(double? minimum, double? maximum) => new(GraphAxisRangeOption.Auto, new GraphAxisRangeOption(minimum, maximum));

    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    private static string Axis(GraphAxisModel axis) =>
        $"{axis.Title} {axis.Scale} {R(axis.Range.Minimum)}..{R(axis.Range.Maximum)} [{string.Join(",", axis.Ticks.Select(tick => $"{R(tick.Value)}={tick.Label}"))}]";

    // Everything but the two axes' ranges and ticks: what a viewport must never change.
    private static string AllButAxes(GraphRenderModel frame) =>
        $"{frame.Title}|{frame.XAxis.Title}/{frame.XAxis.Scale}|{frame.YAxis.Title}/{frame.YAxis.Scale}" +
        $"|legend={(frame.Legend is { } legend ? legend.Title + ":" + string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}")) : "none")}" +
        $"|panel={(frame.StatisticsPanel is { } panel ? string.Join(";", panel.Rows.Select(row => $"{row.Label} {R(row.Mean)} {row.StandardDeviationText} {row.Count}")) : "none")}" +
        $"|lines={string.Join(",", frame.ReferenceLines.Select(line => $"{line.Axis}:{R(line.Value)}:{line.Label}"))}";

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    // ---- Auto ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void BothAxesAutoLeaveTheVeryFrame(GraphType type)
    {
        var built = Build(type);
        var state = Present(built, type, GraphAxisRangeOptions.Default, Inside);

        Assert.Same(state.BaseFrame, state.UnlabelledFrame);
        Assert.Same(state.BaseFrame, state.Frame);
        Assert.Same(built.Frame, GraphAxisViewportBuilder.Attach(built.Frame, GraphTypeDefinitions.For(type), GraphAxisRangeOptions.Default));
        Assert.Same(built.Frame, GraphAxisViewportBuilder.Attach(built.Frame, GraphTypeDefinitions.For(type),
            new GraphAxisRangeOptions(new GraphAxisRangeOption(), new GraphAxisRangeOption())));
    }

    // ---- One end, the other end, both ----

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void AChosenXEndReplacesOnlyItsOwnEnd(GraphType type)
    {
        var built = Build(type);
        var autoFrame = Present(built, type, GraphAxisRangeOptions.Default).Frame;
        var auto = autoFrame.XAxis.Range;

        var minimumOnly = Present(built, type, X(14.95, null)).Frame;
        Assert.Equal(new GraphAxisRange(14.95, auto.Maximum), minimumOnly.XAxis.Range);

        var maximumOnly = Present(built, type, X(null, 15.05)).Frame;
        Assert.Equal(new GraphAxisRange(auto.Minimum, 15.05), maximumOnly.XAxis.Range);

        var both = Present(built, type, X(14.9, 15.2)).Frame;
        Assert.Equal(new GraphAxisRange(14.9, 15.2), both.XAxis.Range);

        foreach (var frame in new[] { minimumOnly, maximumOnly, both })
        {
            // The other axis, and everything but the ranges and ticks, is what it was.
            Assert.Same(built.Frame.YAxis, frame.YAxis);
            Assert.Equal(AllButAxes(autoFrame), AllButAxes(frame));
            Assert.All(frame.XAxis.Ticks, tick => Assert.InRange(tick.Value, frame.XAxis.Range.Minimum, frame.XAxis.Range.Maximum));
            Assert.True(frame.XAxis.Ticks.Count >= 2);
        }
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot, 14.8, 15.2)]
    [InlineData(GraphType.BoxPlot, 14.5, 15.5)]
    [InlineData(GraphType.Histogram, 0, 1000)]
    public void AChosenYRangeIsTheYAxis(GraphType type, double minimum, double maximum)
    {
        var built = Build(type);
        var frame = Present(built, type, Y(minimum, maximum)).Frame;

        Assert.Equal(new GraphAxisRange(minimum, maximum), frame.YAxis.Range);
        Assert.Same(built.Frame.XAxis, frame.XAxis);
        Assert.Equal(AllButAxes(Present(built, type, GraphAxisRangeOptions.Default).Frame), AllButAxes(frame));
    }

    // A box plot's categories are the X axis whatever is typed for it: no range, no ticks, no slots change.
    [Fact]
    public void ABoxPlotsCategoriesAreNeverChanged()
    {
        var built = Build(GraphType.BoxPlot);
        var frame = Present(built, GraphType.BoxPlot, new GraphAxisRangeOptions(GraphAxisRangeOption.Auto, new GraphAxisRangeOption(14, 16))).Frame;

        Assert.Same(built.Frame.XAxis, frame.XAxis);
        Assert.Equal(GraphAxisScale.Categorical, frame.XAxis.Scale);
        Assert.Equal(new GraphAxisRange(14, 16), frame.YAxis.Range);
        var definition = GraphTypeDefinitions.For(GraphType.BoxPlot);
        Assert.Same(built.Frame, GraphAxisViewportBuilder.Attach(built.Frame, definition, X(0, 1)));
    }

    // ---- What the viewport shows, and what it clips ----

    [Fact]
    public void ARangeAwayFromEveryObservationIsAllowedAndDrawsNothingOfTheData()
    {
        foreach (var type in Enum.GetValues<GraphType>())
        {
            var built = Build(type);
            var ranges = type == GraphType.BoxPlot ? Y(100, 200) : new GraphAxisRangeOptions(new GraphAxisRangeOption(100, 200), GraphAxisRangeOption.Auto);
            var frame = Present(built, type, ranges).Frame;

            using var bitmap = new SKBitmap(640, 420);
            using var canvas = new SKCanvas(bitmap);
            SKRect? plotArea = null;
            new SkiaGraphRenderer().Render(canvas, frame, new SKRect(0, 0, 640, 420), GraphThemes.Light,
                new PlotAreaRecorder(built.Plot, area => plotArea = area));

            // Inside the plot area only the background and the grid, antialiased edges included - greys, no mark of any
            // series.
            var area = plotArea!.Value;
            for (var y = (int)area.Top + 2; y < (int)area.Bottom - 2; y++)
            {
                for (var x = (int)area.Left + 2; x < (int)area.Right - 2; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    Assert.True(pixel.Red == pixel.Green && pixel.Green == pixel.Blue,
                        $"{type}: ({x}, {y}) is {pixel}");
                }
            }
        }
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void AChosenXKeepsTheSpecificationButOverridesTheRangeItWidened(GraphType type)
    {
        var built = Build(type);
        var wide = new Specification(10, null, 20);
        var auto = Present(built, type, GraphAxisRangeOptions.Default, wide).Frame;
        var chosen = Present(built, type, X(14.9, 15.1), wide).Frame;

        // Auto: the specification widens X to reach its lines, as it always did.
        Assert.True(auto.XAxis.Range.Minimum < 10 && auto.XAxis.Range.Maximum > 20);

        // Chosen: the range is the user's; the lines are still the graph's, outside the range and so not drawn.
        Assert.Equal(new GraphAxisRange(14.9, 15.1), chosen.XAxis.Range);
        Assert.Equal(auto.ReferenceLines, chosen.ReferenceLines);
        using var font = new SKFont { Size = 11 };
        Assert.Empty(SkiaGraphRenderer.PlaceReferenceLabels(chosen, new SKRect(40, 40, 600, 400), font));
        Assert.Equal(2, SkiaGraphRenderer.PlaceReferenceLabels(auto, new SKRect(40, 40, 600, 400), font).Count);
    }

    [Fact]
    public void AChosenYOverridesTheRangeANormalFitRaisedAndLeavesTheFitAsItWas()
    {
        var uniform = new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Flat"), (double[])[.. Enumerable.Range(0, 1000).Select(i => i / 10.0)], null);
        var options = new HistogramOptions(HistogramYScale.Frequency, HistogramBinningMode.Count, BinCount: 10, ShowNormalFit: true);
        var model = new HistogramRenderModelBuilder().Build(uniform, new HistogramPlotLabels("Flat"), options, Token)!;
        var peak = model.Series[0].NormalFit!.MaximumHeight;
        var configuration = Configuration(GraphType.Histogram, Y(0, 110), histogram: options);

        var auto = GraphPresentation.Present(model.Frame, uniform, configuration with { AxisRangeOptions = GraphAxisRangeOptions.Default }, Token).Frame;
        var chosen = GraphPresentation.Present(model.Frame, uniform, configuration, Token).Frame;

        Assert.True(auto.YAxis.Range.Maximum >= peak);
        Assert.Equal(new GraphAxisRange(0, 110), chosen.YAxis.Range);
        Assert.True(peak > 110);

        // The model - bins, counts, heights and the fit's own points - is the one the builder made; the viewport never
        // sees it, and the curve is only clipped where it is drawn.
        Assert.Equal(peak, model.Series[0].NormalFit!.MaximumHeight);
        Assert.Equal(HistogramNormalFit.PointCount, model.Series[0].NormalFit!.Points.Count);
    }

    // ---- Ticks by scale ----

    [Fact]
    public void AFrequencyAxisKeepsWholeNumberTicksOverAnyRange()
    {
        var built = Build(GraphType.Histogram);
        foreach (var (minimum, maximum) in new[] { (0d, 3d), (2.5, 7.5), (10d, 1000d), (0.2, 0.8), (1d, 1e7) })
        {
            var axis = Present(built, GraphType.Histogram, Y(minimum, maximum)).Frame.YAxis;

            Assert.Equal(GraphAxisScale.Count, axis.Scale);
            Assert.All(axis.Ticks, tick =>
            {
                Assert.Equal(Math.Round(tick.Value), tick.Value);
                Assert.InRange(tick.Value, minimum, maximum);
                Assert.Equal(tick.Value.ToString("0", CultureInfo.InvariantCulture), tick.Label);
            });
            var steps = axis.Ticks.Zip(axis.Ticks.Skip(1), (a, b) => b.Value - a.Value).Distinct().ToList();
            Assert.True(steps.Count <= 1 && steps.All(step => step >= 1));
        }

        // No whole number inside: no tick rather than a fraction on a count axis.
        Assert.Empty(GraphAxisTicks.NiceCountsWithin(new GraphAxisRange(0.2, 0.8)));
        Assert.Equal(["0", "1", "2", "3"], GraphAxisTicks.NiceCountsWithin(new GraphAxisRange(0, 3)).Select(tick => tick.Label));
    }

    [Theory]
    [InlineData(HistogramYScale.Percent)]
    [InlineData(HistogramYScale.Density)]
    public void APercentOrDensityHistogramReadsOnNiceTicks(HistogramYScale scale)
    {
        var options = new HistogramOptions(scale);
        var built = Build(GraphType.Histogram, options);
        var axis = Present(built, GraphType.Histogram, Y(5, 37), histogram: options).Frame.YAxis;

        Assert.Equal(GraphAxisScale.Linear, axis.Scale);
        Assert.Equal(GraphAxisTicks.Nice(new GraphAxisRange(5, 37)), axis.Ticks);
    }

    // A probability axis is typed in the percent it shows and placed at the scores those percents belong to.
    [Fact]
    public void AProbabilityRangeIsTypedInPercentAndDrawnOnScores()
    {
        var built = Build(GraphType.ProbabilityPlot);
        var axis = Present(built, GraphType.ProbabilityPlot, Y(1, 99)).Frame.YAxis;

        Assert.Equal(GraphAxisScale.Probability, axis.Scale);
        Assert.Equal(ProbabilityAxis.Score(1), axis.Range.Minimum);
        Assert.Equal(ProbabilityAxis.Score(99), axis.Range.Maximum);
        Assert.Equal(["1", "2", "5", "10", "20", "30", "50", "70", "80", "90", "95", "98", "99"], axis.Ticks.Select(tick => tick.Label));
        Assert.All(axis.Ticks, tick => Assert.Equal(ProbabilityAxis.Score(double.Parse(tick.Label, CultureInfo.InvariantCulture)), tick.Value));

        // And back: the automatic ends read in percent.
        var (low, high) = GraphAxisViewportBuilder.AutoRange(built.Frame.YAxis);
        Assert.InRange(low, 0, 0.1);
        Assert.InRange(high, 99.9, 100);
        Assert.Equal(1, ProbabilityAxis.Percent(ProbabilityAxis.Score(1)), 1e-9);
    }

    [Fact]
    public void ANarrowProbabilityRangeReadsOnOneTwoFivePercents()
    {
        var built = Build(GraphType.ProbabilityPlot);
        var axis = Present(built, GraphType.ProbabilityPlot, Y(40, 60)).Frame.YAxis;

        // Only 50 of the standard percents lies inside: 1-2-5 percents instead, each at its own score.
        Assert.Equal(["40", "45", "50", "55", "60"], axis.Ticks.Select(tick => tick.Label));
        Assert.All(axis.Ticks, tick => Assert.InRange(tick.Value, axis.Range.Minimum, axis.Range.Maximum));
        Assert.Equal(ProbabilityAxis.Score(45), axis.Ticks[1].Value);
    }

    [Fact]
    public void AnEmpiricalCdfsPercentAxisReadsOnNiceTicks()
    {
        var built = Build(GraphType.EmpiricalCdf);
        var axis = Present(built, GraphType.EmpiricalCdf, Y(90, 100)).Frame.YAxis;

        Assert.Equal(GraphAxisScale.Percent, axis.Scale);
        Assert.Equal(new GraphAxisRange(90, 100), axis.Range);
        Assert.Equal(["90", "92", "94", "96", "98", "100"], axis.Ticks.Select(tick => tick.Label));
    }

    // ---- The probability plot's fitted line ----

    [Fact]
    public void TheFittedLineIsTheLineItselfAtAnyScore()
    {
        var line = new ProbabilityPlotFittedLine(15, 0.1, -3, 3);

        Assert.Equal(line.FromValue, line.ValueAt(-3));
        Assert.Equal(line.ToValue, line.ValueAt(3));
        Assert.Equal(15.5, line.ValueAt(5), 1e-12);
    }

    // Over a wider score range than the builder's, the line reaches both edges of the plot.
    [Fact]
    public void TheFittedLineReachesTheEdgesOfAChosenProbabilityRange()
    {
        var built = Build(GraphType.ProbabilityPlot);
        var model = (ProbabilityPlotRenderModel)built.Model;
        var frame = Present(built, GraphType.ProbabilityPlot, new GraphAxisRangeOptions(
            new GraphAxisRangeOption(14, 16), new GraphAxisRangeOption(0.0001, 99.9999))).Frame;

        // Only the lines: each series' one point far outside the plot.
        var linesOnly = new ProbabilityPlotRenderModel(model.Frame,
            [.. model.Series.Select(series => new ProbabilityPlotSeriesRenderModel(series.Label, series.SeriesIndex, (ProbabilityPlotPoint[])[new(1000, 0)], series.FittedLine, series.ObservationCount))],
            model.SourceObservationCount);
        using var bitmap = new SKBitmap(640, 420);
        using var canvas = new SKCanvas(bitmap);
        SKRect? plotArea = null;
        new SkiaGraphRenderer().Render(canvas, frame, new SKRect(0, 0, 640, 420), GraphThemes.Light,
            new PlotAreaRecorder(new ProbabilityPlotRenderer(linesOnly), area => plotArea = area));

        var area = plotArea!.Value;
        // The three lots' lines nearly coincide; whichever is drawn last shows.
        SKColor[] colours = [.. Enumerable.Range(0, 3).Select(GraphThemes.Light.SeriesColor)];
        bool Near(SKColor pixel) => colours.Any(colour =>
            Math.Abs(pixel.Red - colour.Red) < 60 && Math.Abs(pixel.Green - colour.Green) < 60 && Math.Abs(pixel.Blue - colour.Blue) < 60);
        var rows = Enumerable.Range(0, bitmap.Height).Where(y => Enumerable.Range((int)area.Left, (int)area.Width).Any(x => Near(bitmap.GetPixel(x, y)))).ToList();

        // Drawn from the bottom of the plot area to its top.
        Assert.True(rows.Count > 0, $"no line in {area}");
        Assert.InRange(rows.Min(), area.Top - 1, area.Top + 2);
        Assert.InRange(rows.Max(), area.Bottom - 3, area.Bottom);
    }

    private sealed class PlotAreaRecorder(IGraphPlotRenderer inner, Action<SKRect> record) : IGraphPlotRenderer
    {
        public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
        {
            record(transform.PlotArea);
            inner.RenderPlot(canvas, transform, theme);
        }
    }

    // ---- A range that does not fit the graph ----

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void AChosenMinimumAtOrAboveTheAutomaticMaximumIsRefusedWithTheReason(GraphType type)
    {
        var built = Build(type);
        var definition = GraphTypeDefinitions.For(type);
        var autoMaximum = built.Frame.XAxis.Range.Maximum;

        var conflict = Assert.Single(GraphAxisViewportBuilder.Conflicts(built.Frame, definition, X(autoMaximum + 1, null)));
        Assert.StartsWith("The X-axis minimum (", conflict);
        Assert.Contains("must be below the automatic X-axis maximum (", conflict);

        var thrown = Assert.Throws<GraphPreparationException>(() => Present(built, type, X(autoMaximum, null)));
        Assert.Contains("X-axis minimum", thrown.Message);
    }

    [Fact]
    public void AChosenMaximumAtOrBelowTheAutomaticMinimumIsRefusedWithTheReason()
    {
        var built = Build(GraphType.ScatterPlot);
        var definition = GraphTypeDefinitions.For(GraphType.ScatterPlot);

        var conflict = Assert.Single(GraphAxisViewportBuilder.Conflicts(built.Frame, definition, Y(null, built.Frame.YAxis.Range.Minimum - 1)));
        Assert.Contains("The Y-axis maximum (", conflict);
        Assert.Contains("must be above the automatic Y-axis minimum (", conflict);

        // A probability axis says its automatic end in percent.
        var probability = Build(GraphType.ProbabilityPlot);
        var percent = Assert.Single(GraphAxisViewportBuilder.Conflicts(probability.Frame, GraphTypeDefinitions.For(GraphType.ProbabilityPlot), Y(99.9999, null)));
        Assert.Contains("(99.9999)", percent);
        Assert.Contains("automatic Y-axis maximum (99.9", percent);
    }

    [Fact]
    public void RangesTheRulesRefuseAreNotShownAtAll()
    {
        var built = Build(GraphType.Histogram);
        var definition = GraphTypeDefinitions.For(GraphType.Histogram);

        Assert.Throws<ArgumentException>(() => GraphAxisViewportBuilder.Attach(built.Frame, definition, Y(-5, 10)));
        Assert.Throws<ArgumentException>(() => GraphAxisViewportBuilder.Attach(built.Frame, definition, X(20, 10)));

        // They are the configuration's to report, not conflicts of the graph.
        Assert.Empty(GraphAxisViewportBuilder.Conflicts(built.Frame, definition, X(20, 10)));
    }

    // ---- Nothing of the graph changes ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheViewportChangesNoPlotModelStatisticSpecificationLegendOrLabel(GraphType type)
    {
        var built = Build(type);
        var labels = new GraphLabelOptions(GraphLabelOption.Custom("Wafer 晶圓"), GraphLabelOption.Auto, GraphLabelOption.Custom("Y"));
        var specification = GraphTypeDefinitions.For(type).Supports(GraphCapability.SpecificationLines) ? Inside : null;
        var auto = Present(built, type, GraphAxisRangeOptions.Default, specification, labels).Frame;
        var ranges = type == GraphType.BoxPlot ? Y(14.95, null) : new GraphAxisRangeOptions(new GraphAxisRangeOption(14.95, null), GraphAxisRangeOption.Auto);
        var chosen = Present(built, type, ranges, specification, labels).Frame;

        Assert.Equal(AllButAxes(auto), AllButAxes(chosen));
        Assert.Same(auto.Legend, chosen.Legend);
        Assert.Equal(auto.ReferenceLines, chosen.ReferenceLines);
        Assert.Equal(("Wafer 晶圓", "Y"), (chosen.Title, chosen.YAxis.Title));
    }

    // The same ranges put on at setup or afterwards give the same graph, drawn and exported the same.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void RangesChosenAfterwardsGiveTheGraphTheSetupWouldHave(GraphType type)
    {
        var built = Build(type);
        var ranges = type == GraphType.BoxPlot ? Y(14.8, 15.2) : new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, null), new GraphAxisRangeOption(null, null));
        var atSetup = Present(built, type, ranges);
        var afterwards = Present(built, type, GraphAxisRangeOptions.Default).WithAxisRanges(ranges);
        var back = afterwards.WithAxisRanges(GraphAxisRangeOptions.Default);

        var service = new GraphExportService();
        byte[] Png(GraphPresentationState state) => service.RenderPng(new GraphExportSnapshot(state.Frame, built.Plot, GraphThemes.Dark));
        Assert.Equal(Png(atSetup), Png(afterwards));
        Assert.Equal(Axis(atSetup.Frame.XAxis) + Axis(atSetup.Frame.YAxis), Axis(afterwards.Frame.XAxis) + Axis(afterwards.Frame.YAxis));

        // And back to Auto: the automatic frame itself.
        Assert.Same(back.BaseFrame, back.Frame);
        Assert.Equal(Png(Present(built, type, GraphAxisRangeOptions.Default)), Png(back));
    }
}
