using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.app.Views;
using static YAT.App.Tests.GraphLabelEditingTests;

namespace YAT.App.Tests;

// Zooming and panning a drawn graph (Task #055): the arithmetic (GraphViewNavigator), the view it gives
// (GraphViewController, GraphViewBuilder) between the configured ranges and the ticks, Reset View, and what the view never
// touches - the configured ranges, the stored ticks, the data - while it is shown, copied and exported like everything
// else. An interval too dense for a zoomed-out view is thinned on its own grid, never replaced.
public class GraphViewTests
{
    private static readonly SKRect Plot = new(100, 50, 600, 350);

    private static readonly SKRect Canvas = new(0, 0, 760, 488);

    private static GraphAxisRange Range(double minimum, double maximum) => new(minimum, maximum);

    private static GraphAxisRangeOptions XRange(double minimum, double maximum) =>
        GraphAxisRangeOptions.Default with { X = new GraphAxisRangeOption(minimum, maximum) };

    private static GraphAxisTickOptions XTicks(GraphAxisTickOption option) => GraphAxisTickOptions.Default with { X = option };

    private static GraphViewLimits Unlimited(double span = 1) =>
        new(span * 1e-6, span * 1e3, double.NegativeInfinity, double.PositiveInfinity);

    private static byte[] Png(GraphRenderModel frame, IGraphPlotRenderer plot) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, GraphThemes.Light));

    private static string[] Labels(GraphAxisModel axis) => [.. axis.Ticks.Select(tick => tick.Label)];

    private static GraphAxisRange Shown(GraphPresentationState graph, GraphAxisField axis) =>
        axis == GraphAxisField.X ? graph.Frame.XAxis.Range : graph.Frame.YAxis.Range;

    // ---- The arithmetic ----

    [Fact]
    public void AWheelStepZoomsByOnePointTwoAndAFractionByItsShare()
    {
        Assert.Equal(1 / 1.2, GraphViewNavigator.ZoomScale(1), 15);
        Assert.Equal(1.2, GraphViewNavigator.ZoomScale(-1), 15);
        Assert.Equal(1 / 1.44, GraphViewNavigator.ZoomScale(2), 15);
        Assert.Equal(Math.Pow(1.2, -0.25), GraphViewNavigator.ZoomScale(0.25), 15);
        Assert.Equal(GraphViewNavigator.ZoomScale(1), GraphViewNavigator.ZoomScale(0.5) * GraphViewNavigator.ZoomScale(0.5), 15);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    [InlineData(1.0)]
    public void ZoomKeepsTheValueUnderTheCursorWhereItIs(double fraction)
    {
        var range = Range(14.9, 15.1);
        var point = new SKPoint(Plot.Left + (float)(fraction * Plot.Width), Plot.Bottom - (float)(fraction * Plot.Height));

        foreach (var axis in new[] { GraphAxisField.X, GraphAxisField.Y })
        {
            var anchor = GraphViewNavigator.ValueAt(axis, range, Plot, point);
            var zoomed = GraphViewNavigator.Zoom(range, anchor, GraphViewNavigator.ZoomScale(1), Unlimited());

            Assert.Equal(range.Span / 1.2, zoomed.Span, 12);
            Assert.Equal(anchor, GraphViewNavigator.ValueAt(axis, zoomed, Plot, point), 12);
        }
    }

    [Fact]
    public void ZoomingInAndOutAgainReturnsToTheSameRange()
    {
        var range = Range(-3, 7);
        var anchor = 1.234;

        var back = GraphViewNavigator.Zoom(GraphViewNavigator.Zoom(range, anchor, 1 / 1.2, Unlimited(10)), anchor, 1.2, Unlimited(10));

        Assert.Equal(range.Minimum, back.Minimum, 12);
        Assert.Equal(range.Maximum, back.Maximum, 12);
    }

    [Fact]
    public void TheValueAtAPointIsMeasuredFromTheLeftAndTheBottomOfThePlot()
    {
        var range = Range(10, 20);

        Assert.Equal(10, GraphViewNavigator.ValueAt(GraphAxisField.X, range, Plot, new SKPoint(Plot.Left, 0)), 12);
        Assert.Equal(15, GraphViewNavigator.ValueAt(GraphAxisField.X, range, Plot, new SKPoint(Plot.MidX, 0)), 12);
        Assert.Equal(10, GraphViewNavigator.ValueAt(GraphAxisField.Y, range, Plot, new SKPoint(0, Plot.Bottom)), 12);
        Assert.Equal(20, GraphViewNavigator.ValueAt(GraphAxisField.Y, range, Plot, new SKPoint(0, Plot.Top)), 12);
    }

    [Fact]
    public void ZoomIsClampedToTheSpanLimitsAboutTheSameAnchor()
    {
        var range = Range(0, 10);
        var limits = GraphViewNavigator.Limits(GraphAxisKind.Numeric, range);
        Assert.Equal(1e-5, limits.MinimumSpan, 15);
        Assert.Equal(1e4, limits.MaximumSpan, 9);

        var narrowest = GraphViewNavigator.Zoom(range, 5, 1e-12, limits);
        Assert.Equal(1e-5, narrowest.Span, 15);
        Assert.Equal(5, narrowest.Minimum + (narrowest.Span / 2), 12);

        var widest = GraphViewNavigator.Zoom(range, 2.5, 1e12, limits);
        Assert.Equal(1e4, widest.Span, 6);
        Assert.Equal(0.25, (2.5 - widest.Minimum) / widest.Span, 12);
    }

    [Fact]
    public void ARangeAlreadyPastTheLimitsIsNeverForcedToThemTheOtherWay()
    {
        var limits = GraphViewNavigator.Limits(GraphAxisKind.Numeric, Range(0, 10));

        // Wider than the widest allowed: zooming in still narrows it, zooming out keeps it.
        var wide = Range(0, 1e6);
        Assert.True(GraphViewNavigator.Zoom(wide, 0, 1 / 1.2, limits).Span < wide.Span);
        Assert.Equal(wide.Span, GraphViewNavigator.Zoom(wide, 0, 1.2, limits).Span, 6);
    }

    [Fact]
    public void AxesThatCannotGoBelowZeroOrPast100StayInside()
    {
        var count = GraphViewNavigator.Limits(GraphAxisKind.NonNegative, Range(0, 40));
        var zoomedOut = GraphViewNavigator.Zoom(Range(0, 40), 10, 3, count);
        Assert.Equal(0, zoomedOut.Minimum);
        Assert.Equal(120, zoomedOut.Span, 9);
        Assert.Equal(0, GraphViewNavigator.Pan(Range(10, 30), -50, count).Minimum);

        var percent = GraphViewNavigator.Limits(GraphAxisKind.Percent, Range(0, 100));
        Assert.Equal(Range(0, 100), GraphViewNavigator.Zoom(Range(0, 100), 30, 5, percent));
        Assert.Equal(Range(80, 100), GraphViewNavigator.Pan(Range(40, 60), 1000, percent));
        Assert.Equal(Range(0, 20), GraphViewNavigator.Pan(Range(40, 60), -1000, percent));
    }

    [Fact]
    public void AProbabilityAxisIsNavigatedInScoresWithinItsPercentages()
    {
        var lowest = ProbabilityAxis.Score(GraphAxisRangeRules.MinimumProbabilityPercent);
        var highest = ProbabilityAxis.Score(GraphAxisRangeRules.MaximumProbabilityPercent);

        var limits = GraphViewNavigator.Limits(GraphAxisKind.ProbabilityPercent, Range(-3.2, 3.2));
        Assert.Equal((lowest, highest), (limits.Lowest, limits.Highest));
        Assert.Equal(lowest, GraphViewNavigator.Pan(Range(-1, 1), -100, limits).Minimum, 12);

        // An automatic range padded past the farthest percentages keeps its own ends.
        var padded = GraphViewNavigator.Limits(GraphAxisKind.ProbabilityPercent, Range(-5.2, 5.1));
        Assert.Equal((-5.2, 5.1), (padded.Lowest, padded.Highest));
    }

    [Fact]
    public void APanKeepsTheWidthAndFollowsThePointer()
    {
        var start = Range(14.9, 15.1);

        var right = GraphViewNavigator.Shift(GraphAxisField.X, start, Plot, new SKPoint(300, 200), new SKPoint(350, 200));
        Assert.Equal(-0.02, right, 12);
        var down = GraphViewNavigator.Shift(GraphAxisField.Y, start, Plot, new SKPoint(300, 200), new SKPoint(300, 230));
        Assert.Equal(0.02, down, 12);

        var panned = GraphViewNavigator.Pan(start, right, Unlimited());
        Assert.Equal(start.Span, panned.Span, 14);
        Assert.Equal(14.88, panned.Minimum, 12);
    }

    [Theory]
    [InlineData(2.9f, 0f, false)]
    [InlineData(0f, -2.9f, false)]
    [InlineData(3f, 0f, true)]
    [InlineData(0f, -3f, true)]
    [InlineData(2f, 2f, false)]
    public void APressBecomesAPanOnceItMovesThreePixels(float dx, float dy, bool drag) =>
        Assert.Equal(drag, GraphViewNavigator.IsDrag(new SKPoint(200, 200), new SKPoint(200 + dx, 200 + dy)));

    // ---- No view: the graph exactly as it was ----

    [Theory]
    [MemberData(nameof(GraphAxisTickTests.GraphTypes), MemberType = typeof(GraphAxisTickTests))]
    public void NoViewIsTheGraphExactlyAsItWas(GraphType type)
    {
        var graph = Present(type).Graph;

        Assert.Equal(GraphViewOptions.Default, graph.ViewOptions);
        Assert.Same(graph.ConfiguredFrame, GraphViewBuilder.Attach(graph.ConfiguredFrame, graph.Definition, GraphViewOptions.Default));
        Assert.False(new GraphViewController(graph).Reset());
    }

    // ---- Zoom ----

    [Fact]
    public void AWheelStepInThePlotZoomsBothAxesAboutTheCursor()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;
        var controller = new GraphViewController(graph);
        var point = new SKPoint(250, 120);
        var anchorX = GraphViewNavigator.ValueAt(GraphAxisField.X, Shown(graph, GraphAxisField.X), Plot, point);
        var anchorY = GraphViewNavigator.ValueAt(GraphAxisField.Y, Shown(graph, GraphAxisField.Y), Plot, point);

        Assert.True(controller.ZoomAt(null, Plot, point, 1));

        var zoomed = controller.Graph;
        Assert.Equal(Shown(graph, GraphAxisField.X).Span / 1.2, Shown(zoomed, GraphAxisField.X).Span, 12);
        Assert.Equal(Shown(graph, GraphAxisField.Y).Span / 1.2, Shown(zoomed, GraphAxisField.Y).Span, 12);
        Assert.Equal(anchorX, GraphViewNavigator.ValueAt(GraphAxisField.X, Shown(zoomed, GraphAxisField.X), Plot, point), 12);
        Assert.Equal(anchorY, GraphViewNavigator.ValueAt(GraphAxisField.Y, Shown(zoomed, GraphAxisField.Y), Plot, point), 12);
    }

    [Fact]
    public void AWheelStepOverAnAxisZoomsThatAxisAlone()
    {
        var presented = Present(GraphType.ScatterPlot);
        var controller = new GraphViewController(presented.Graph);

        Assert.True(controller.ZoomAt(GraphAxisField.X, Plot, new SKPoint(300, 400), -1));

        Assert.Equal(Shown(presented.Graph, GraphAxisField.X).Span * 1.2, Shown(controller.Graph, GraphAxisField.X).Span, 12);
        Assert.Null(controller.Graph.ViewOptions.Y);
        Assert.Equal(presented.Graph.Frame.YAxis, controller.Graph.Frame.YAxis);
    }

    [Fact]
    public void FractionalWheelStepsAddUpToAWholeOne()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;
        var whole = new GraphViewController(graph);
        var halves = new GraphViewController(graph);
        var point = new SKPoint(420, 260);

        whole.ZoomAt(null, Plot, point, 1);
        halves.ZoomAt(null, Plot, point, 0.5);
        halves.ZoomAt(null, Plot, point, 0.5);

        foreach (var axis in new[] { GraphAxisField.X, GraphAxisField.Y })
        {
            Assert.Equal(Shown(whole.Graph, axis).Minimum, Shown(halves.Graph, axis).Minimum, 12);
            Assert.Equal(Shown(whole.Graph, axis).Maximum, Shown(halves.Graph, axis).Maximum, 12);
        }
    }

    [Fact]
    public void ABoxPlotsCategoriesNeverMove()
    {
        var presented = Present(GraphType.BoxPlot);
        var controller = new GraphViewController(presented.Graph);

        Assert.False(controller.ZoomAt(GraphAxisField.X, Plot, new SKPoint(300, 200), 1));
        Assert.True(controller.ZoomAt(null, Plot, new SKPoint(300, 200), 1));
        controller.BeginPan(Plot, new SKPoint(300, 200));
        Assert.True(controller.PanTo(new SKPoint(360, 240)));

        Assert.Null(controller.Graph.ViewOptions.X);
        Assert.NotNull(controller.Graph.ViewOptions.Y);
        Assert.Equal(presented.Graph.Frame.XAxis, controller.Graph.Frame.XAxis);
        Assert.Equal(Labels(presented.Graph.Frame.XAxis), Labels(controller.Graph.Frame.XAxis));
    }

    [Fact]
    public void ANonsenseWheelDeltaOrPlotDoesNothing()
    {
        var controller = new GraphViewController(Present(GraphType.ScatterPlot).Graph);

        Assert.False(controller.ZoomAt(null, Plot, new SKPoint(300, 200), 0));
        Assert.False(controller.ZoomAt(null, Plot, new SKPoint(300, 200), double.NaN));
        Assert.False(controller.ZoomAt(null, SKRect.Empty, new SKPoint(300, 200), 1));
        Assert.True(controller.Graph.ViewOptions.IsDefault);
    }

    [Fact]
    public void AnAxisZoomedAsFarAsItGoesStopsChanging()
    {
        var controller = new GraphViewController(Present(GraphType.ScatterPlot).Graph);
        var reference = controller.Graph.ConfiguredFrame.XAxis.Range.Span;

        for (var step = 0; step < 200; step++)
        {
            controller.ZoomAt(GraphAxisField.X, Plot, new SKPoint(300, 400), 1);
        }

        Assert.Equal(1, Shown(controller.Graph, GraphAxisField.X).Span / (reference * GraphViewNavigator.MinimumSpanRatio), 6);
        Assert.False(controller.ZoomAt(GraphAxisField.X, Plot, new SKPoint(300, 400), 1));
    }

    // ---- Pan ----

    [Fact]
    public void APanIsWorkedOutFromWhereTheDragBeganAndKeepsTheWidths()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;
        var stepwise = new GraphViewController(graph);
        var direct = new GraphViewController(graph);
        var from = new SKPoint(300, 200);

        stepwise.BeginPan(Plot, from);
        foreach (var x in Enumerable.Range(1, 40))
        {
            stepwise.PanTo(new SKPoint(300 + (x * 1.37f), 200 + (x * 0.61f)));
        }

        direct.BeginPan(Plot, from);
        direct.PanTo(new SKPoint(300 + (40 * 1.37f), 200 + (40 * 0.61f)));

        Assert.Equal(direct.Graph.ViewOptions, stepwise.Graph.ViewOptions);
        foreach (var axis in new[] { GraphAxisField.X, GraphAxisField.Y })
        {
            Assert.Equal(Shown(graph, axis).Span, Shown(direct.Graph, axis).Span, 12);
        }

        Assert.True(Shown(direct.Graph, GraphAxisField.X).Minimum < Shown(graph, GraphAxisField.X).Minimum);
        Assert.True(Shown(direct.Graph, GraphAxisField.Y).Minimum > Shown(graph, GraphAxisField.Y).Minimum);
    }

    [Fact]
    public void APanNeedsADragAndEndsWithIt()
    {
        var controller = new GraphViewController(Present(GraphType.ScatterPlot).Graph);
        Assert.False(controller.IsPanning);
        Assert.False(controller.PanTo(new SKPoint(400, 300)));

        controller.BeginPan(Plot, new SKPoint(300, 200));
        Assert.True(controller.IsPanning);
        Assert.True(controller.PanTo(new SKPoint(400, 300)));
        var panned = controller.Graph.ViewOptions;

        controller.EndPan();
        Assert.False(controller.IsPanning);
        Assert.False(controller.PanTo(new SKPoint(500, 300)));
        Assert.Equal(panned, controller.Graph.ViewOptions);
    }

    [Fact]
    public void ANumericAxisPansPastItsData()
    {
        var controller = new GraphViewController(Present(GraphType.ScatterPlot).Graph);
        var reference = controller.Graph.ConfiguredFrame.XAxis.Range;

        controller.BeginPan(Plot, new SKPoint(100, 200));
        controller.PanTo(new SKPoint(100 + (Plot.Width * 50), 200));

        Assert.Equal(reference.Minimum - (reference.Span * 50), Shown(controller.Graph, GraphAxisField.X).Minimum, 9);
    }

    // ---- Reset View ----

    [Fact]
    public void ResetViewReturnsToTheConfiguredRange()
    {
        var presented = Present(GraphType.ScatterPlot);
        var configured = presented.Graph.WithAxisRanges(XRange(14.85, 15.15));
        var controller = new GraphViewController(configured);
        controller.ZoomAt(null, Plot, new SKPoint(300, 200), 3);
        controller.BeginPan(Plot, new SKPoint(300, 200));
        controller.PanTo(new SKPoint(380, 260));

        Assert.True(controller.Reset());

        Assert.True(controller.Graph.ViewOptions.IsDefault);
        Assert.False(controller.IsPanning);
        Assert.Equal(XRange(14.85, 15.15), controller.Graph.AxisRangeOptions);
        Assert.Equal(new GraphAxisRange(14.85, 15.15), controller.Graph.Frame.XAxis.Range);
        Assert.Equal(Png(configured.Frame, presented.Plot), Png(controller.Graph.Frame, presented.Plot));
    }

    [Fact]
    public void ResetViewOnAnAutoGraphIsTheGraphAsItWas()
    {
        var presented = Present(GraphType.Histogram);
        var controller = new GraphViewController(presented.Graph);
        controller.ZoomAt(null, Plot, new SKPoint(300, 200), 2);
        Assert.NotEqual(Png(presented.Graph.Frame, presented.Plot), Png(controller.Graph.Frame, presented.Plot));

        controller.Reset();

        Assert.Equal(Png(presented.Graph.Frame, presented.Plot), Png(controller.Graph.Frame, presented.Plot));
    }

    // ---- What a view never touches ----

    [Fact]
    public void ZoomAndPanChangeNeitherTheConfiguredRangesNorTheTicks()
    {
        var ticks = new GraphAxisTickOptions(new GraphAxisTickOption.FixedInterval(0.05), new GraphAxisTickOption.CustomValues([14.8, 15.0]));
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.85, 15.15), ticks);
        var controller = new GraphViewController(start);

        controller.ZoomAt(null, Plot, new SKPoint(300, 200), -4);
        controller.BeginPan(Plot, new SKPoint(300, 200));
        controller.PanTo(new SKPoint(150, 320));
        controller.EndPan();
        Assert.Equal((start.AxisRangeOptions, ticks), (controller.Graph.AxisRangeOptions, controller.Graph.AxisTickOptions));
        Assert.Same(start.BaseFrame, controller.Graph.BaseFrame);

        controller.Reset();
        Assert.Equal((start.AxisRangeOptions, ticks), (controller.Graph.AxisRangeOptions, controller.Graph.AxisTickOptions));
        Assert.Equal(Labels(start.Frame.XAxis), Labels(controller.Graph.Frame.XAxis));
    }

    [Fact]
    public void ADenseIntervalIsThinnedOnItsOwnGridAndRestoredWhenZoomedBackIn()
    {
        var interval = new GraphAxisTickOption.FixedInterval(0.01);
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.9, 15.1), XTicks(interval));
        Assert.Equal(21, start.Frame.XAxis.Ticks.Count);
        var controller = new GraphViewController(start);

        for (var step = 0; step < 30; step++)
        {
            controller.ZoomAt(GraphAxisField.X, Plot, new SKPoint(350, 400), -1);
        }

        var axis = controller.Graph.Frame.XAxis;
        Assert.True(axis.Range.Span > 30, $"zoomed out to {axis.Range}");
        Assert.InRange(axis.Ticks.Count, 2, 100);
        Assert.Equal(XTicks(interval), controller.Graph.AxisTickOptions);

        // Every tick is a multiple of the interval, and of one 1-2-5 step of it, labelled as the interval is.
        var step0 = axis.Ticks[1].Value - axis.Ticks[0].Value;
        var every = Math.Round(step0 / 0.01);
        Assert.Contains(every, new[] { 1d, 2d, 5d, 10d, 20d, 50d, 100d, 200d, 500d, 1000d, 2000d, 5000d });
        Assert.All(axis.Ticks, tick => Assert.Equal(0, Math.Round(tick.Value / 0.01) % every));
        Assert.All(axis.Ticks, tick => Assert.Matches(@"^-?\d+\.\d\d$", tick.Label));

        controller.Reset();
        Assert.Equal(Labels(start.Frame.XAxis), Labels(controller.Graph.Frame.XAxis));
    }

    [Fact]
    public void ThinningTakesTheFewestSkippedMultiplesThatFit()
    {
        var graph = Present(GraphType.ScatterPlot).Graph;

        // 0.5 over 0..100 is 201 ticks; every 2nd is 101, still too many; every 5th - 2.5 apart - is 41.
        var thinned = graph.WithAxisScale(XRange(0, 100), XTicks(new GraphAxisTickOption.FixedInterval(0.5))).Frame.XAxis;
        Assert.Equal(41, thinned.Ticks.Count);
        Assert.Equal([0, 2.5, 5], thinned.Ticks.Take(3).Select(tick => tick.Value));
        Assert.Equal("2.5", thinned.Ticks[1].Label);

        // Over another view the step that fits is chosen again - here every 2nd, 1 apart, a hundred of them - and still on the
        // interval's own zero-anchored grid.
        var panned = graph.WithAxisRanges(XRange(0, 100)).WithAxisScale(XRange(0, 100), XTicks(new GraphAxisTickOption.FixedInterval(0.5)))
            .WithView(new GraphViewOptions(Range(1.3, 101.3), null)).Frame.XAxis;
        Assert.Equal(100, panned.Ticks.Count);
        Assert.Equal(2, panned.Ticks[0].Value, 12);
        Assert.All(panned.Ticks, tick => Assert.Equal(0, tick.Value % 1, 9));
    }

    // ---- Readable thinning on a navigated axis: at least 50 px apart ----

    private static readonly GraphAxisTickOption.FixedInterval Hundredth = new(0.01);

    private static GraphAxisModel ThinnedAxis(GraphAxisField axis, GraphAxisRange view, double width, double height, GraphType type = GraphType.ScatterPlot)
    {
        var graph = Present(type).Graph.WithAxisScale(GraphAxisRangeOptions.Default, GraphAxisTickOptions.Default.With(axis, Hundredth));
        var viewed = graph.WithView(new GraphViewOptions(null, null).With(axis, view) with { PlotWidth = width, PlotHeight = height });
        return axis == GraphAxisField.X ? viewed.Frame.XAxis : viewed.Frame.YAxis;
    }

    private static double Every(GraphAxisModel axis, double interval) =>
        Math.Round((axis.Ticks[1].Value - axis.Ticks[0].Value) / interval);

    private static void OnTheGrid(GraphAxisModel axis, double interval, double every) =>
        Assert.All(axis.Ticks, tick =>
        {
            var multiple = tick.Value / interval;
            Assert.Equal(Math.Round(multiple), multiple, 6);
            Assert.Equal(0, Math.Round(multiple) % every);
        });

    [Fact]
    public void AThinnedXAxisKeepsItsTicksFiftyPixelsApart()
    {
        // 0.01 over 14..16 is 201 ticks. By count alone every 5th would do (41 ticks, 19 px apart on 760 px); 50 px apart
        // takes every 20th - 0.2 apart, 76 px.
        var countOnly = ThinnedAxis(GraphAxisField.X, Range(14, 16), 0, 0);
        Assert.Equal(5, Every(countOnly, 0.01));

        var axis = ThinnedAxis(GraphAxisField.X, Range(14, 16), 760, 300);
        Assert.Equal(20, Every(axis, 0.01));
        Assert.Equal(11, axis.Ticks.Count);
        Assert.Equal("14.00", axis.Ticks[0].Label);
        Assert.Equal("14.20", axis.Ticks[1].Label);
        OnTheGrid(axis, 0.01, 20);
    }

    [Fact]
    public void AThinnedYAxisIsMeasuredOnThePlotsHeight()
    {
        // 300 px high: 50 px is a sixth of 14..16, so every 50th (0.5 apart, 75 px). The width does not matter to Y.
        var axis = ThinnedAxis(GraphAxisField.Y, Range(14, 16), 3000, 300);

        Assert.Equal(50, Every(axis, 0.01));
        Assert.Equal(["14.00", "14.50", "15.00", "15.50", "16.00"], Labels(axis));
        OnTheGrid(axis, 0.01, 50);
    }

    [Theory]
    [InlineData(1520, 10)]
    [InlineData(760, 20)]
    [InlineData(380, 50)]
    [InlineData(150, 100)]
    public void TheSizeOfThePlotChoosesTheStep(double width, double every)
    {
        var axis = ThinnedAxis(GraphAxisField.X, Range(14, 16), width, 300);

        Assert.Equal(every, Every(axis, 0.01));
        Assert.True((axis.Ticks[1].Value - axis.Ticks[0].Value) / 2 * width >= GraphAxisTickBuilder.MinimumTickSpacing - 1e-9);
        OnTheGrid(axis, 0.01, every);
    }

    [Fact]
    public void AHundredTicksStayTheLimitHoweverManyPixels()
    {
        // 1 over 0..1000 on a million pixels: 50 px apart at every 1st already, but 1001 ticks - every 20th is the first
        // step within a hundred.
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(GraphAxisRangeOptions.Default, XTicks(new GraphAxisTickOption.FixedInterval(1)));
        var axis = graph.WithView(new GraphViewOptions(Range(0, 1000), null) { PlotWidth = 1e6, PlotHeight = 1e6 }).Frame.XAxis;

        Assert.Equal(51, axis.Ticks.Count);
        Assert.Equal(20, Every(axis, 1));
        Assert.Equal(new GraphAxisTickOption.FixedInterval(1), graph.AxisTickOptions.X);
    }

    [Fact]
    public void OnlyANavigatedAxisIsThinnedForItsPixels()
    {
        // The configured range is the user's: 21 ticks of 0.01 over 14.9..15.1 stay, however few pixels the plot has.
        var graph = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.9, 15.1), XTicks(Hundredth));
        Assert.Equal(21, graph.Frame.XAxis.Ticks.Count);

        // A view on Y alone, measured at 760 px, leaves X's ticks as they were.
        var yOnly = graph.WithView(new GraphViewOptions(null, Range(14, 16)) { PlotWidth = 760, PlotHeight = 300 });
        Assert.Equal(Labels(graph.Frame.XAxis), Labels(yOnly.Frame.XAxis));
    }

    [Fact]
    public void AProbabilityAxisIsThinnedByWhereItsPercentagesSit()
    {
        var graph = Present(GraphType.ProbabilityPlot).Graph.WithAxisScale(
            GraphAxisRangeOptions.Default, GraphAxisTickOptions.Default with { Y = new GraphAxisTickOption.FixedInterval(1) });
        var view = Range(-2.5, 2.5);
        var axis = graph.WithView(new GraphViewOptions(null, view) { PlotWidth = 600, PlotHeight = 600 }).Frame.YAxis;

        // The percentages crowd towards 50: every 20th - 20, 40, 60, 80 - is the first step whose closest two (40 and 60)
        // are 50 px apart on 600 px.
        Assert.Equal(["20", "40", "60", "80"], Labels(axis));
        var closest = axis.Ticks.Zip(axis.Ticks.Skip(1), (a, b) => b.Value - a.Value).Min();
        Assert.True(closest / view.Span * 600 >= GraphAxisTickBuilder.MinimumTickSpacing - 1e-9, $"closest {closest / view.Span * 600:F1} px");
        Assert.All(axis.Ticks, tick => Assert.Equal(0, double.Parse(tick.Label, System.Globalization.CultureInfo.InvariantCulture) % 1, 9));
        Assert.Equal(new GraphAxisTickOption.FixedInterval(1), graph.AxisTickOptions.Y);
    }

    [Fact]
    public void NavigatingMeasuresThePlotAndResizingThinsAgainWithoutMovingTheView()
    {
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.9, 15.1), XTicks(Hundredth));
        var controller = new GraphViewController(start);
        Assert.False(controller.Resize(Plot));

        var wide = new SKRect(0, 0, 1500, 300);
        for (var step = 0; step < 12; step++)
        {
            controller.ZoomAt(GraphAxisField.X, wide, new SKPoint(750, 320), -1);
        }

        Assert.Equal((1500d, 300d), (controller.Graph.ViewOptions.PlotWidth, controller.Graph.ViewOptions.PlotHeight));
        var shown = controller.Graph.Frame.XAxis;
        var everyWide = Every(shown, 0.01);

        Assert.True(controller.Resize(new SKRect(0, 0, 300, 300)));
        var narrow = controller.Graph.Frame.XAxis;
        Assert.Equal(shown.Range, narrow.Range);
        Assert.True(Every(narrow, 0.01) > everyWide, $"{everyWide} -> {Every(narrow, 0.01)}");
        OnTheGrid(narrow, 0.01, Every(narrow, 0.01));
        Assert.Equal(XTicks(Hundredth), controller.Graph.AxisTickOptions);

        // Reset View: the configured range, every 0.01 tick again.
        controller.Reset();
        Assert.Equal(Labels(start.Frame.XAxis), Labels(controller.Graph.Frame.XAxis));
        Assert.Equal(21, controller.Graph.Frame.XAxis.Ticks.Count);
    }

    [Fact]
    public void CustomTicksComeAndGoWithTheView()
    {
        var start = Present(GraphType.ScatterPlot).Graph.WithAxisScale(XRange(14.85, 15.15), XTicks(new GraphAxisTickOption.CustomValues([14.9, 15.0, 20])));
        Assert.Equal(["14.9", "15.0"], Labels(start.Frame.XAxis));

        var wider = start.WithView(new GraphViewOptions(Range(14, 21), null));
        Assert.Equal(["14.9", "15.0", "20.0"], Labels(wider.Frame.XAxis));

        var away = start.WithView(new GraphViewOptions(Range(30, 40), null));
        Assert.Empty(away.Frame.XAxis.Ticks);
        Assert.Equal(start.AxisTickOptions, away.AxisTickOptions);
    }

    [Fact]
    public void TheViewIsKeptThroughTheGraphsOtherEdits()
    {
        var controller = new GraphViewController(Present(GraphType.Histogram).Graph);
        controller.ZoomAt(null, Plot, new SKPoint(300, 200), 2);
        var viewed = controller.Graph;

        foreach (var (what, graph) in new[]
                 {
                     ("labels", viewed.WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("T"), GraphLabelOption.Custom("X"), GraphLabelOption.Hidden))),
                     ("legend", viewed.WithLegend(viewed.LegendOptions with { Position = GraphLegendPosition.Bottom })),
                     ("statistics", viewed.WithStatistics(viewed.StatisticsOptions with { Mode = GraphStatisticsMode.Hide })),
                     ("appearance", viewed.WithAppearance(viewed.AppearanceOptions with { GridMode = GraphGridMode.Hide })),
                     ("ticks", viewed.WithAxisScale(viewed.AxisRangeOptions, XTicks(new GraphAxisTickOption.FixedInterval(0.01))))
                 })
        {
            Assert.True(graph.ViewOptions == viewed.ViewOptions, $"{what} kept the view");
            Assert.True(graph.Frame.XAxis.Range == viewed.Frame.XAxis.Range, $"{what} kept the X range shown");
            Assert.True(graph.Frame.YAxis.Range == viewed.Frame.YAxis.Range, $"{what} kept the Y range shown");
        }
    }

    [Fact]
    public void CopiesAndExportsDrawTheViewAtAnySizeAndTheme()
    {
        var presented = Present(GraphType.ScatterPlot);
        var controller = new GraphViewController(presented.Graph);
        controller.ZoomAt(null, Plot, new SKPoint(300, 200), 3);
        var shown = controller.Graph.Frame;

        Assert.NotEqual(Png(presented.Graph.Frame, presented.Plot), Png(shown, presented.Plot));
        Assert.Equal(Png(shown, presented.Plot), Png(presented.Graph.WithView(controller.Graph.ViewOptions).Frame, presented.Plot));

        foreach (var canvas in new[] { new SKRect(0, 0, 400, 300), Canvas, new SKRect(0, 0, 1400, 900) })
        {
            var layout = SkiaGraphRenderer.Layout(shown, canvas, GraphThemes.Dark);
            var transform = new GraphCoordinateTransform(shown.XAxis.Range, shown.YAxis.Range, layout.PlotArea);
            Assert.Equal(layout.PlotArea.Left, (float)transform.ToScreenX(controller.Graph.ViewOptions.X!.Value.Minimum), 3);
        }
    }

    // ---- The scale dialogs edit the configured ranges, not the view ----

    private sealed class ScaleDialog(Func<GraphAxisRangeOptions, GraphAxisTickOptions, GraphAxisScaleEdit?> answer) : IGraphAxisScaleDialog
    {
        public GraphAxisRangeOptions? ShownRanges { get; private set; }

        public Task<GraphAxisScaleEdit?> EditAsync(GraphTypeDefinition definition, GraphAxisField axis, GraphAxisRangeOptions currentRanges, GraphAxisTickOptions currentTicks, GraphRenderModel autoFrame)
        {
            ShownRanges = currentRanges;
            return Task.FromResult(answer(currentRanges, currentTicks));
        }
    }

    private sealed class AxesDialog(GraphAxisRangeOptions answer) : IGraphAxesDialog
    {
        public Task<GraphAxisRangeOptions?> EditAsync(GraphTypeDefinition definition, GraphAxisRangeOptions current, GraphRenderModel autoFrame, GraphAxisTickOptions ticks) =>
            Task.FromResult<GraphAxisRangeOptions?>(answer);
    }

    private static GraphPresentationState Zoomed(GraphType type = GraphType.ScatterPlot)
    {
        var controller = new GraphViewController(Present(type).Graph);
        controller.ZoomAt(null, Plot, new SKPoint(300, 200), 3);
        return controller.Graph;
    }

    [Fact]
    public async Task EditScaleShowsTheConfiguredRangeAndTicksAloneKeepTheView()
    {
        var zoomed = Zoomed();
        var dialog = new ScaleDialog((_, _) => new GraphAxisScaleEdit(GraphAxisRangeOption.Auto, new GraphAxisTickOption.FixedInterval(0.01)));
        var axes = new GraphAxesEditController(zoomed, new AxesDialog(GraphAxisRangeOptions.Default), dialog);

        Assert.True(await axes.EditScaleAsync(GraphAxisField.X));

        Assert.Equal(GraphAxisRangeOptions.Default, dialog.ShownRanges);
        Assert.Equal(GraphAxisRangeOptions.Default, axes.Graph.AxisRangeOptions);
        Assert.Equal(zoomed.ViewOptions, axes.Graph.ViewOptions);
        Assert.Equal(new GraphAxisTickOption.FixedInterval(0.01), axes.Graph.AxisTickOptions.X);
    }

    [Fact]
    public async Task CommittingADifferentRangeEndsThatAxissViewOnly()
    {
        var zoomed = Zoomed();
        var axes = new GraphAxesEditController(
            zoomed,
            new AxesDialog(GraphAxisRangeOptions.Default),
            new ScaleDialog((_, ticks) => new GraphAxisScaleEdit(new GraphAxisRangeOption(14.9, 15.1), ticks.X)));

        Assert.True(await axes.EditScaleAsync(GraphAxisField.X));

        Assert.Null(axes.Graph.ViewOptions.X);
        Assert.Equal(zoomed.ViewOptions.Y, axes.Graph.ViewOptions.Y);
        Assert.Equal(new GraphAxisRange(14.9, 15.1), axes.Graph.Frame.XAxis.Range);
    }

    [Fact]
    public async Task EditAxesEndsTheViewOfTheAxesWhoseRangeChanged()
    {
        var zoomed = Zoomed();
        var axes = new GraphAxesEditController(zoomed, new AxesDialog(XRange(14.8, 15.2)), new ScaleDialog((_, _) => null));

        Assert.True(await axes.EditAsync());

        Assert.Null(axes.Graph.ViewOptions.X);
        Assert.Equal(zoomed.ViewOptions.Y, axes.Graph.ViewOptions.Y);

        // The same ranges again keep every view.
        var again = new GraphAxesEditController(zoomed, new AxesDialog(GraphAxisRangeOptions.Default), new ScaleDialog((_, _) => null));
        Assert.True(await again.EditAsync());
        Assert.Equal(zoomed.ViewOptions, again.Graph.ViewOptions);
    }

    // ---- What the pointer acts on ----

    [Fact]
    public void AWheelStepActsOnANavigableAxisElseThePlotElseNothing()
    {
        var scatter = GraphTypeDefinitions.For(GraphType.ScatterPlot);
        var box = GraphTypeDefinitions.For(GraphType.BoxPlot);

        Assert.Equal((false, (GraphAxisField?)null), GraphWindow.WheelTarget(GraphLabelField.Title, null, false, scatter));
        Assert.Equal((false, (GraphAxisField?)null), GraphWindow.WheelTarget(GraphLabelField.XAxisTitle, GraphAxisField.X, true, scatter));
        Assert.Equal((true, (GraphAxisField?)GraphAxisField.X), GraphWindow.WheelTarget(null, GraphAxisField.X, false, scatter));
        Assert.Equal((true, (GraphAxisField?)GraphAxisField.Y), GraphWindow.WheelTarget(null, GraphAxisField.Y, true, box));
        Assert.Equal((true, (GraphAxisField?)null), GraphWindow.WheelTarget(null, null, true, scatter));
        Assert.Equal((false, (GraphAxisField?)null), GraphWindow.WheelTarget(null, null, false, scatter));

        // A box plot's categories: nothing on the axis itself, the plot where it reaches into it.
        Assert.Equal((false, (GraphAxisField?)null), GraphWindow.WheelTarget(null, GraphAxisField.X, false, box));
        Assert.Equal((true, (GraphAxisField?)null), GraphWindow.WheelTarget(null, GraphAxisField.X, true, box));
    }

    [Fact]
    public void ADoubleClickStillEditsTitlesAndAxes()
    {
        var scatter = GraphTypeDefinitions.For(GraphType.ScatterPlot);

        Assert.Equal((GraphLabelField.Title, (GraphAxisField?)null), GraphWindow.DoubleClickTarget(GraphLabelField.Title, null, scatter));
        Assert.Equal(((GraphLabelField?)null, (GraphAxisField?)GraphAxisField.Y), GraphWindow.DoubleClickTarget(null, GraphAxisField.Y, scatter));
        Assert.Equal(((GraphLabelField?)null, (GraphAxisField?)null), GraphWindow.DoubleClickTarget(null, null, scatter));
    }
}
