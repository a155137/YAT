using System.Reflection;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// A graph drawn in panels (Task #058): the split by panel value (first seen first, "(Missing)" a panel of its own, at
// most nine), the grid the panels are laid out in, the composition - the whole graph's series order, bins and display
// budget given to every panel, the graph's axes the union of the panels' - and the one drawing used on screen and in
// every export, with the shared view.
public class GraphPanelTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData Text(string column, params string?[] values) => new(Column(column, WorksheetDataType.String), values);

    private static NumericGroupData Numbers(string column, params double?[] values) => new(Column(column), values);

    private static UnivariateGraphData Univariate(GraphType type, double[] values, GraphGroupData? group, GraphGroupData? panel) =>
        new(type, Guid.Empty, Column("Reg1"), values, group) { Panel = panel };

    private static T Model<T>(IGraphPlotRenderer? plot) =>
        (T)plot!.GetType().GetField("_model", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(plot)!;

    private static GraphConfiguration Configuration(GraphType type) => new(type, Guid.Empty, []);

    // Twelve observations over four sites; lot "z" only at site C, and site B seen first.
    private static readonly double[] Values = [15.0, 14.9, 15.2, 15.1, 14.8, 15.3, 15.05, 14.95, 15.15, 14.85, 15.25, 14.7];
    private static readonly string?[] Sites = ["B", "A", "B", null, "C", "A", "C", "B", "A", "C", null, "C"];
    private static readonly string?[] Lots = ["x", "y", "x", "y", "z", "x", "y", "x", "y", "x", "y", "z"];

    // ---- The split ----

    [Fact]
    public void PanelsFollowTheOrderTheirValuesAreFirstSeenWithMissingAsItsOwnPanel()
    {
        var panels = GraphPanelSplit.Split(Univariate(GraphType.Histogram, Values, Text("Lot", Lots), Text("Site", Sites)), Token);

        Assert.Equal(["Site = B", "Site = A", "Site = (Missing)", "Site = C"], panels.Select(panel => panel.Title));
        var b = Assert.IsType<UnivariateGraphData>(panels[0].Data);
        Assert.Equal([15.0, 15.2, 14.95], b.Values.ToArray());
        Assert.Equal(["x", "x", "x"], Assert.IsType<StringGroupData>(b.Group).Values.ToArray());
        Assert.Equal([15.1, 15.25], Assert.IsType<UnivariateGraphData>(panels[2].Data).Values.ToArray());
    }

    [Fact]
    public void NumericPanelValuesAreWrittenAsGroupValuesAre()
    {
        var panels = GraphPanelSplit.Split(Univariate(GraphType.EmpiricalCdf, [1, 2, 3], null, Numbers("Site", 2.5, 1, 2.5)), Token);

        Assert.Equal(["Site = 1", "Site = 2.5"], panels.Select(panel => panel.Title));
    }

    private static string[] Titles(IReadOnlyList<GraphPanelData> panels) => [.. panels.Select(panel => panel.Title)];

    private static double[] ValuesOf(GraphPanelData panel) => Assert.IsType<UnivariateGraphData>(panel.Data).Values.ToArray();

    [Fact]
    public void NumericPanelsGoFromTheSmallestValueUpWithMissingLast()
    {
        // Seen as 5, 1, 7, Missing, 3: drawn as 1, 3, 5, 7, Missing.
        var panels = GraphPanelSplit.Split(
            Univariate(GraphType.Histogram, [10, 11, 12, 13, 14], null, Numbers("Site", 5, 1, 7, null, 3)), Token);

        Assert.Equal(["Site = 1", "Site = 3", "Site = 5", "Site = 7", "Site = (Missing)"], Titles(panels));
        Assert.Equal([[11d], [14d], [10d], [12d], [13d]], panels.Select(ValuesOf));
    }

    [Fact]
    public void RepeatedNumericPanelValuesShareOnePanelInRowOrder()
    {
        var panels = GraphPanelSplit.Split(
            Univariate(GraphType.EmpiricalCdf, [10, 11, 12, 13, 14, 15], null, Numbers("Site", 2, 1, 2, null, 1, 2)), Token);

        Assert.Equal(["Site = 1", "Site = 2", "Site = (Missing)"], Titles(panels));
        Assert.Equal([[11d, 14], [10d, 12, 15], [13d]], panels.Select(ValuesOf));
    }

    [Fact]
    public void NegativeAndDecimalPanelValuesAreOrderedAsNumbersNotAsText()
    {
        var panels = GraphPanelSplit.Split(
            Univariate(GraphType.ProbabilityPlot, [1, 2, 3, 4, 5, 6], null, Numbers("Site", 0.75, -2.5, 10, -10, 0.5, 2)), Token);

        Assert.Equal(["Site = -10", "Site = -2.5", "Site = 0.5", "Site = 0.75", "Site = 2", "Site = 10"], Titles(panels));
        Assert.Equal([[4d], [2d], [5d], [1d], [6d], [3d]], panels.Select(ValuesOf));
    }

    [Fact]
    public void TextPanelsKeepTheOrderTheyAreFirstSeenMissingIncluded()
    {
        var panels = GraphPanelSplit.Split(Univariate(GraphType.Histogram, [1, 2, 3, 4], null, Text("Site", "S5", null, "S1", "S5")), Token);

        Assert.Equal(["Site = S5", "Site = (Missing)", "Site = S1"], Titles(panels));
    }

    [Fact]
    public void NineNumericPanelsMissingIncludedAreDrawnInOrderButNotTen()
    {
        double?[] nine = [9, 2, null, 8, 3, 7, 4, 6, 5];
        var panels = GraphPanelSplit.Split(Univariate(GraphType.Histogram, [.. nine.Select((_, index) => (double)index)], null, Numbers("Site", nine)), Token);
        Assert.Equal(["Site = 2", "Site = 3", "Site = 4", "Site = 5", "Site = 6", "Site = 7", "Site = 8", "Site = 9", "Site = (Missing)"], Titles(panels));

        double?[] ten = [.. nine, 1];
        var refused = Assert.Throws<GraphPreparationException>(() =>
            GraphPanelSplit.Split(Univariate(GraphType.Histogram, [.. ten.Select((_, index) => (double)index)], null, Numbers("Site", ten)), Token));
        Assert.Equal(GraphPanelSplit.TooManyPanelsMessage("Site"), refused.Message);
    }

    [Theory]
    [MemberData(nameof(PanelGraphs))]
    public void ReorderedNumericPanelsKeepEveryGroupsColour(GraphType type)
    {
        // Sites seen as 3, 1, 2 (drawn 1, 2, 3); lots first seen x, y, z in the whole graph - z at sites 1 and 3 only.
        double?[] sites = [3, 1, 2, 3, 1, 2, 1, 3, 2, 1, null, 3];
        var data = type == GraphType.ScatterPlot
            ? new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), Values, Values.Select(value => (value * 2) - 15).ToArray(), Text("Lot", Lots)) { Panel = Numbers("Site", sites) }
            : (GraphData)Univariate(type, Values, Text("Lot", Lots), Numbers("Site", sites));

        var prepared = GraphPanelPreparation.Prepare(data, Configuration(type), null, Token)!;

        Assert.Equal(["Site = 1", "Site = 2", "Site = 3", "Site = (Missing)"], prepared.Panels.Select(panel => panel.Title));
        var colours = prepared.Frame.Legend!.Entries.ToDictionary(entry => entry.Label, entry => entry.SeriesIndex);
        Assert.Equal(new Dictionary<string, int> { ["x"] = 0, ["y"] = 1, ["z"] = 2 }, colours);
        Assert.All(prepared.Panels, panel => Assert.All(SeriesOf(type, panel.Plot), series => Assert.Equal(colours[series.Label], series.Index)));
        Assert.Contains(("z", 2), SeriesOf(type, prepared.Panels[0].Plot));
    }

    [Fact]
    public void NinePanelsAreDrawnMissingIncludedButNotTen()
    {
        double[] values = [.. Enumerable.Range(0, 10).Select(i => (double)i)];
        var nine = GraphPanelSplit.Split(Univariate(GraphType.Histogram, values[..9], null, Text("Site", [.. Enumerable.Range(0, 8).Select(i => $"S{i}"), null])), Token);
        Assert.Equal(9, nine.Count);
        Assert.Equal("Site = (Missing)", nine[^1].Title);

        var ten = Assert.Throws<GraphPreparationException>(() =>
            GraphPanelSplit.Split(Univariate(GraphType.Histogram, values, null, Text("Site", [.. Enumerable.Range(0, 9).Select(i => $"S{i}"), null])), Token));
        Assert.Equal(
            "The panel column \"Site\" has more than 9 values in the rows used. Filter the rows down to 9 values or fewer first.",
            ten.Message);
    }

    [Fact]
    public void SeveralVariablesShareOnePanelOrderFirstVariableFirst()
    {
        var data = new MultiVariableGraphData(GraphType.ProbabilityPlot, Guid.Empty,
        [
            new UnivariateGraphData(GraphType.ProbabilityPlot, Guid.Empty, Column("Reg1"), new double[] { 1, 2 }, null) { Panel = Text("Site", "A", "A") },
            new UnivariateGraphData(GraphType.ProbabilityPlot, Guid.Empty, Column("Reg2"), new double[] { 3, 4, 5 }, null) { Panel = Text("Site", "B", "A", "B") }
        ]);

        var panels = GraphPanelSplit.Split(data, Token);

        Assert.Equal(["Site = A", "Site = B"], panels.Select(panel => panel.Title));
        var a = Assert.IsType<MultiVariableGraphData>(panels[0].Data);
        Assert.Equal([[1d, 2], [4d]], a.Variables.Select(variable => variable.Values.ToArray()));
        var b = Assert.IsType<MultiVariableGraphData>(panels[1].Data);
        Assert.Equal([[], [3d, 5]], b.Variables.Select(variable => variable.Values.ToArray()));
    }

    [Fact]
    public void AScatterPlotKeepsItsPairsAndGroupsInEveryPanel()
    {
        var data = new ScatterGraphData(Guid.Empty, Column("X"), Column("Y"), new double[] { 1, 2, 3, 4 }, new double[] { 10, 20, 30, 40 }, Text("Lot", "x", "y", "x", "y"))
        {
            Panel = Numbers("Site", 1, 2, 2, 1)
        };

        var panels = GraphPanelSplit.Split(data, Token);

        var one = Assert.IsType<ScatterGraphData>(panels[0].Data);
        Assert.Equal([1d, 4], one.XValues.ToArray());
        Assert.Equal([10d, 40], one.YValues.ToArray());
        Assert.Equal(["x", "y"], Assert.IsType<StringGroupData>(one.Group).Values.ToArray());
    }

    // ---- The grid ----

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 2, 2)]
    [InlineData(4, 2, 2)]
    [InlineData(5, 3, 2)]
    [InlineData(6, 3, 2)]
    [InlineData(7, 3, 3)]
    [InlineData(8, 3, 3)]
    [InlineData(9, 3, 3)]
    public void PanelsAreLaidOutInANearSquareGrid(int count, int columns, int rows)
    {
        Assert.Equal((columns, rows), GraphPanelLayout.Grid(count));

        var area = new SKRect(100, 50, 900, 650);
        var cells = GraphPanelLayout.Cells(area, count);
        Assert.Equal(count, cells.Count);
        Assert.All(cells, cell =>
        {
            Assert.Equal(cells[0].Width, cell.Width, 3);
            Assert.Equal(cells[0].Height, cell.Height, 3);
            Assert.True(area.Contains(new SKPoint(cell.MidX, cell.MidY)));
        });
        Assert.Equal((area.Width - (GraphPanelLayout.Gap * (columns - 1))) / columns, cells[0].Width, 3);
        for (var index = 0; index < count; index++)
        {
            Assert.Equal(index, GraphPanelLayout.PanelAt(cells, new SKPoint(cells[index].MidX, cells[index].MidY)));
            Assert.All(cells.Where((_, other) => other != index), other => Assert.False(other.IntersectsWith(cells[index])));
        }

        Assert.Null(GraphPanelLayout.PanelAt(cells, new SKPoint(area.Left - 5, area.Top)));
    }

    // ---- The composition ----

    public static TheoryData<GraphType> PanelGraphs => [GraphType.ScatterPlot, GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf];

    private static GraphData Data(GraphType type) => type == GraphType.ScatterPlot
        ? new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), Values, Values.Select(value => (value * 2) - 15).ToArray(), Text("Lot", Lots)) { Panel = Text("Site", Sites) }
        : Univariate(type, Values, Text("Lot", Lots), Text("Site", Sites));

    private static IReadOnlyList<(string Label, int Index)> SeriesOf(GraphType type, IGraphPlotRenderer? plot) => type switch
    {
        GraphType.ScatterPlot => [.. Model<ScatterRenderModel>(plot).Series.Select(series => (series.Label, series.SeriesIndex))],
        GraphType.Histogram => [.. Model<HistogramRenderModel>(plot).Series.Select(series => (series.Label, series.SeriesIndex))],
        GraphType.ProbabilityPlot => [.. Model<ProbabilityPlotRenderModel>(plot).Series.Select(series => (series.Label, series.SeriesIndex))],
        _ => [.. Model<EmpiricalCdfRenderModel>(plot).Series.Select(series => (series.Label, series.SeriesIndex))]
    };

    [Theory]
    [MemberData(nameof(PanelGraphs))]
    public void AGroupHasItsColourInEveryPanelAndOneLegendHasThemAll(GraphType type)
    {
        var prepared = GraphPanelPreparation.Prepare(Data(type), Configuration(type), null, Token)!;

        // The whole graph's order: x, y, z - z first seen in the fifth row, and only at site C.
        Assert.Equal([("x", 0), ("y", 1), ("z", 2)], prepared.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
        Assert.Equal(4, prepared.Panels.Count);

        // Site C sees z first, yet z keeps index 2 and every panel draws its series in the whole graph's order, so one lies
        // over another the same way in each; site A has no z at all.
        Assert.Equal([("x", 0), ("y", 1), ("z", 2)], SeriesOf(type, prepared.Panels[3].Plot));
        Assert.Equal([("x", 0), ("y", 1)], SeriesOf(type, prepared.Panels[1].Plot));
    }

    [Fact]
    public void EveryHistogramPanelCountsIntoTheWholeGraphsBins()
    {
        var data = Data(GraphType.Histogram);
        var whole = new HistogramRenderModelBuilder().Build((UnivariateGraphData)data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;

        var prepared = GraphPanelPreparation.Prepare(data, Configuration(GraphType.Histogram), null, Token)!;

        Assert.All(prepared.Panels, panel => Assert.Equal(whole.Bins, Model<HistogramRenderModel>(panel.Plot).Bins));
        var counts = prepared.Panels.Select(panel => Model<HistogramRenderModel>(panel.Plot).Series.Sum(series => series.Counts.Sum())).ToArray();
        Assert.Equal([3, 3, 2, 4], counts);
        Assert.Equal(new GraphAxisRange(whole.Bins[0].LowerEdge, whole.Bins[^1].UpperEdge), prepared.Frame.XAxis.Range);
    }

    [Fact]
    public void AHistogramsCountAxisReachesTheTallestPanelNotTheWholeGraph()
    {
        var prepared = GraphPanelPreparation.Prepare(Data(GraphType.Histogram), Configuration(GraphType.Histogram), null, Token)!;
        var tallest = prepared.Panels.Max(panel => Model<HistogramRenderModel>(panel.Plot).Series.Max(series => series.Counts.Max()));

        Assert.Equal(GraphAxisScale.Count, prepared.Frame.YAxis.Scale);
        Assert.Equal(GraphAxisTicks.NiceCounts(tallest).Range, prepared.Frame.YAxis.Range);
    }

    [Theory]
    [MemberData(nameof(PanelGraphs))]
    public void TheGraphsAxesReachEveryPanel(GraphType type)
    {
        var prepared = GraphPanelPreparation.Prepare(Data(type), Configuration(type), null, Token)!;
        var frames = prepared.Panels.Select(panel => type switch
        {
            GraphType.ScatterPlot => Model<ScatterRenderModel>(panel.Plot).Frame,
            GraphType.Histogram => Model<HistogramRenderModel>(panel.Plot).Frame,
            GraphType.ProbabilityPlot => Model<ProbabilityPlotRenderModel>(panel.Plot).Frame,
            _ => Model<EmpiricalCdfRenderModel>(panel.Plot).Frame
        }).ToList();

        Assert.Equal(frames.Min(frame => frame.XAxis.Range.Minimum), prepared.Frame.XAxis.Range.Minimum);
        Assert.Equal(frames.Max(frame => frame.XAxis.Range.Maximum), prepared.Frame.XAxis.Range.Maximum);
        Assert.Equal(frames.Min(frame => frame.YAxis.Range.Minimum), prepared.Frame.YAxis.Range.Minimum);
        Assert.Equal(frames.Max(frame => frame.YAxis.Range.Maximum), prepared.Frame.YAxis.Range.Maximum);
        Assert.Equal(frames[0].YAxis.Scale, prepared.Frame.YAxis.Scale);
        if (type == GraphType.EmpiricalCdf)
        {
            Assert.Equal(new GraphAxisRange(0, 100), prepared.Frame.YAxis.Range);
        }
    }

    [Fact]
    public void OnePanelIsTheWholeGraphUnderItsPanelTitle()
    {
        var data = Univariate(GraphType.Histogram, Values, Text("Lot", Lots), Text("Site", [.. Values.Select(_ => "A")]));
        var whole = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;

        var prepared = GraphPanelPreparation.Prepare(data, Configuration(GraphType.Histogram), null, Token)!;

        Assert.Equal("Site = A", Assert.Single(prepared.Panels).Title);
        Assert.Equal(whole.Frame.XAxis.Range, prepared.Frame.XAxis.Range);
        Assert.Equal(whole.Frame.YAxis.Range, prepared.Frame.YAxis.Range);
        Assert.Equal(whole.Frame.YAxis.Ticks.Select(tick => tick.Label), prepared.Frame.YAxis.Ticks.Select(tick => tick.Label));
    }

    [Fact]
    public void ThePanelsTogetherDrawNoMoreThanOneGraphWould()
    {
        const int PerPanel = 20_000;
        var count = PerPanel * 9;
        var random = new Random(58);
        double[] x = [.. Enumerable.Range(0, count).Select(_ => random.NextDouble())];
        double[] y = [.. Enumerable.Range(0, count).Select(_ => random.NextDouble())];
        var data = new ScatterGraphData(Guid.Empty, Column("X"), Column("Y"), x, y, null)
        {
            Panel = Numbers("Site", [.. Enumerable.Range(0, count).Select(i => (double?)(i % 9))])
        };

        var prepared = GraphPanelPreparation.Prepare(data, Configuration(GraphType.ScatterPlot), null, Token)!;

        var drawn = prepared.Panels.Select(panel => Model<ScatterRenderModel>(panel.Plot)).ToList();
        Assert.All(drawn, model => Assert.Equal(DisplaySampling.DefaultMaximumRenderedPoints / 9, model.RenderedPointCount));
        Assert.True(drawn.Sum(model => model.RenderedPointCount) <= DisplaySampling.DefaultMaximumRenderedPoints);
        Assert.All(drawn, model => Assert.Equal(PerPanel, model.SourcePointCount));
    }

    [Fact]
    public void SeveralVariablesInPanelsAreDrawnTogetherInEachPanel()
    {
        var data = new MultiVariableGraphData(GraphType.EmpiricalCdf, Guid.Empty,
        [
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.Empty, Column("Reg1"), new double[] { 1, 2, 3 }, null) { Panel = Text("Site", "A", "B", "A") },
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.Empty, Column("Reg2"), new double[] { 4, 5 }, null) { Panel = Text("Site", "B", "B") }
        ]);

        var prepared = GraphPanelPreparation.Prepare(data, Configuration(GraphType.EmpiricalCdf), GraphVariablesTogether.AxisTitle, Token)!;

        Assert.Equal([("Reg1", 0), ("Reg2", 1)], prepared.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
        Assert.Equal([("Reg1", 0)], SeriesOf(GraphType.EmpiricalCdf, prepared.Panels[0].Plot));
        Assert.Equal([("Reg1", 0), ("Reg2", 1)], SeriesOf(GraphType.EmpiricalCdf, prepared.Panels[1].Plot));
        Assert.Equal(GraphVariablesTogether.AxisTitle, prepared.Frame.XAxis.Title);
    }

    [Fact]
    public void AGraphInPanelsHasNoStatisticsPanel()
    {
        foreach (var type in new[] { GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf })
        {
            Assert.True(GraphTypeDefinitions.For(type).Supports(GraphCapability.StatisticsPanel));
            var definition = GraphPanelPreparation.Definition(type);
            Assert.False(definition.Supports(GraphCapability.StatisticsPanel));
            Assert.Equal(GraphTypeDefinitions.For(type).Capabilities.Count - 1, definition.Capabilities.Count);

            var data = Data(type);
            var prepared = GraphPanelPreparation.Prepare(data, Configuration(type), null, Token)!;
            var graph = GraphPresentation.Present(prepared.Frame, prepared.Whole, Configuration(type), definition, Token);
            Assert.Null(graph.Frame.StatisticsPanel);
            Assert.Null(graph.BaseFrame.StatisticsPanel);
        }
    }

    // ---- Drawing, copying, exporting ----

    private static (GraphPresentationState Graph, IReadOnlyList<GraphPanel> Panels) Presented(GraphType type)
    {
        var prepared = GraphPanelPreparation.Prepare(Data(type), Configuration(type), null, Token)!;
        return (GraphPresentation.Present(prepared.Frame, prepared.Whole, Configuration(type), GraphPanelPreparation.Definition(type), Token), prepared.Panels);
    }

    private static byte[] Png(GraphRenderModel frame, IGraphPlotRenderer? plot, IReadOnlyList<GraphPanel>? panels) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, GraphThemes.Light) { Panels = panels });

    [Fact]
    public void WithoutPanelsTheDrawingIsTheGraphsOwn()
    {
        var data = Univariate(GraphType.Histogram, Values, Text("Lot", Lots), null);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;
        var plot = new HistogramRenderer(model);
        var bounds = new SKRect(0, 0, 760, 488);

        using var drawn = new SKBitmap(760, 488);
        using (var canvas = new SKCanvas(drawn))
        {
            GraphDrawing.Render(new SkiaGraphRenderer(), canvas, model.Frame, plot, panels: null, bounds, GraphThemes.Light);
        }

        using var direct = new SKBitmap(760, 488);
        using (var canvas = new SKCanvas(direct))
        {
            new SkiaGraphRenderer().Render(canvas, model.Frame, bounds, GraphThemes.Light, plot);
        }

        Assert.Equal(direct.Bytes, drawn.Bytes);
        Assert.Equal(Png(model.Frame, plot, null), Png(model.Frame, plot, []));
    }

    [Theory]
    [MemberData(nameof(PanelGraphs))]
    public void AnExportIsTheWholeGraphEveryPanelInIt(GraphType type)
    {
        var (graph, panels) = Presented(type);

        var all = Png(graph.Frame, null, panels);
        Assert.Equal(all, Png(graph.Frame, null, panels));
        Assert.NotEqual(all, Png(graph.Frame, null, panels.Take(3).ToList()));
        Assert.NotEqual(all, Png(graph.Frame, null, null));
    }

    [Fact]
    public void EachPanelIsDrawnUnderItsTitleOverTheGraphsAxesAndTheGraphKeepsItsOwnTitles()
    {
        var (graph, panels) = Presented(GraphType.Histogram);

        var outer = GraphPanelLayout.OuterFrame(graph.Frame);
        Assert.Equal(graph.Frame.Title, outer.Title);
        Assert.Equal((graph.Frame.XAxis.Title, graph.Frame.YAxis.Title), (outer.XAxis.Title, outer.YAxis.Title));
        Assert.Empty(outer.XAxis.Ticks);
        Assert.Same(graph.Frame.Legend, outer.Legend);

        var panel = GraphPanelLayout.PanelFrame(graph.Frame, panels[0].Title);
        Assert.Equal("Site = B", panel.Title);
        Assert.Null(panel.Legend);
        Assert.Null(panel.XAxis.Title);
        Assert.Equal(graph.Frame.XAxis.Ticks, panel.XAxis.Ticks);
        Assert.Equal(graph.Frame.YAxis.Range, panel.YAxis.Range);
    }

    private static (GraphPresentationState Graph, IReadOnlyList<GraphPanel> Panels) SevenSites(GraphType type)
    {
        var values = Enumerable.Range(0, 140).Select(index => 15 + (0.1 * Math.Sin(index * 0.37))).ToArray();
        var sites = Numbers("Site", [.. Enumerable.Range(0, 140).Select(index => (double?)((index % 7) + 1))]);
        var prepared = GraphPanelPreparation.Prepare(Univariate(type, values, null, sites), Configuration(type), null, Token)!;
        return (GraphPresentation.Present(prepared.Frame, prepared.Whole, Configuration(type), GraphPanelPreparation.Definition(type), Token), prepared.Panels);
    }

    [Fact]
    public void ASmallPanelLabelsOnlyTheSharedTicksItHasRoomFor()
    {
        var (graph, panels) = SevenSites(GraphType.ProbabilityPlot);
        var cells = GraphPanelLayout.Cells(graph.Frame, panels.Count, new SKRect(0, 0, 800, 600), GraphThemes.Light);

        var shared = graph.Frame.YAxis.Ticks;
        foreach (var (panel, cell) in panels.Zip(cells))
        {
            var frame = GraphPanelLayout.PanelFrame(graph.Frame, panel.Title, cell, GraphThemes.Light);
            var plotArea = SkiaGraphRenderer.Layout(frame, cell, GraphThemes.Light).PlotArea;
            var kept = frame.YAxis.Ticks;

            // Fewer of the whole graph's own percentages, 50 among them and every second one out from it, each label a
            // line clear of the next; the X axis, with room enough, keeps all of its ticks.
            Assert.InRange(kept.Count, 2, shared.Count - 1);
            Assert.All(kept, tick => Assert.Contains(tick, shared));
            Assert.Contains(kept, tick => tick.Label == "50");
            Assert.All(kept.Zip(kept.Skip(1)), pair =>
                Assert.True((pair.Second.Value - pair.First.Value) / frame.YAxis.Range.Span * plotArea.Height >= GraphThemes.Light.TickLabelFontSize * 1.2));
            Assert.Equal(graph.Frame.YAxis.Range, frame.YAxis.Range);
            Assert.Equal(graph.Frame.XAxis.Ticks, frame.XAxis.Ticks);
        }
    }

    [Theory]
    [MemberData(nameof(PanelGraphs))]
    public void APanelWithRoomForEveryTickKeepsThemAll(GraphType type)
    {
        var (graph, panels) = Presented(type);
        var cells = GraphPanelLayout.Cells(graph.Frame, panels.Count, new SKRect(0, 0, 1600, 1200), GraphThemes.Light);

        var frame = GraphPanelLayout.PanelFrame(graph.Frame, panels[0].Title, cells[0], GraphThemes.Light);

        Assert.Equal(graph.Frame.XAxis.Ticks, frame.XAxis.Ticks);
        Assert.Equal(graph.Frame.YAxis.Ticks, frame.YAxis.Ticks);
    }

    // ---- One view for every panel ----

    [Fact]
    public void ZoomingOrPanningOnePanelMovesThemAllAndResetBringsThemBack()
    {
        var (graph, panels) = Presented(GraphType.ScatterPlot);
        var bounds = new SKRect(0, 0, 760, 488);
        var cells = GraphPanelLayout.Cells(graph.Frame, panels.Count, bounds, GraphThemes.Light);
        var plotArea = SkiaGraphRenderer.Layout(GraphPanelLayout.PanelFrame(graph.Frame, panels[3].Title, cells[3], GraphThemes.Light), cells[3], GraphThemes.Light).PlotArea;
        var controller = new GraphViewController(graph);

        var point = new SKPoint(plotArea.MidX, plotArea.MidY);
        var anchor = GraphViewNavigator.ValueAt(GraphAxisField.X, graph.Frame.XAxis.Range, plotArea, point);
        Assert.True(controller.ZoomAt(null, plotArea, point, 2));
        Assert.Equal(anchor, GraphViewNavigator.ValueAt(GraphAxisField.X, controller.Graph.Frame.XAxis.Range, plotArea, point), 9);

        // Every panel is drawn over the graph's frame, so every panel shows the zoomed axes.
        Assert.All(panels, panel => Assert.Equal(controller.Graph.Frame.XAxis.Range, GraphPanelLayout.PanelFrame(controller.Graph.Frame, panel.Title).XAxis.Range));

        controller.BeginPan(plotArea, point);
        controller.PanTo(new SKPoint(point.X + 30, point.Y));
        Assert.True(controller.Graph.Frame.XAxis.Range.Minimum < GraphViewNavigator.ValueAt(GraphAxisField.X, graph.Frame.XAxis.Range, plotArea, point));

        controller.ResetCommand.Execute(null);
        Assert.Equal(graph.Frame.XAxis.Range, controller.Graph.Frame.XAxis.Range);
        Assert.Equal(Png(graph.Frame, null, panels), Png(controller.Graph.Frame, null, panels));
    }

    [Fact]
    public void ConfiguredRangesAndTicksApplyToEveryPanel()
    {
        var (graph, panels) = Presented(GraphType.EmpiricalCdf);
        var configured = graph.WithAxisScale(
            GraphAxisRangeOptions.Default with { X = new GraphAxisRangeOption(14.6, 15.4) },
            GraphAxisTickOptions.Default with { X = new GraphAxisTickOption.FixedInterval(0.2) });

        Assert.All(panels, panel =>
        {
            var frame = GraphPanelLayout.PanelFrame(configured.Frame, panel.Title);
            Assert.Equal(new GraphAxisRange(14.6, 15.4), frame.XAxis.Range);
            Assert.Equal(["14.6", "14.8", "15.0", "15.2", "15.4"], frame.XAxis.Ticks.Select(tick => tick.Label));
        });
    }

    [Fact]
    public void TheSharedCountAxisDoesNotDependOnTheOrderOfThePanels()
    {
        // 80 counts in one panel's tallest bar, 72 in the other's: both axes reach 80, on steps of 20 and of 10.
        static GraphPresentationState Graph(double tall, double short_)
        {
            double[] values = [.. Enumerable.Repeat(1.0, 80), 2.0, .. Enumerable.Repeat(1.0, 72), 2.0];
            double?[] sites = [.. Enumerable.Repeat<double?>(tall, 81), .. Enumerable.Repeat<double?>(short_, 73)];
            var prepared = GraphPanelPreparation.Prepare(Univariate(GraphType.Histogram, values, null, Numbers("Site", sites)), Configuration(GraphType.Histogram), null, Token)!;
            return GraphPresentation.Present(prepared.Frame, prepared.Whole, Configuration(GraphType.Histogram), GraphPanelPreparation.Definition(GraphType.Histogram), Token);
        }

        Assert.Equal(GraphAxisTicks.NiceCounts(80).Range, GraphAxisTicks.NiceCounts(72).Range);
        Assert.NotEqual(GraphAxisTicks.NiceCounts(80).Ticks.Count, GraphAxisTicks.NiceCounts(72).Ticks.Count);

        var tallFirst = Graph(1, 2).Frame.YAxis;
        var tallLast = Graph(2, 1).Frame.YAxis;

        Assert.Equal(tallFirst.Ticks, tallLast.Ticks);
        Assert.Equal(GraphAxisTicks.NiceCounts(80).Ticks, tallFirst.Ticks);
    }

    [Fact]
    public void ThinningAPanelsTicksChangesOnlyWhatThePanelDrawsNeverTheGraphsSettings()
    {
        var (graph, panels) = SevenSites(GraphType.EmpiricalCdf);
        var ranges = GraphAxisRangeOptions.Default with { Y = new GraphAxisRangeOption(0, 100) };
        var ticks = new GraphAxisTickOptions(
            new GraphAxisTickOption.CustomValues([.. Enumerable.Range(0, 41).Select(step => 14.8 + (step * 0.01))]),
            new GraphAxisTickOption.FixedInterval(5));
        var configured = graph.WithAxisScale(ranges, ticks);
        var shared = (X: configured.Frame.XAxis.Ticks.ToArray(), Y: configured.Frame.YAxis.Ticks.ToArray());
        var bounds = new SKRect(0, 0, 800, 600);

        using (var bitmap = new SKBitmap(800, 600))
        using (var canvas = new SKCanvas(bitmap))
        {
            GraphDrawing.Render(new SkiaGraphRenderer(), canvas, configured.Frame, null, panels, bounds, GraphThemes.Light);
        }

        var cells = GraphPanelLayout.Cells(configured.Frame, panels.Count, bounds, GraphThemes.Light);
        var drawn = GraphPanelLayout.PanelFrame(configured.Frame, panels[0].Title, cells[0], GraphThemes.Light);

        // The panel draws fewer of the ticks...
        Assert.InRange(drawn.YAxis.Ticks.Count, 2, shared.Y.Length - 1);
        Assert.InRange(drawn.XAxis.Ticks.Count, 2, shared.X.Length - 1);
        Assert.All(drawn.YAxis.Ticks, tick => Assert.Contains(tick, shared.Y));
        Assert.All(drawn.XAxis.Ticks, tick => Assert.Contains(tick, shared.X));

        // ...while the settings, the configured range and the graph's own ticks stay exactly as they were.
        Assert.Equal(ticks, configured.AxisTickOptions);
        Assert.Equal(41, Assert.IsType<GraphAxisTickOption.CustomValues>(configured.AxisTickOptions.X).Values.Count);
        Assert.Equal(new GraphAxisTickOption.FixedInterval(5), configured.AxisTickOptions.Y);
        Assert.Equal(ranges, configured.AxisRangeOptions);
        Assert.Equal(shared.X, configured.Frame.XAxis.Ticks);
        Assert.Equal(shared.Y, configured.Frame.YAxis.Ticks);
        Assert.Equal(new GraphAxisRange(0, 100), drawn.YAxis.Range);
    }
}
