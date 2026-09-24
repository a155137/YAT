using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests.Robustness;

// #044: the legend over the whole named corpus, on every graph type, with every mode and side, on canvases from a small
// window to the export - with the corpus' own groups, and with 40 and 150 groups laid over its values. Each graph is
// presented as the application presents it and its legend options put on it; then:
//
//   * the presentation changes the legend and its side only: the axes, the panel, the lines and the plot model are the
//     very ones the graph had, and Auto on the right gives back the frame itself;
//   * the layout and the drawing do not fail, and the same graph lays out the same way twice;
//   * every cell of an arranged legend is finite and inside the legend's area, which is inside the canvas and clear of
//     the plot, and the entries shown and the ones counted in "… N more" add up to all of them;
//   * a legend on the right that its single column holds is laid out as a legend always was.
public sealed class GraphRobustnessLegendTests
{
    public static TheoryData<string> Cases => GraphRobustnessNamedTests.UnivariateCases;

    public static TheoryData<string> PairedCases => GraphRobustnessNamedTests.PairedCases;

    private static readonly SKRect[] Canvases = [new(0, 0, 380, 244), new(0, 0, 760, 488), new(0, 0, 800, 500)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryLegendOptionLaysOutAndDrawsOnEveryCanvas(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        foreach (var graph in RobustnessGraphs.UnivariateGraphs)
        {
            foreach (var groups in new[] { 0, 40, 150 })
            {
                var data = WithGroups(RobustnessGraphs.DataFor(graph, robustnessCase), groups);
                Check(robustnessCase.Describe($"{RobustnessGraphs.Name(graph)} legend groups={(groups == 0 ? "own" : groups)}"), graph, data);
            }
        }
    }

    [Theory]
    [MemberData(nameof(PairedCases))]
    public void EveryScatterLegendOptionLaysOutAndDrawsOnEveryCanvas(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        foreach (var groups in new[] { 0, 40, 150 })
        {
            var data = WithGroups(RobustnessGraphs.DataFor(RobustnessGraph.Scatter, robustnessCase), groups);
            Check(robustnessCase.Describe($"Scatter legend groups={(groups == 0 ? "own" : groups)}"), RobustnessGraph.Scatter, data);
        }
    }

    // The case's values with groups laid over them: every observation in group index mod groups. 0 keeps its own.
    private static GraphData WithGroups(GraphData data, int groups)
    {
        if (groups == 0)
        {
            return data;
        }

        StringGroupData Over(int count) => new(
            new GraphColumnInfo(Guid.NewGuid(), "Lot", WorksheetDataType.String),
            (string?[])[.. Enumerable.Range(0, count).Select(index => $"Lot {index % groups}")]);

        return data switch
        {
            MultiVariableGraphData multi => new MultiVariableGraphData(multi.GraphType, multi.WorksheetId,
                [.. multi.Variables.Select(variable => new UnivariateGraphData(variable.GraphType, variable.WorksheetId, variable.Variable, variable.Values, Over(variable.Count)))]),
            UnivariateGraphData univariate => new UnivariateGraphData(univariate.GraphType, univariate.WorksheetId, univariate.Variable, univariate.Values, Over(univariate.Count)),
            ScatterGraphData scatter => new ScatterGraphData(scatter.WorksheetId, scatter.X, scatter.Y, scatter.XValues, scatter.YValues, Over(scatter.XValues.Length)),
            _ => data
        };
    }

    private static void Check(string context, RobustnessGraph graph, GraphData data)
    {
        var built = RobustnessGraphs.Build(graph, data, cancellationToken: TestContext.Current.CancellationToken);
        if (built.Frame is not { } frame)
        {
            return;
        }

        var definition = GraphTypeDefinitions.For(RobustnessGraphs.TypeOf(graph));
        var model = built.Model;
        var themes = 0;
        foreach (var mode in Enum.GetValues<GraphLegendMode>())
        {
            foreach (var position in Enum.GetValues<GraphLegendPosition>())
            {
                var options = new GraphLegendOptions(mode, position);
                var where = $"{context} legend {mode} {position}";
                var state = new GraphPresentationState(frame, definition, GraphLabelOptions.Default, legendOptions: options);
                var shown = state.Frame;

                GraphRobustnessInvariants.That(
                    ReferenceEquals(shown.XAxis, frame.XAxis) && ReferenceEquals(shown.YAxis, frame.YAxis)
                    && ReferenceEquals(shown.StatisticsPanel, frame.StatisticsPanel) && ReferenceEquals(shown.ReferenceLines, frame.ReferenceLines)
                    && shown.Title == frame.Title && ReferenceEquals(model, built.Model),
                    where, "the legend options changed more than the legend");
                GraphRobustnessInvariants.That(
                    mode == GraphLegendMode.Hide ? shown.Legend is null : ReferenceEquals(shown.Legend, frame.Legend), where,
                    "the legend is not the graph type's, or was not hidden");
                if (mode != GraphLegendMode.Hide && position == GraphLegendPosition.Right)
                {
                    GraphRobustnessInvariants.That(ReferenceEquals(shown, frame), where, "the legend where it always was is not the very frame");
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

                GraphRobustnessInvariants.That(ReferenceEquals(state.WithLegend(GraphLegendOptions.Default).Frame, frame), where,
                    "Auto on the right again did not give back the frame");
            }
        }
    }

    private static void Layout(string where, GraphRenderModel frame, SKRect canvas)
    {
        var layout = SkiaGraphRenderer.Layout(frame, canvas, GraphThemes.Light);
        var again = SkiaGraphRenderer.Layout(frame, canvas, GraphThemes.Light);
        GraphRobustnessInvariants.That(layout.PlotArea == again.PlotArea && layout.LegendArea == again.LegendArea
            && Same(layout.LegendArrangement, again.LegendArrangement), where, "the same graph laid out differently");

        if (!layout.HasPlotArea || frame.Legend is not { } legend || layout.LegendArea.IsEmpty)
        {
            return;
        }

        var area = layout.LegendArea;
        GraphRobustnessInvariants.That(canvas.Contains(area) && !area.IntersectsWith(layout.PlotArea), where,
            $"the legend's area {area} is outside the canvas or over the plot {layout.PlotArea}");

        if (layout.LegendArrangement is not { } arrangement)
        {
            GraphRobustnessInvariants.That(frame.LegendPosition == GraphLegendPosition.Right, where, "a legend on another side was not arranged");
            return;
        }

        GraphRobustnessInvariants.That(Math.Abs(arrangement.Size.Width - area.Width) < 0.01f && Math.Abs(arrangement.Size.Height - area.Height) < 0.01f,
            where, $"the legend's area {area} is not the size of its arrangement {arrangement.Size}");
        foreach (var cell in arrangement.Cells)
        {
            var bounds = cell.Bounds;
            GraphRobustnessInvariants.That(
                float.IsFinite(bounds.Left) && float.IsFinite(bounds.Top) && float.IsFinite(bounds.Right) && float.IsFinite(bounds.Bottom)
                && bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= area.Width + 0.01f && bounds.Bottom <= area.Height + 0.01f
                && (cell.IsMore || cell.EntryIndex < legend.Entries.Count),
                where, $"a cell {bounds} lies outside the legend's {area.Width}x{area.Height}");
        }

        var shown = arrangement.Cells.Count(cell => !cell.IsMore);
        GraphRobustnessInvariants.That(shown + arrangement.HiddenCount == legend.Entries.Count
            && (arrangement.HiddenCount > 0) == arrangement.Cells.Any(cell => cell.IsMore), where,
            $"{shown} entries shown and {arrangement.HiddenCount} left out of {legend.Entries.Count}");
    }

    private static bool Same(GraphLegendArrangement? first, GraphLegendArrangement? second) =>
        first is null ? second is null
            : second is not null && first.Size == second.Size && first.HiddenCount == second.HiddenCount && first.Cells.SequenceEqual(second.Cells);
}
