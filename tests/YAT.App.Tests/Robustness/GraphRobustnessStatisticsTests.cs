using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests.Robustness;

// #045: the statistics panel's options over the whole named corpus, on the three graph types that have a panel - with
// the corpus' own groups, with 40 groups and with 12 long CJK groups laid over its values, and with the case drawn
// together with a second variable - in every mode and with every choice of statistics, beside a legend on each side,
// on canvases from a small window to the export. Each graph is presented as the application presents it and its
// statistics options put on it; then:
//
//   * the options change the panel only: the axes, the legend, the lines and the plot model are the very ones the graph
//     had, the panel's rows are the ones worked out with it, and every statistic shown gives back the frame itself;
//   * Hide draws exactly what the graph draws with no panel ever put on it;
//   * the layout and the drawing do not fail, and the same graph lays out the same way twice;
//   * the panel's area is inside the canvas and clear of the plot and the legend, and the lines it shows and the ones
//     counted in "… N more" add up to all of them.
//
// The legend's side turns with the statistics chosen rather than multiplying them: every side meets every mode.
public sealed class GraphRobustnessStatisticsTests
{
    public static TheoryData<string> Cases => GraphRobustnessNamedTests.UnivariateCases;

    private static readonly RobustnessGraph[] Graphs = [RobustnessGraph.Histogram, RobustnessGraph.ProbabilityPlot, RobustnessGraph.EmpiricalCdf];

    private static readonly SKRect[] Canvases = [new(0, 0, 380, 244), new(0, 0, 760, 488), new(0, 0, 800, 500)];

    private static readonly GraphStatisticsOptions[] Options =
    [
        GraphStatisticsOptions.Default,
        new(GraphStatisticsMode.Show),
        new(GraphStatisticsMode.Hide),
        new(GraphStatisticsMode.Auto, true, false, false),
        new(GraphStatisticsMode.Show, false, true, false),
        new(GraphStatisticsMode.Auto, false, false, true),
        new(GraphStatisticsMode.Auto, true, false, true),
        new(GraphStatisticsMode.Show, false, true, true),
        new(GraphStatisticsMode.Auto, true, true, false),
        new(GraphStatisticsMode.Hide, false, false, false)
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryStatisticsOptionLaysOutAndDrawsOnEveryCanvas(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        foreach (var graph in Graphs)
        {
            var own = (UnivariateGraphData)RobustnessGraphs.DataFor(graph, robustnessCase);
            var cases = new (string What, GraphData Data)[]
            {
                ("groups=own", own),
                ("groups=40", WithGroups(own, 40, index => $"Lot {index}")),
                ("groups=12 long CJK", WithGroups(own, 12, index => $"長い晶圓批次名稱測試データ {index} 號")),
                ("together", Together(own))
            };

            foreach (var (what, data) in cases)
            {
                Check(robustnessCase.Describe($"{RobustnessGraphs.Name(graph)} statistics {what}"), graph, data);
            }
        }
    }

    private static UnivariateGraphData WithGroups(UnivariateGraphData data, int groups, Func<int, string> name) =>
        new(data.GraphType, data.WorksheetId, data.Variable, data.Values, new StringGroupData(
            new GraphColumnInfo(Guid.NewGuid(), "Lot", WorksheetDataType.String),
            (string?[])[.. Enumerable.Range(0, data.Count).Select(index => name(index % groups))]));

    // The case and its negation drawn together: one graph of series, "Reg1 / group" and "Negated / group".
    private static UnivariateGraphData Together(UnivariateGraphData data) =>
        GraphVariablesTogether.Combine(new MultiVariableGraphData(data.GraphType, data.WorksheetId,
        [
            data,
            new UnivariateGraphData(data.GraphType, data.WorksheetId, new GraphColumnInfo(Guid.NewGuid(), "Negated", data.Variable.DataType),
                (double[])[.. data.Values.ToArray().Select(value => -value)], data.Group)
        ]), TestContext.Current.CancellationToken);

    private static void Check(string context, RobustnessGraph graph, GraphData data)
    {
        var built = RobustnessGraphs.Build(graph, data, cancellationToken: TestContext.Current.CancellationToken);
        if (built.Frame is not { } frame)
        {
            return;
        }

        var definition = GraphTypeDefinitions.For(RobustnessGraphs.TypeOf(graph));
        var model = built.Model;
        var panel = frame.StatisticsPanel;
        var positions = Enum.GetValues<GraphLegendPosition>();
        var themes = 0;

        // Hide is the graph with no panel ever put on it: the builder's frame and nothing else (no specification here).
        var neverPanelled = RobustnessGraphs.BuilderFrame(model!);
        GraphRobustnessInvariants.That(neverPanelled.StatisticsPanel is null, context, "the builder's frame already had a panel");

        for (var index = 0; index < Options.Length; index++)
        {
            var options = Options[index];
            var legend = new GraphLegendOptions(GraphLegendMode.Auto, positions[index % positions.Length]);
            var where = $"{context} statistics {options.Mode} {string.Join("+", options.Items)} legend {legend.Position}";
            var state = new GraphPresentationState(frame, definition, GraphLabelOptions.Default, legendOptions: legend, statisticsOptions: options);
            var shown = state.Frame;

            GraphRobustnessInvariants.That(
                ReferenceEquals(shown.XAxis, frame.XAxis) && ReferenceEquals(shown.YAxis, frame.YAxis)
                && ReferenceEquals(shown.Legend, frame.Legend) && ReferenceEquals(shown.ReferenceLines, frame.ReferenceLines)
                && shown.Title == frame.Title && ReferenceEquals(model, built.Model) && ReferenceEquals(state.BaseFrame, frame),
                where, "the statistics options changed more than the panel");

            if (panel is null || options.Mode == GraphStatisticsMode.Hide)
            {
                GraphRobustnessInvariants.That(shown.StatisticsPanel is null, where, "a hidden panel, or none at all, was shown");
            }
            else
            {
                var trimmed = shown.StatisticsPanel;
                GraphRobustnessInvariants.That(
                    trimmed is not null && trimmed.Items.SequenceEqual(options.Items) && trimmed.Rows.Count == panel.Rows.Count
                    && trimmed.Rows.Zip(panel.Rows).All(pair => ReferenceEquals(pair.First, pair.Second))
                    && trimmed.Title == panel.Title && trimmed.GroupHeader == panel.GroupHeader,
                    where, "the panel shown is not the graph's own, with the statistics chosen");
            }

            if (options.Items.SequenceEqual(GraphStatisticsOptions.AllItems) && options.Mode != GraphStatisticsMode.Hide)
            {
                GraphRobustnessInvariants.That(
                    ReferenceEquals(state.WithLegend(GraphLegendOptions.Default).Frame, frame), where,
                    "every statistic shown is not the very frame");
            }

            foreach (var canvas in Canvases)
            {
                Layout(where + $" on {canvas.Width}x{canvas.Height}", shown, canvas);
            }

            try
            {
                RobustnessGraphs.Render(built with { Frame = shown }, themes++ % 2 == 0 ? GraphThemes.Light : GraphThemes.Dark);
            }
            catch (Exception exception)
            {
                Assert.Fail($"{where}\n  rendering threw: {exception}");
            }

            GraphRobustnessInvariants.That(
                ReferenceEquals(state.WithStatistics(GraphStatisticsOptions.Default).WithLegend(GraphLegendOptions.Default).Frame, frame),
                where, "the default statistics again did not give back the frame");
        }

        var hidden = new GraphPresentationState(frame, definition, GraphLabelOptions.Default, statisticsOptions: new GraphStatisticsOptions(GraphStatisticsMode.Hide)).Frame;
        GraphRobustnessInvariants.That(
            Pixels(built with { Frame = hidden }).AsSpan().SequenceEqual(Pixels(built with { Frame = neverPanelled })),
            context, "Hide did not draw what a graph with no panel draws");
    }

    private static byte[] Pixels(BuiltGraph built)
    {
        using var bitmap = new SKBitmap(RobustnessGraphs.RenderWidth, RobustnessGraphs.RenderHeight);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(
            canvas, built.Frame!, new SKRect(0, 0, RobustnessGraphs.RenderWidth, RobustnessGraphs.RenderHeight), GraphThemes.Light, built.Plot);
        return bitmap.Bytes;
    }

    private static void Layout(string where, GraphRenderModel frame, SKRect canvas)
    {
        var layout = SkiaGraphRenderer.Layout(frame, canvas, GraphThemes.Light);
        var again = SkiaGraphRenderer.Layout(frame, canvas, GraphThemes.Light);
        GraphRobustnessInvariants.That(layout.PlotArea == again.PlotArea && layout.LegendArea == again.LegendArea
            && layout.StatisticsPanelArea == again.StatisticsPanelArea, where, "the same graph laid out differently");

        if (!layout.HasPlotArea)
        {
            return;
        }

        var area = layout.StatisticsPanelArea;
        if (frame.StatisticsPanel is not { } panel)
        {
            GraphRobustnessInvariants.That(area.IsEmpty, where, $"a graph without a panel was given room for one: {area}");
            return;
        }

        if (area.IsEmpty || area.Height <= 0)
        {
            return;
        }

        GraphRobustnessInvariants.That(
            canvas.Contains(area) && !area.IntersectsWith(layout.PlotArea)
            && (layout.LegendArea.IsEmpty || !area.IntersectsWith(layout.LegendArea)),
            where, $"the panel's area {area} is outside the canvas {canvas} or over the plot {layout.PlotArea} or the legend {layout.LegendArea}");

        var dataLines = panel.IsGrouped ? panel.Rows.Count : panel.Items.Count;
        var (lines, more) = SkiaGraphRenderer.FitStatisticsLines(panel, area.Height, SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light));
        GraphRobustnessInvariants.That(
            lines >= 0 && lines <= dataLines && (more == 0 ? lines == dataLines || lines == 0 : lines - 1 + more == dataLines),
            where, $"{lines} lines shown and {more} counted out of {dataLines}");
    }
}
