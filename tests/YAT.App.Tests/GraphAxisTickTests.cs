using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using static YAT.App.Tests.GraphLabelEditingTests;

namespace YAT.App.Tests;

// The ticks a user chooses for an axis (Task #054): Auto - the graph exactly as it was - a fixed interval or custom
// values, put on the frame after its ranges (GraphAxisTickBuilder) and kept by the graph window's presentation state
// apart from the ranges. Only ticks on the range enter the frame, so values off it never change the layout; an interval
// that would draw more than a hundred ticks is refused, never replaced. Edited from the Edit Scale dialog with the range,
// kept by every other edit, copied and exported as shown.
public class GraphAxisTickTests
{
    private static readonly SKRect Canvas = new(0, 0, 760, 488);

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    private static GraphAxisTickOption.FixedInterval Interval(double interval) => new(interval);

    private static GraphAxisTickOption.CustomValues Values(params double[] values) => new(values);

    private static GraphAxisTickOptions XTicks(GraphAxisTickOption option) => GraphAxisTickOptions.Default with { X = option };

    private static GraphAxisTickOptions YTicks(GraphAxisTickOption option) => GraphAxisTickOptions.Default with { Y = option };

    private static GraphAxisRangeOptions XRange(double minimum, double maximum) =>
        GraphAxisRangeOptions.Default with { X = new GraphAxisRangeOption(minimum, maximum) };

    private static GraphAxisRangeOptions YRange(double minimum, double maximum) =>
        GraphAxisRangeOptions.Default with { Y = new GraphAxisRangeOption(minimum, maximum) };

    private static byte[] Png(GraphRenderModel frame, IGraphPlotRenderer plot) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, GraphThemes.Light));

    private static string[] Labels(GraphAxisModel axis) => [.. axis.Ticks.Select(tick => tick.Label)];

    // ---- Auto ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AutoTicksAreTheGraphExactlyAsItWas(GraphType type)
    {
        var graph = Present(type).Graph;

        Assert.Same(graph.UnlabelledFrame, GraphAxisTickBuilder.Attach(graph.UnlabelledFrame, graph.Definition, GraphAxisTickOptions.Default));
        Assert.Equal(GraphAxisTickOptions.Default, graph.AxisTickOptions);
        Assert.Empty(GraphAxisTickBuilder.Problems(graph.UnlabelledFrame, graph.Definition, GraphAxisTickOptions.Default));
    }

    [Fact]
    public void AutoTicksOverAChosenRangeAreTheTicksThatRangeAlwaysHad()
    {
        var presented = Present(GraphType.ScatterPlot);
        var ranges = XRange(14.85, 15.15);

        var ranged = presented.Graph.WithAxisRanges(ranges);
        var scaled = presented.Graph.WithAxisScale(ranges, GraphAxisTickOptions.Default);

        Assert.Equal(Labels(ranged.Frame.XAxis), Labels(scaled.Frame.XAxis));
        Assert.Equal(Png(ranged.Frame, presented.Plot), Png(scaled.Frame, presented.Plot));
    }

    // ---- A fixed interval ----

    [Fact]
    public void AnIntervalMarksEveryMultipleOnTheRangeAndKeepsTheRange()
    {
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.9, 15.1), XTicks(Interval(0.02)));
        var axis = graph.Frame.XAxis;

        Assert.Equal(new GraphAxisRange(14.9, 15.1), axis.Range);
        Assert.Equal(
            ["14.90", "14.92", "14.94", "14.96", "14.98", "15.00", "15.02", "15.04", "15.06", "15.08", "15.10"],
            Labels(axis));
        Assert.All(axis.Ticks, tick => Assert.InRange(tick.Value, 14.9 - 1e-9, 15.1 + 1e-9));
    }

    [Fact]
    public void AnIntervalIsCountedFromZeroNotFromTheEndOfTheRange()
    {
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.91, 15.05), XTicks(Interval(0.05)));

        Assert.Equal(["14.95", "15.00", "15.05"], Labels(graph.Frame.XAxis));
    }

    [Fact]
    public void AnIntervalIsLabelledWithTheDecimalsItNeeds()
    {
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.9, 15), XTicks(Interval(0.025)));

        Assert.Equal(["14.900", "14.925", "14.950", "14.975", "15.000"], Labels(graph.Frame.XAxis));
    }

    [Fact]
    public void AnIntervalOverTheAutoRangeMarksIt()
    {
        var presented = Present(GraphType.ScatterPlot);
        var auto = presented.Graph.Frame.XAxis.Range;

        var axis = presented.Graph.WithAxisScale(GraphAxisRangeOptions.Default, XTicks(Interval(0.05))).Frame.XAxis;

        Assert.Equal(auto, axis.Range);
        Assert.NotEmpty(axis.Ticks);
        Assert.All(axis.Ticks, tick => Assert.Equal(0, Math.Round(tick.Value / 0.05) * 0.05 - tick.Value, 9));
    }

    [Fact]
    public void AHundredTicksAreDrawnAndAnEditorRefusesOneMore()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;

        Assert.Equal(100, graph.WithAxisScale(XRange(0, 99), XTicks(Interval(1))).Frame.XAxis.Ticks.Count);

        var ranges = XRange(0, 100);
        var problem = Assert.Single(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, ranges, XTicks(Interval(1))));
        Assert.Equal("The X-axis tick interval 1 would draw more than 100 ticks over the range shown. Enter a larger interval.", problem);
        // The graph itself never refuses it (Task #055: a zoomed-out view must not be refused): it draws every 2nd multiple,
        // and the interval stays 1.
        var thinned = graph.WithAxisScale(ranges, XTicks(Interval(1)));
        Assert.Equal(XTicks(Interval(1)), thinned.AxisTickOptions);
        Assert.Equal(51, thinned.Frame.XAxis.Ticks.Count);
        Assert.All(thinned.Frame.XAxis.Ticks, tick => Assert.Equal(0, tick.Value % 2));
    }

    [Fact]
    public void AnIntervalTooSmallForTheValuesIsRefusedNotReplaced()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;

        Assert.NotEmpty(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, GraphAxisRangeOptions.Default, XTicks(Interval(1e-9))));
    }

    // ---- Custom values ----

    [Fact]
    public void CustomValuesOnTheRangeAreDrawnInOneFormat()
    {
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.85, 15.15), XTicks(Values(15.1, 14.9, 15.0, 14.95)));

        Assert.Equal(["14.90", "14.95", "15.00", "15.10"], Labels(graph.Frame.XAxis));
        Assert.Equal([14.9, 14.95, 15.0, 15.1], graph.Frame.XAxis.Ticks.Select(tick => tick.Value));
    }

    [Fact]
    public void CustomValuesOffTheRangeAreKeptButNotPutInTheFrame()
    {
        var ticks = XTicks(Values(0, 14.9, 15.0, 100));
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.85, 15.15), ticks);

        Assert.Equal(ticks, graph.AxisTickOptions);
        Assert.Equal([14.9, 15.0], graph.Frame.XAxis.Ticks.Select(tick => tick.Value));
        Assert.Equal(2, GraphAxisTickBuilder.OutsideCount(graph.Frame.XAxis, Values(0, 14.9, 15.0, 100)));
    }

    [Fact]
    public void CustomValuesOffTheRangeDoNotChangeTheLayoutOrTheDrawing()
    {
        var presented = Present(GraphType.ScatterPlot);
        var ranges = new GraphAxisRangeOptions(new GraphAxisRangeOption(14.85, 15.15), new GraphAxisRangeOption(14.7, 15.1));
        var near = new GraphAxisTickOptions(Values(14.9, 15.0), Values(14.8, 15.0));
        var far = new GraphAxisTickOptions(Values(-98765.4321, 14.9, 15.0, 123456.789), Values(14.8, 15.0, 1e9 + 0.123456));

        var shown = presented.Graph.WithAxisScale(ranges, near).Frame;
        var withFar = presented.Graph.WithAxisScale(ranges, far).Frame;

        Assert.Equal(Labels(shown.XAxis), Labels(withFar.XAxis));
        Assert.Equal(Labels(shown.YAxis), Labels(withFar.YAxis));
        Assert.Equal(SkiaGraphRenderer.Layout(shown, Canvas, GraphThemes.Light).PlotArea, SkiaGraphRenderer.Layout(withFar, Canvas, GraphThemes.Light).PlotArea);
        Assert.Equal(Png(shown, presented.Plot), Png(withFar, presented.Plot));
    }

    [Fact]
    public void ARangeThatReachesAKeptValueDrawsIt()
    {
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.85, 15.15), XTicks(Values(14.9, 15.0, 20)));
        Assert.DoesNotContain(20d, graph.Frame.XAxis.Ticks.Select(tick => tick.Value));

        var wider = graph.WithAxisRanges(XRange(14, 21));

        Assert.Equal(graph.AxisTickOptions, wider.AxisTickOptions);
        Assert.Equal(["14.9", "15.0", "20.0"], Labels(wider.Frame.XAxis));
    }

    [Fact]
    public void ValuesAllOffTheRangeLeaveTheAxisWithoutTicks()
    {
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.85, 15.15), XTicks(Values(100, 200)));

        Assert.Empty(graph.Frame.XAxis.Ticks);
        Assert.Equal(new GraphAxisRange(14.85, 15.15), graph.Frame.XAxis.Range);
    }

    [Fact]
    public void LargeOrVeryPreciseValuesAreLabelledScientifically()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;

        Assert.Equal(["1E+6", "2.5E+6"], Labels(graph.WithAxisScale(XRange(0, 3e6), XTicks(Values(1e6, 2.5e6))).Frame.XAxis));
        Assert.Equal(["1.2E-11", "1.5E+1"], Labels(graph.WithAxisScale(XRange(0, 20), XTicks(Values(1.2e-11, 15))).Frame.XAxis));
    }

    [Theory]
    [InlineData(0.02, 2)]
    [InlineData(0.025, 3)]
    [InlineData(12, 0)]
    [InlineData(1e-5, 5)]
    [InlineData(1.5e-7, 8)]
    [InlineData(2.5e10, 0)]
    public void TheDecimalsOfAValueAreThoseOfItsShortestText(double value, int decimals) =>
        Assert.Equal(decimals, GraphAxisTickBuilder.DecimalsOf(value));

    // ---- Percent and probability axes ----

    [Fact]
    public void AProbabilityAxisIsMarkedInPercentPlacedAtItsScores()
    {
        var graph = Present(GraphType.ProbabilityPlot).Graph;

        var interval = graph.WithAxisScale(GraphAxisRangeOptions.Default, YTicks(Interval(10))).Frame.YAxis;
        Assert.Equal(["10", "20", "30", "40", "50", "60", "70", "80", "90"], Labels(interval));
        Assert.Equal(ProbabilityAxis.Score(30), interval.Ticks[2].Value, 12);
        Assert.Equal(graph.Frame.YAxis.Range, interval.Range);

        var custom = graph.WithAxisScale(GraphAxisRangeOptions.Default, YTicks(Values(50, 1, 99))).Frame.YAxis;
        Assert.Equal(["1", "50", "99"], Labels(custom));
        Assert.Equal([ProbabilityAxis.Score(1), 0, ProbabilityAxis.Score(99)], custom.Ticks.Select(tick => tick.Value));

        Assert.Equal(["0.1", "50.0"], Labels(graph.WithAxisScale(GraphAxisRangeOptions.Default, YTicks(Values(0.1, 50))).Frame.YAxis));
    }

    [Fact]
    public void AProbabilityIntervalStopsAtTheRangeShown()
    {
        var graph = Present(GraphType.ProbabilityPlot).Graph.WithAxisScale(YRange(20, 80), YTicks(Interval(5)));

        Assert.Equal(["20", "25", "30", "35", "40", "45", "50", "55", "60", "65", "70", "75", "80"], Labels(graph.Frame.YAxis));
    }

    [Fact]
    public void AProbabilityAxisRefusesValuesItCannotShow()
    {
        var graph = Present(GraphType.ProbabilityPlot).Graph;

        Assert.Equal(
            "The Y-axis tick values must be from 0.0001 to 99.9999 (%) (100 is not).",
            Assert.Single(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, GraphAxisRangeOptions.Default, YTicks(Values(50, 100)))));
    }

    [Fact]
    public void APercentAxisIsMarkedInPercent()
    {
        var graph = Present(GraphType.EmpiricalCdf).Graph;

        Assert.Equal(["0", "25", "50", "75", "100"], Labels(graph.WithAxisScale(GraphAxisRangeOptions.Default, YTicks(Interval(25))).Frame.YAxis));
        Assert.Equal(
            "The Y-axis tick values must be from 0 to 100 (120 is not).",
            Assert.Single(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, GraphAxisRangeOptions.Default, YTicks(Values(120)))));
    }

    // ---- A histogram's count axis ----

    [Fact]
    public void ACountAxisOnAutoTicksIsMarkedInWholeCountsAsAlways()
    {
        var presented = Present(GraphType.Histogram);
        var graph = presented.Graph;
        Assert.Equal(GraphAxisScale.Count, graph.Frame.YAxis.Scale);

        // Its own whole-number ticks over its own range - those the histogram's builder gave it...
        Assert.Equal(Labels(presented.BuilderFrame.YAxis), Labels(graph.Frame.YAxis));
        Assert.Equal(presented.BuilderFrame.YAxis.Range, graph.Frame.YAxis.Range);
        Assert.All(graph.Frame.YAxis.Ticks, tick => Assert.DoesNotContain(".", tick.Label));
        Assert.Equal(Png(graph.Frame, presented.Plot), Png(graph.WithAxisScale(GraphAxisRangeOptions.Default, GraphAxisTickOptions.Default).Frame, presented.Plot));

        // ...and over a chosen range.
        var ranged = graph.WithAxisScale(YRange(0, 23), GraphAxisTickOptions.Default).Frame.YAxis;
        Assert.Equal(Labels(new GraphAxisModel(new GraphAxisRange(0, 23), GraphAxisTicks.NiceCountsWithin(new GraphAxisRange(0, 23)))), Labels(ranged));
        Assert.Equal(Png(graph.WithAxisRanges(YRange(0, 23)).Frame, presented.Plot), Png(graph.WithAxisScale(YRange(0, 23), GraphAxisTickOptions.Default).Frame, presented.Plot));
    }

    [Fact]
    public void ACountAxisTakesADecimalInterval()
    {
        var graph = Present(GraphType.Histogram).Graph;

        Assert.Empty(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, YRange(0, 10), YTicks(Interval(2.5))));
        var marked = graph.WithAxisScale(YRange(0, 10), YTicks(Interval(2.5))).Frame.YAxis;

        Assert.Equal(["0.0", "2.5", "5.0", "7.5", "10.0"], Labels(marked));
        Assert.Equal([0, 2.5, 5, 7.5, 10], marked.Ticks.Select(tick => tick.Value));
        Assert.Equal((GraphAxisScale.Count, new GraphAxisRange(0, 10)), (marked.Scale, marked.Range));

        // A whole-number interval reads in whole numbers, as before.
        Assert.Equal(["0", "5", "10", "15", "20"], Labels(graph.WithAxisScale(YRange(0, 20), YTicks(Interval(5))).Frame.YAxis));
    }

    [Fact]
    public void ACountAxisTakesDecimalValues()
    {
        var graph = Present(GraphType.Histogram).Graph;
        var values = Values(0, 2.5, 7.5, 12);

        Assert.Empty(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, YRange(0, 20), YTicks(values)));
        var marked = graph.WithAxisScale(YRange(0, 20), YTicks(values)).Frame.YAxis;

        Assert.Equal(["0.0", "2.5", "7.5", "12.0"], Labels(marked));
        Assert.Equal([0, 2.5, 7.5, 12], marked.Ticks.Select(tick => tick.Value));
        Assert.Equal(["0", "7", "12"], Labels(graph.WithAxisScale(YRange(0, 20), YTicks(Values(12, 0, 7))).Frame.YAxis));
    }

    [Fact]
    public void ACountAxisStillRefusesTicksBelowZeroAndIntervalsNotAboveZero()
    {
        var graph = Present(GraphType.Histogram).Graph;
        string Problem(GraphAxisTickOption option) =>
            Assert.Single(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, GraphAxisRangeOptions.Default, YTicks(option)));

        Assert.Equal("The Y-axis tick values cannot be below 0 (-2.5 is).", Problem(Values(-2.5, 0, 2.5)));
        Assert.Equal("The Y-axis tick interval must be a number above 0.", Problem(Interval(0)));
        Assert.Equal("The Y-axis tick interval must be a number above 0.", Problem(Interval(-2.5)));
    }

    [Fact]
    public void AHistogramsPercentAxisTakesAnyNonNegativeTicks()
    {
        var graph = Present(GraphType.Histogram, histogram: HistogramOptions.Default with { YScale = HistogramYScale.Percent }).Graph;

        Assert.NotEqual(GraphAxisScale.Count, graph.Frame.YAxis.Scale);
        Assert.Equal(["0.0", "2.5", "5.0"], Labels(graph.WithAxisScale(YRange(0, 5), YTicks(Interval(2.5))).Frame.YAxis));
        Assert.Equal(
            "The Y-axis tick values cannot be below 0 (-1 is).",
            Assert.Single(GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, GraphAxisRangeOptions.Default, YTicks(Values(-1, 2)))));
    }

    [Fact]
    public void AHistogramsBinsDoNotFollowItsTicks()
    {
        var presented = Present(GraphType.Histogram);

        var marked = presented.Graph.WithAxisScale(GraphAxisRangeOptions.Default, XTicks(Interval(0.01)));

        Assert.Same(presented.Graph.BaseFrame, marked.BaseFrame);
        Assert.Equal(presented.Graph.Frame.XAxis.Range, marked.Frame.XAxis.Range);
    }

    // ---- An axis without a range to choose ----

    [Fact]
    public void ABoxPlotsCategoriesAreNeverMarked()
    {
        var graph = Present(GraphType.BoxPlot).Graph;
        Assert.False(graph.Definition.SupportsAxisRange(GraphAxisField.X));

        var marked = graph.WithAxisScale(GraphAxisRangeOptions.Default, new GraphAxisTickOptions(Values(1, 2), Interval(0.05)));

        Assert.Equal(Labels(graph.Frame.XAxis), Labels(marked.Frame.XAxis));
        Assert.NotEqual(Labels(graph.Frame.YAxis), Labels(marked.Frame.YAxis));
    }

    // ---- Whatever is accepted can be drawn ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TicksTheRulesAcceptAreDrawnOnTheRangeAndNoMoreThanAHundred(GraphType type)
    {
        var graph = Present(type).Graph;
        GraphAxisTickOption[] options =
        [
            Interval(1e-9), Interval(1e-3), Interval(0.01), Interval(0.05), Interval(0.5), Interval(1), Interval(5), Interval(10),
            Interval(1e6), Values(0), Values(14.9, 15, 15.1), Values(0.0001, 50, 99.9999), Values(-1e300, 1e300),
            Values([.. Enumerable.Range(0, 100).Select(i => i * 0.01 + 14.5)])
        ];

        foreach (var field in new[] { GraphAxisField.X, GraphAxisField.Y })
        {
            foreach (var option in options)
            {
                var ticks = GraphAxisTickOptions.Default.With(field, option);
                // What the rules refuse is refused; an interval too dense for the range is thinned (Task #055).
                var problems = GraphAxisTickBuilder.Problems(graph.BaseFrame, graph.Definition, GraphAxisRangeOptions.Default, ticks);
                if (problems.Any(problem => !problem.Contains("would draw more than", StringComparison.Ordinal)))
                {
                    Assert.Throws<ArgumentException>(() => graph.WithAxisScale(GraphAxisRangeOptions.Default, ticks));
                    continue;
                }

                var axis = field == GraphAxisField.X ? graph.WithAxisScale(GraphAxisRangeOptions.Default, ticks).Frame.XAxis : graph.WithAxisScale(GraphAxisRangeOptions.Default, ticks).Frame.YAxis;
                var slack = axis.Range.Span * 1e-6;
                Assert.True(axis.Ticks.Count <= 100, $"{type} {field} {option}: {axis.Ticks.Count} ticks");
                Assert.All(axis.Ticks, tick => Assert.InRange(tick.Value, axis.Range.Minimum - slack, axis.Range.Maximum + slack));
                SkiaGraphRenderer.Layout(graph.WithAxisScale(GraphAxisRangeOptions.Default, ticks).Frame, Canvas, GraphThemes.Light);
            }
        }
    }

    // ---- Kept apart from the ranges and through every other edit ----

    [Fact]
    public void RangesAndTicksAreKeptApart()
    {
        var ticks = new GraphAxisTickOptions(Interval(0.05), Values(14.8, 15.0));
        var ranges = XRange(14.85, 15.15);
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(ranges, ticks);

        var ranged = graph.WithAxisRanges(XRange(14.8, 15.2));
        Assert.Equal(ticks, ranged.AxisTickOptions);
        Assert.Equal(["14.80", "14.85", "14.90"], Labels(ranged.Frame.XAxis)[..3]);

        var autoRanges = graph.WithAxisRanges(GraphAxisRangeOptions.Default);
        Assert.Equal(ticks, autoRanges.AxisTickOptions);

        var autoTicks = graph.WithAxisScale(ranges, GraphAxisTickOptions.Default);
        Assert.Equal(ranges, autoTicks.AxisRangeOptions);
        Assert.Equal(new GraphAxisRange(14.85, 15.15), autoTicks.Frame.XAxis.Range);
    }

    [Fact]
    public void TheTicksAreKeptThroughTheGraphsOtherEdits()
    {
        var edited = Present(GraphType.Histogram).Graph.WithAxisScale(XRange(14.85, 15.15), new GraphAxisTickOptions(Interval(0.05), Values(0, 10)));
        var x = Labels(edited.Frame.XAxis);
        var y = Labels(edited.Frame.YAxis);

        foreach (var (what, graph) in new[]
                 {
                     ("labels", edited.WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("T"), GraphLabelOption.Custom("X"), GraphLabelOption.Hidden))),
                     ("legend", edited.WithLegend(edited.LegendOptions with { Position = GraphLegendPosition.Bottom })),
                     ("statistics", edited.WithStatistics(edited.StatisticsOptions with { Mode = GraphStatisticsMode.Hide })),
                     ("appearance", edited.WithAppearance(edited.AppearanceOptions with { GridMode = GraphGridMode.Hide }))
                 })
        {
            Assert.True(graph.AxisTickOptions == edited.AxisTickOptions, $"{what} kept the ticks");
            Assert.True(graph.AxisRangeOptions == edited.AxisRangeOptions, $"{what} kept the ranges");
            Assert.True(Labels(graph.Frame.XAxis).SequenceEqual(x), $"{what} kept the X ticks");
            Assert.True(Labels(graph.Frame.YAxis).SequenceEqual(y), $"{what} kept the Y ticks");
        }
    }

    [Fact]
    public void CopiesAndExportsDrawTheTicksAsShownAtAnySizeAndTheme()
    {
        var presented = Present(GraphType.ScatterPlot);
        var marked = presented.Graph.WithAxisScale(GraphAxisRangeOptions.Default, XTicks(Values(14.9, 15.0, 15.1)));

        Assert.NotEqual(Png(presented.Graph.Frame, presented.Plot), Png(marked.Frame, presented.Plot));
        Assert.Equal(Png(marked.Frame, presented.Plot), Png(presented.Graph.WithAxisScale(GraphAxisRangeOptions.Default, marked.AxisTickOptions).Frame, presented.Plot));

        // A resize or another theme lays the same frame out again: its ticks are the frame's, not the size's.
        foreach (var canvas in new[] { new SKRect(0, 0, 400, 300), new SKRect(0, 0, 1400, 900) })
        {
            var layout = SkiaGraphRenderer.Layout(marked.Frame, canvas, GraphThemes.Dark);
            Assert.True(layout.PlotArea.Width > 0);
            Assert.Equal(["14.9", "15.0", "15.1"], Labels(marked.Frame.XAxis));
        }
    }

    // ---- The controller ----

    private sealed class TickDialog(GraphAxisScaleEdit? answer) : IGraphAxisScaleDialog
    {
        public GraphAxisTickOptions? Shown { get; private set; }

        public Task<GraphAxisScaleEdit?> EditAsync(GraphTypeDefinition definition, GraphAxisField axis, GraphAxisRangeOptions currentRanges, GraphAxisTickOptions currentTicks, GraphRenderModel autoFrame)
        {
            Shown = currentTicks;
            return Task.FromResult(answer);
        }
    }

    private sealed class RangesDialog(GraphAxisRangeOptions answer) : IGraphAxesDialog
    {
        public GraphAxisTickOptions? Shown { get; private set; }

        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame, GraphAxisTickOptions ticks)
        {
            Shown = ticks;
            return Task.FromResult<GraphAxisRangeOptions?>(answer);
        }
    }

    [Fact]
    public async Task TheScaleDialogSetsItsAxissRangeAndTicksAndKeepsTheOtherAxiss()
    {
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisScale(
            new GraphAxisRangeOptions(GraphAxisRangeOption.Auto, new GraphAxisRangeOption(14.7, 15.1)),
            YTicks(Values(14.8, 15.0)));
        var dialog = new TickDialog(new GraphAxisScaleEdit(new GraphAxisRangeOption(14.9, 15.1), Interval(0.05)));
        var controller = new GraphAxesEditController(start, new RangesDialog(GraphAxisRangeOptions.Default), dialog);

        Assert.True(await controller.EditScaleAsync(GraphAxisField.X));

        Assert.Equal(start.AxisTickOptions, dialog.Shown);
        Assert.Equal(new GraphAxisTickOptions(Interval(0.05), Values(14.8, 15.0)), controller.Graph.AxisTickOptions);
        Assert.Equal(new GraphAxisRangeOption(14.9, 15.1), controller.Graph.AxisRangeOptions.X);
        Assert.Equal(new GraphAxisRangeOption(14.7, 15.1), controller.Graph.AxisRangeOptions.Y);
        Assert.Equal(["14.90", "14.95", "15.00", "15.05", "15.10"], Labels(controller.Graph.Frame.XAxis));
    }

    [Fact]
    public async Task TicksTheGraphCannotDrawAreNotApplied()
    {
        var start = Present(GraphType.ScatterPlot).Graph;
        var controller = new GraphAxesEditController(
            start,
            new RangesDialog(GraphAxisRangeOptions.Default),
            new TickDialog(new GraphAxisScaleEdit(new GraphAxisRangeOption(0, 1000), Interval(1))));

        Assert.False(await controller.EditScaleAsync(GraphAxisField.X));
        Assert.Same(start, controller.Graph);
    }

    [Fact]
    public async Task EditingTheRangesKeepsTheTicks()
    {
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisScale(GraphAxisRangeOptions.Default, XTicks(Interval(0.05)));
        var dialog = new RangesDialog(XRange(14.8, 15.2));
        var controller = new GraphAxesEditController(start, dialog, new TickDialog(null));

        Assert.True(await controller.EditAsync());

        Assert.Equal(start.AxisTickOptions, dialog.Shown);
        Assert.Equal(start.AxisTickOptions, controller.Graph.AxisTickOptions);
        Assert.Equal("14.80", controller.Graph.Frame.XAxis.Ticks[0].Label);
    }

    [Fact]
    public async Task ARangeTheTickIntervalCannotMarkIsNotApplied()
    {
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisScale(GraphAxisRangeOptions.Default, XTicks(Interval(0.05)));
        var controller = new GraphAxesEditController(start, new RangesDialog(XRange(0, 100)), new TickDialog(null));

        Assert.False(await controller.EditAsync());
        Assert.Same(start, controller.Graph);
    }

    // ---- The dialogs ----

    private static GraphAxisScaleEditorViewModel Dialog(GraphType type, GraphAxisField axis, GraphAxisRangeOptions? ranges = null, GraphAxisTickOptions? ticks = null)
    {
        var graph = Present(type).Graph;
        return new GraphAxisScaleEditorViewModel(graph.Definition, axis, ranges ?? GraphAxisRangeOptions.Default, graph.BaseFrame, ticks);
    }

    [Fact]
    public void TheDialogStartsOnAutoTicksWithTheShownTicksToEditFrom()
    {
        var shown = Present(GraphType.ScatterPlot).Graph.WithAxisRanges(XRange(14.9, 15.1)).Frame.XAxis;
        var dialog = Dialog(GraphType.ScatterPlot, GraphAxisField.X, XRange(14.9, 15.1));

        Assert.Equal(GraphAxisTickMode.Auto, dialog.TickMode);
        Assert.True(dialog.TicksAreAuto);
        Assert.Same(GraphAxisTickOption.Auto, dialog.TickOption);
        Assert.Equal(string.Join(", ", Labels(shown)), dialog.ValuesText);
        Assert.Equal("0.05", dialog.IntervalText);
        Assert.Null(dialog.TickNotice);
        Assert.Equal(new GraphAxisScaleEdit(new GraphAxisRangeOption(14.9, 15.1), GraphAxisTickOption.Auto), dialog.Edit);
    }

    [Fact]
    public void TheDialogStartsFromTheTicksTheAxisHas()
    {
        var interval = Dialog(GraphType.ScatterPlot, GraphAxisField.X, ticks: XTicks(Interval(0.02)));
        Assert.Equal((GraphAxisTickMode.Interval, "0.02"), (interval.TickMode, interval.IntervalText));

        var values = Dialog(GraphType.ScatterPlot, GraphAxisField.Y, ticks: YTicks(Values(15.1, 14.9)));
        Assert.Equal((GraphAxisTickMode.Custom, "14.9, 15.1"), (values.TickMode, values.ValuesText));
        Assert.True(values.TicksAreCustom);
    }

    [Fact]
    public void AProbabilityAxisStartsWithNoIntervalAndItsPercents()
    {
        var dialog = Dialog(GraphType.ProbabilityPlot, GraphAxisField.Y);

        Assert.Equal(string.Empty, dialog.IntervalText);
        Assert.StartsWith("0.1, 0.5, 1, 2, 5, 10", dialog.ValuesText);
        Assert.Equal("%", dialog.Unit);
    }

    [Fact]
    public void AnIntervalIsTypedAndChecked()
    {
        var dialog = Dialog(GraphType.ScatterPlot, GraphAxisField.X);
        dialog.TicksAreInterval = true;

        dialog.IntervalText = "0.02";
        Assert.Equal(new GraphAxisTickOption.FixedInterval(0.02), dialog.Edit?.Ticks);

        dialog.IntervalText = "fine";
        Assert.Equal("The X-axis tick interval must be a number.", dialog.ValidationMessage);
        Assert.Null(dialog.Edit);

        dialog.IntervalText = "0";
        Assert.Equal("The X-axis tick interval must be a number above 0.", dialog.ValidationMessage);

        dialog.IntervalText = "0.0001";
        Assert.Equal("The X-axis tick interval 0.0001 would draw more than 100 ticks over the range shown. Enter a larger interval.", dialog.ValidationMessage);
        Assert.False(dialog.IsValid);
    }

    [Fact]
    public void ValuesAreTypedInAnyOrderWithAnySeparator()
    {
        var dialog = Dialog(GraphType.ScatterPlot, GraphAxisField.X);
        dialog.TicksAreCustom = true;

        dialog.ValuesText = "15.1; 14.9 15.0,14.95, 15.0";
        Assert.Equal(Values(14.9, 14.95, 15.0, 15.1), dialog.Edit?.Ticks);

        dialog.ValuesText = "14.9, abc";
        Assert.Equal("The X-axis tick value \"abc\" is not a number.", dialog.ValidationMessage);

        dialog.ValuesText = " , ";
        Assert.Equal("Enter at least one X-axis tick value.", dialog.ValidationMessage);
    }

    [Fact]
    public void ValuesOffTheRangeAreSaidButNotRefused()
    {
        var dialog = Dialog(GraphType.ScatterPlot, GraphAxisField.X, XRange(14.85, 15.15));
        dialog.TicksAreCustom = true;
        dialog.ValuesText = "0, 14.9, 15, 100";

        Assert.True(dialog.IsValid);
        Assert.Equal("2 of 4 tick values lie outside the range shown. They are kept, and drawn when the range reaches them.", dialog.TickNotice);

        // The notice follows the range the dialog describes.
        dialog.MinimumIsAuto = false;
        dialog.MinimumText = "-1";
        Assert.Equal("1 of 4 tick values lie outside the range shown. They are kept, and drawn when the range reaches them.", dialog.TickNotice);
    }

    [Fact]
    public void ARangeProblemIsSaidBeforeATickProblem()
    {
        var dialog = Dialog(GraphType.ScatterPlot, GraphAxisField.X);
        dialog.TicksAreInterval = true;
        dialog.IntervalText = "x";
        dialog.MinimumIsAuto = false;
        dialog.MinimumText = "y";

        Assert.Equal("X-axis minimum must be a number.", dialog.ValidationMessage);
    }

    [Fact]
    public void ACountAxisDialogTakesDecimalsButNothingBelowZero()
    {
        var dialog = Dialog(GraphType.Histogram, GraphAxisField.Y);
        dialog.TicksAreInterval = true;
        dialog.IntervalText = "2.5";
        Assert.True(dialog.IsValid);
        Assert.Equal(new GraphAxisTickOption.FixedInterval(2.5), dialog.Edit?.Ticks);

        dialog.TicksAreCustom = true;
        dialog.ValuesText = "0, 2.5, 7.5, 12";
        Assert.True(dialog.IsValid);
        Assert.Equal(Values(0, 2.5, 7.5, 12), dialog.Edit?.Ticks);

        dialog.ValuesText = "-2.5, 2.5";
        Assert.Equal("The Y-axis tick values cannot be below 0 (-2.5 is).", dialog.ValidationMessage);
    }

    [Fact]
    public void ResetToAutoResetsTheRangeAndTheTicksInTheDialogOnly()
    {
        var dialog = Dialog(GraphType.ScatterPlot, GraphAxisField.X, XRange(14.85, 15.15), XTicks(Values(14.9, 15)));

        dialog.ResetToAuto();

        Assert.Equal(new GraphAxisScaleEdit(GraphAxisRangeOption.Auto, GraphAxisTickOption.Auto), dialog.Edit);
        Assert.Equal("14.9, 15", dialog.ValuesText);
    }

    [Fact]
    public void TheEditAxesDialogRefusesARangeTheTickIntervalCannotMark()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;
        var dialog = new GraphAxesEditorViewModel(graph.Definition, GraphAxisRangeOptions.Default, graph.BaseFrame, XTicks(Interval(0.05)))
        {
            XMinimumText = "0",
            XMaximumText = "100"
        };

        Assert.Equal("The X-axis tick interval 0.05 would draw more than 100 ticks over the range shown. Enter a larger interval.", dialog.ValidationMessage);

        dialog.XMinimumText = "14";
        dialog.XMaximumText = "15.5";
        Assert.True(dialog.IsValid);
    }
}
