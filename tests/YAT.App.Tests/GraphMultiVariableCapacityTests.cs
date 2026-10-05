using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Graphs of many variables (Task #060): up to fifty, drawn by every graph type that takes several, grouped or not; a
// title that counts them rather than naming fifty; and a box plot whose category labels are thinned to the room they are
// drawn in - on screen, after a resize, in an export - without touching its categories, boxes or settings.
public class GraphMultiVariableCapacityTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static string Name(int index) => $"P{index:000}_IDDQ_VDD_CORE";

    private static MultiVariableGraphData Variables(GraphType type, int count, bool grouped = false, int rows = 60) =>
        new(type, Guid.Empty,
        [
            .. Enumerable.Range(1, count).Select(index => new UnivariateGraphData(
                type,
                Guid.Empty,
                Column(Name(index)),
                Enumerable.Range(0, rows).Select(row => index + (0.01 * ((row * 7) % 23))).ToArray(),
                grouped ? new NumericGroupData(Column("SITE"), Enumerable.Range(0, rows).Select(row => (double?)(((row * 5) % 4) + 1)).ToArray()) : null))
        ]);

    public static TheoryData<GraphType, int> GraphsOfManyVariables => new()
    {
        { GraphType.Histogram, 10 }, { GraphType.Histogram, 20 }, { GraphType.Histogram, 50 },
        { GraphType.ProbabilityPlot, 10 }, { GraphType.ProbabilityPlot, 20 }, { GraphType.ProbabilityPlot, 50 },
        { GraphType.EmpiricalCdf, 10 }, { GraphType.EmpiricalCdf, 20 }, { GraphType.EmpiricalCdf, 50 },
        { GraphType.BoxPlot, 10 }, { GraphType.BoxPlot, 20 }, { GraphType.BoxPlot, 50 }
    };

    private static (GraphPresentationState Graph, IGraphPlotRenderer Plot, int Series) Draw(GraphType type, int count, bool grouped)
    {
        var data = Variables(type, count, grouped);
        var configuration = new GraphConfiguration(type, Guid.Empty, []);
        if (type == GraphType.BoxPlot)
        {
            var box = new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels([.. data.Variables.Select(v => v.Variable.Name)], grouped ? "SITE" : null), Token)!;
            return (GraphPresentation.Present(box.Frame, data, configuration, Token), new BoxPlotRenderer(box), box.Boxes.Count);
        }

        var together = GraphVariablesTogether.Combine(data, Token);
        var group = together.Group?.Column.Name;
        switch (type)
        {
            case GraphType.Histogram:
                var histogram = new HistogramRenderModelBuilder().Build(together, new HistogramPlotLabels(together.Variable.Name, group) { AxisTitle = GraphVariablesTogether.AxisTitle }, Token)!;
                return (GraphPresentation.Present(histogram.Frame, together, configuration, Token), new HistogramRenderer(histogram), histogram.Series.Count);
            case GraphType.ProbabilityPlot:
                var probability = new ProbabilityPlotRenderModelBuilder().Build(together, new ProbabilityPlotLabels(together.Variable.Name, group) { AxisTitle = GraphVariablesTogether.AxisTitle }, Token)!;
                return (GraphPresentation.Present(probability.Frame, together, configuration, Token), new ProbabilityPlotRenderer(probability), probability.Series.Count);
            default:
                var cdf = new EmpiricalCdfRenderModelBuilder().Build(together, new EmpiricalCdfLabels(together.Variable.Name, group) { AxisTitle = GraphVariablesTogether.AxisTitle }, Token)!;
                return (GraphPresentation.Present(cdf.Frame, together, configuration, Token), new EmpiricalCdfRenderer(cdf), cdf.Series.Count);
        }
    }

    private static byte[] Png(GraphPresentationState graph, IGraphPlotRenderer plot) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(graph.Frame, plot, GraphThemes.Light));

    // ---- Up to fifty variables ----

    [Theory]
    [MemberData(nameof(GraphsOfManyVariables))]
    public void ManyVariablesAreDrawnGroupedOrNot(GraphType type, int count)
    {
        foreach (var grouped in new[] { false, true })
        {
            var (graph, plot, series) = Draw(type, count, grouped);

            Assert.Equal(grouped ? count * 4 : count, series);
            Assert.NotEmpty(Png(graph, plot));
            if (graph.Frame.StatisticsPanel is { } statistics)
            {
                Assert.Equal(series, statistics.Rows.Count);
            }

            if (grouped && type == GraphType.BoxPlot)
            {
                // The numeric groups in their order (Task #059), one colour each across every variable.
                Assert.Equal([("1", 0), ("2", 1), ("3", 2), ("4", 3)], graph.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
            }
        }
    }

    // ---- The title ----

    [Fact]
    public void UpToTenVariablesAreNamedAndMoreAreCounted()
    {
        string[] ten = [.. Enumerable.Range(1, 10).Select(Name)];

        Assert.Equal(10, GraphVariablesTogether.MaximumNamedVariables);
        Assert.Equal("Reg1, Reg2", GraphVariablesTogether.Name(["Reg1", "Reg2"]));
        Assert.Equal(string.Join(", ", ten), GraphVariablesTogether.Name(ten));
        Assert.Equal("11 variables", GraphVariablesTogether.Name([.. ten, "One more"]));
        Assert.Equal("50 variables", GraphVariablesTogether.Name(Enumerable.Range(1, 50).Select(Name)));
    }

    [Theory]
    [InlineData(GraphType.BoxPlot, "Boxplot of 50 variables")]
    [InlineData(GraphType.Histogram, "Histogram of 50 variables")]
    [InlineData(GraphType.ProbabilityPlot, "Normal Probability Plot of 50 variables")]
    [InlineData(GraphType.EmpiricalCdf, "Empirical CDF of 50 variables")]
    public void TheTitleOfManyVariablesCountsThem(GraphType type, string title)
    {
        Assert.Equal(title, Draw(type, 50, grouped: false).Graph.Frame.Title);
        Assert.Equal(title.Replace("50", "11", StringComparison.Ordinal), Draw(type, 11, grouped: true).Graph.Frame.Title);
        Assert.Contains($"{Name(1)}, {Name(2)}", Draw(type, 10, grouped: false).Graph.Frame.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleTheUserTypedIsNeverShortened()
    {
        var custom = string.Join(", ", Enumerable.Range(1, 50).Select(Name));
        var (graph, _, _) = Draw(GraphType.BoxPlot, 50, grouped: false);

        var labelled = graph.WithLabels(GraphLabelOptions.Default with { Title = new GraphLabelOption(GraphLabelMode.Custom, custom) });

        Assert.Equal(custom, labelled.Frame.Title);
        Assert.Equal("Boxplot of 50 variables", labelled.WithLabels(GraphLabelOptions.Default).Frame.Title);
    }

    // ---- Box plot category labels ----

    // The labels a box plot shows drawn at these bounds, as the renderer decides them.
    private static IReadOnlyList<GraphAxisTick> Shown(GraphRenderModel frame, SKRect bounds, out SKRect plotArea, out Func<string, float> width)
    {
        var layout = SkiaGraphRenderer.Layout(frame, bounds, GraphThemes.Light);
        var area = plotArea = layout.PlotArea;
        var transform = new GraphCoordinateTransform(frame.XAxis.Range, frame.YAxis.Range, area);
        width = label => SkiaGraphRenderer.TickLabelWidth(label, GraphThemes.Light);
        return GraphCategoryLabels.Shown(frame.XAxis, value => (float)transform.ToScreenX(value), width, GraphThemes.Light.TickLabelFontSize / 2);
    }

    private static void AssertApart(GraphRenderModel frame, IReadOnlyList<GraphAxisTick> shown, SKRect plotArea, Func<string, float> width)
    {
        var transform = new GraphCoordinateTransform(frame.XAxis.Range, frame.YAxis.Range, plotArea);
        foreach (var (left, right) in shown.Zip(shown.Skip(1)))
        {
            var room = transform.ToScreenX(right.Value) - transform.ToScreenX(left.Value);
            Assert.True(room >= ((width(left.Label) + width(right.Label)) / 2) + (GraphThemes.Light.TickLabelFontSize / 2), $"{left.Label} / {right.Label}");
        }
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(50, false)]
    [InlineData(20, true)]
    [InlineData(50, true)]
    public void ABoxPlotLabelsOnlyTheCategoriesItHasRoomForEveryOneFromTheFirst(int count, bool grouped)
    {
        var (graph, _, _) = Draw(GraphType.BoxPlot, count, grouped);
        var frame = graph.Frame;
        var categories = frame.XAxis.Ticks;

        var shown = Shown(frame, new SKRect(0, 0, 760, 488), out var plotArea, out var width);

        Assert.InRange(shown.Count, 1, categories.Count - 1);
        Assert.Same(categories[0], shown[0]);
        var step = categories.ToList().IndexOf(shown[1]);
        Assert.Equal([.. categories.Where((_, index) => index % step == 0)], shown);
        AssertApart(frame, shown, plotArea, width);

        // Nothing of the graph changed: every category and its box are still there.
        Assert.Equal(grouped ? count * 4 : count, frame.XAxis.Ticks.Count);
    }

    [Fact]
    public void AFewCategoriesWithRoomAreAllLabelled()
    {
        var (graph, _, _) = Draw(GraphType.BoxPlot, 3, grouped: false);

        Assert.Same(graph.Frame.XAxis.Ticks, Shown(graph.Frame, new SKRect(0, 0, 760, 488), out _, out _));
    }

    [Fact]
    public void MoreRoomShowsMoreLabelsAndTheSameRoomTheSameLabels()
    {
        var (graph, plot, _) = Draw(GraphType.BoxPlot, 50, grouped: false);

        var small = Shown(graph.Frame, new SKRect(0, 0, 760, 488), out _, out _);
        var wide = Shown(graph.Frame, new SKRect(0, 0, 1600, 1000), out var widePlot, out var width);

        Assert.True(wide.Count > small.Count, $"{small.Count} at 760 x 488, {wide.Count} at 1600 x 1000");
        AssertApart(graph.Frame, wide, widePlot, width);
        Assert.Equal(small, Shown(graph.Frame, new SKRect(0, 0, 760, 488), out _, out _));
        Assert.Equal(Png(graph, plot), Png(graph, plot));
    }

    [Fact]
    public void OnlyACategoricalAxisIsThinned()
    {
        var crowded = new GraphAxisModel(new GraphAxisRange(0, 100), [.. Enumerable.Range(0, 101).Select(value => new GraphAxisTick(value, $"{value:000000}"))], "X");

        Assert.Same(crowded.Ticks, GraphCategoryLabels.Shown(crowded, value => (float)value, _ => 50f, 5f));
        Assert.Equal(
            [0d, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100],
            GraphCategoryLabels.Shown(crowded with { Scale = GraphAxisScale.Categorical }, value => (float)value * 6, _ => 50f, 5f).Select(tick => tick.Value));
    }
}
