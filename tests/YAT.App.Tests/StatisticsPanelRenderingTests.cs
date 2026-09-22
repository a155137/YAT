using DocumentFormat.OpenXml.Packaging;
using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Where the statistics panel goes and how it is drawn: in the column right of the plot, below the legend, the same in
// the window, the PNG and the slide. Without a panel everything is laid out and drawn exactly as before.
public class StatisticsPanelRenderingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly SKRect Canvas = new(0, 0, 800, 600);

    private static readonly GraphLayoutMetrics Metrics = new()
    {
        TitleHeight = 20,
        AxisTitleHeight = 14,
        TickLabelHeight = 13,
        YTickLabelWidth = 30,
        XTickLabelOverflow = 10,
        LegendWidth = 80,
        LegendHeight = 60,
        StatisticsPanelWidth = 150
    };

    private static GraphAxisModel Axis(double minimum, double maximum, string? title) =>
        new(new GraphAxisRange(minimum, maximum), GraphAxisTicks.Evenly(new GraphAxisRange(minimum, maximum)), title);

    private static GraphRenderModel Model(GraphLegendModel? legend = null, GraphStatisticsPanel? panel = null) =>
        new GraphRenderModel("Sample Graph", Axis(0, 100, "X Axis"), Axis(0, 500, "Y Axis"), legend).WithStatisticsPanel(panel);

    private static GraphLegendModel Legend(int entries = 2) =>
        new([.. Enumerable.Range(0, entries).Select(index => new GraphLegendEntry($"Lot {index}", index))], "Lot");

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Data(double[] values, string?[]? groups = null) =>
        new(GraphType.Histogram, Guid.NewGuid(), Column("Reg1"), values,
            groups is null ? null : new StringGroupData(Column("Lot", WorksheetDataType.String), groups));

    private static GraphStatisticsPanel Ungrouped() => GraphStatisticsPanelBuilder.Build(Data([1, 2, 3, 4.5]), Token)!;

    private static GraphStatisticsPanel Grouped(int groups, string prefix = "Lot ")
    {
        var labels = Enumerable.Range(0, groups * 2).Select(index => $"{prefix}{index % groups}").ToArray();
        return GraphStatisticsPanelBuilder.Build(Data([.. Enumerable.Range(0, groups * 2).Select(index => (double)index)], labels), Token)!;
    }

    private static void AssertInside(SKRect outer, SKRect inner) =>
        Assert.True(inner.Left >= outer.Left - 0.01f && inner.Top >= outer.Top - 0.01f
            && inner.Right <= outer.Right + 0.01f && inner.Bottom <= outer.Bottom + 0.01f,
            $"{inner} is not inside {outer}.");

    private static SKBitmap Render(GraphRenderModel model, int width = 800, int height = 600, GraphTheme? theme = null)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, model, new SKRect(0, 0, width, height), theme ?? GraphThemes.Light);
        return bitmap;
    }

    private static IEnumerable<SKColor> PixelsIn(SKBitmap bitmap, SKRect area)
    {
        for (var y = (int)Math.Ceiling(area.Top); y < (int)Math.Floor(area.Bottom); y++)
        {
            for (var x = (int)Math.Ceiling(area.Left); x < (int)Math.Floor(area.Right); x++)
            {
                yield return bitmap.GetPixel(x, y);
            }
        }
    }

    private static bool Identical(SKBitmap first, SKBitmap second) =>
        first.Width == second.Width && first.Height == second.Height && first.Bytes.SequenceEqual(second.Bytes);

    // ---- Layout ----

    [Fact]
    public void WithoutAPanelTheLayoutIsWhatItAlwaysWas()
    {
        var legacyMetrics = Metrics with { LegendHeight = 0, StatisticsPanelWidth = 0 };

        foreach (var model in new[] { Model(), Model(Legend()) })
        {
            var layout = GraphLayoutCalculator.Calculate(Canvas, model, Metrics);
            var legacy = GraphLayoutCalculator.Calculate(Canvas, model, legacyMetrics);

            Assert.Equal(legacy, layout);
            Assert.True(layout.StatisticsPanelArea.IsEmpty);
        }

        // The legend on its own: flush right, the plot's full height.
        var alone = GraphLayoutCalculator.Calculate(Canvas, Model(Legend()), Metrics);
        Assert.Equal(Canvas.Right - Metrics.OuterPadding, alone.LegendArea.Right);
        Assert.Equal(alone.PlotArea.Top, alone.LegendArea.Top);
        Assert.Equal(alone.PlotArea.Bottom, alone.LegendArea.Bottom);
    }

    [Fact]
    public void ThePanelSitsBelowTheLegendInOneColumnRightOfThePlot()
    {
        var layout = GraphLayoutCalculator.Calculate(Canvas, Model(Legend(), Ungrouped()), Metrics);
        var legend = layout.LegendArea;
        var panel = layout.StatisticsPanelArea;

        Assert.False(panel.IsEmpty);
        Assert.Equal(legend.Left, panel.Left);
        Assert.True(panel.Top >= legend.Bottom);
        Assert.True(panel.Left > layout.PlotArea.Right);
        Assert.Equal(layout.PlotArea.Top, legend.Top);
        Assert.Equal(layout.PlotArea.Bottom, panel.Bottom);

        // The column is as wide as the wider of the two, and each keeps its own width.
        Assert.Equal(Metrics.LegendWidth, legend.Width, 0.01f);
        Assert.Equal(Metrics.StatisticsPanelWidth, panel.Width, 0.01f);
        Assert.Equal(Canvas.Right - Metrics.OuterPadding, panel.Right, 0.01f);

        var areas = new[] { layout.TitleArea, layout.PlotArea, layout.XAxisArea, layout.YAxisArea, legend, panel };
        Assert.All(areas, area => AssertInside(Canvas, area));
        for (var first = 0; first < areas.Length; first++)
        {
            for (var second = first + 1; second < areas.Length; second++)
            {
                Assert.False(areas[first].IntersectsWith(areas[second]), $"{areas[first]} overlaps {areas[second]}.");
            }
        }
    }

    [Fact]
    public void TheSharedColumnTakesTheWidthOfTheWiderElement()
    {
        var withoutPanel = GraphLayoutCalculator.Calculate(Canvas, Model(Legend()), Metrics);
        var widePanel = GraphLayoutCalculator.Calculate(Canvas, Model(Legend(), Ungrouped()), Metrics);
        var narrowPanel = GraphLayoutCalculator.Calculate(
            Canvas, Model(Legend(), Ungrouped()), Metrics with { StatisticsPanelWidth = 50 });

        Assert.Equal(withoutPanel.PlotArea.Right - (Metrics.StatisticsPanelWidth - Metrics.LegendWidth), widePanel.PlotArea.Right, 0.01f);
        Assert.Equal(withoutPanel.PlotArea.Right, narrowPanel.PlotArea.Right, 0.01f);
        Assert.Equal(50, narrowPanel.StatisticsPanelArea.Width, 0.01f);
    }

    [Fact]
    public void WithoutALegendThePanelStartsAtTheTopOfThePlot()
    {
        var layout = GraphLayoutCalculator.Calculate(Canvas, Model(panel: Ungrouped()), Metrics);

        Assert.True(layout.LegendArea.IsEmpty);
        Assert.Equal(layout.PlotArea.Top, layout.StatisticsPanelArea.Top);
        Assert.Equal(layout.PlotArea.Bottom, layout.StatisticsPanelArea.Bottom);
    }

    [Fact]
    public void ATallLegendLeavesThePanelHalfTheColumn()
    {
        var layout = GraphLayoutCalculator.Calculate(
            Canvas, Model(Legend(), Ungrouped()), Metrics with { LegendHeight = 5000 });

        Assert.Equal(layout.PlotArea.Height * GraphLayoutCalculator.MaximumLegendShareWithPanel, layout.LegendArea.Height, 0.01f);
        Assert.True(layout.StatisticsPanelArea.Height >= (layout.PlotArea.Height / 2f) - Metrics.Gap - 0.01f);
    }

    [Theory]
    [InlineData(400, 300)]
    [InlineData(260, 240)]
    [InlineData(300, 400)]
    public void ANarrowCanvasKeepsThePanelAndNarrowsThePlot(int width, int height)
    {
        var canvas = new SKRect(0, 0, width, height);
        var layout = GraphLayoutCalculator.Calculate(canvas, Model(Legend(), Ungrouped()), Metrics with { StatisticsPanelWidth = 280 });

        Assert.True(layout.HasPlotArea);
        Assert.False(layout.StatisticsPanelArea.IsEmpty);
        Assert.True(layout.StatisticsPanelArea.Width > 0);
        Assert.True(layout.StatisticsPanelArea.Width
            <= ((width - (2 * Metrics.OuterPadding)) * GraphLayoutCalculator.MaximumStatisticsPanelShare) + 0.01f);
        AssertInside(canvas, layout.StatisticsPanelArea);
        Assert.False(layout.StatisticsPanelArea.IntersectsWith(layout.PlotArea));
    }

    // ---- Fitting the rows ----

    [Fact]
    public void RowsThatFitAreAllShown()
    {
        var panel = Grouped(5);

        Assert.Equal((5, 0), SkiaGraphRenderer.FitStatisticsLines(panel, 1000, 15));
        Assert.Equal((3, 0), SkiaGraphRenderer.FitStatisticsLines(Ungrouped(), 1000, 15));
    }

    [Fact]
    public void RowsThatDoNotFitAreCountedOnTheLastLine()
    {
        var panel = Grouped(100);

        // 16 padding + 2 header lines + 6 data lines at 15 + 5 spacing, less one spacing: 171.
        var (lines, more) = SkiaGraphRenderer.FitStatisticsLines(panel, 171, 15);

        Assert.Equal(6, lines);
        Assert.Equal(95, more);
        Assert.Equal(panel.Rows.Count, lines - 1 + more);
        Assert.Equal(100, panel.Rows.Count);
    }

    [Fact]
    public void ALongLabelEndsInAnEllipsisAndFits()
    {
        using var font = new SKFont { Size = 12 };
        const string Label = "A very long lot name that no statistics panel has room for";

        var shortened = SkiaGraphRenderer.Ellipsize(Label, font, 80);

        Assert.EndsWith("…", shortened);
        Assert.True(font.MeasureText(shortened) <= 80);
        Assert.StartsWith(shortened[..^1], Label);
        Assert.Equal("Lot 1", SkiaGraphRenderer.Ellipsize("Lot 1", font, 80));
    }

    // ---- Drawing ----

    [Fact]
    public void AFrameWithoutAPanelIsDrawnExactlyAsBefore()
    {
        var frame = Model(Legend());

        using var plain = Render(frame);
        using var cleared = Render(frame.WithStatisticsPanel(Ungrouped()).WithStatisticsPanel(null));

        Assert.True(Identical(plain, cleared));
    }

    [Fact]
    public void ThePanelIsDrawnInItsArea()
    {
        var model = Model(panel: Ungrouped());
        var layout = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light);

        using var without = Render(Model());
        using var with = Render(model);

        Assert.False(Identical(without, with));
        // Its text is there, where the plot would otherwise have reached.
        var pixels = PixelsIn(with, layout.StatisticsPanelArea).ToList();
        Assert.Contains(GraphThemes.Light.Text, pixels);
        Assert.Contains(GraphThemes.Light.PlotBackground, PixelsIn(without, layout.StatisticsPanelArea));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AGroupedPanelShowsEachSeriesInItsLegendColourAndTheLegendStays(bool dark)
    {
        var theme = dark ? GraphThemes.Dark : GraphThemes.Light;
        var model = Model(Legend(3), Grouped(3));
        var layout = SkiaGraphRenderer.Layout(model, Canvas, theme);

        using var bitmap = Render(model, theme: theme);
        var panelPixels = PixelsIn(bitmap, layout.StatisticsPanelArea).ToHashSet();
        var legendPixels = PixelsIn(bitmap, layout.LegendArea).ToHashSet();

        for (var series = 0; series < 3; series++)
        {
            Assert.Contains(theme.SeriesColor(series), panelPixels);
            Assert.Contains(theme.SeriesColor(series), legendPixels);
        }
    }

    [Theory]
    [InlineData(800, 600)]
    [InlineData(420, 320)]
    [InlineData(340, 260)]
    public void ManyGroupsAreDrawnInsideThePanelAtAnySize(int width, int height)
    {
        var model = Model(Legend(200), Grouped(200, "A rather long lot name "));
        var canvas = new SKRect(0, 0, width, height);
        var layout = SkiaGraphRenderer.Layout(model, canvas, GraphThemes.Light);

        using var bitmap = Render(model, width, height);

        Assert.True(layout.HasPlotArea);
        Assert.False(layout.StatisticsPanelArea.IsEmpty);
        Assert.Contains(PixelsIn(bitmap, layout.StatisticsPanelArea), pixel => pixel != GraphThemes.Light.Background);

        var (lines, more) = SkiaGraphRenderer.FitStatisticsLines(
            model.StatisticsPanel!, layout.StatisticsPanelArea.Height, SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light));
        Assert.True(more > 0, "200 groups should not all fit.");
        Assert.Equal(200, lines - 1 + more);
    }

    // ---- Export ----

    private static (GraphRenderModel Frame, IGraphPlotRenderer Plot) Histogram(bool showStatistics)
    {
        var values = Enumerable.Range(0, 200).Select(index => (double)(index % 40)).ToArray();
        var groups = Enumerable.Range(0, 200).Select(index => index % 5 == 0 ? null : $"Lot {index % 3}").ToArray();
        var data = Data(values, groups);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;
        var frame = GraphStatisticsPanelBuilder.Attach(
            model.Frame, data, GraphTypeDefinitions.For(GraphType.Histogram), new GraphPresentationOptions(showStatistics), Token);
        return (frame, new HistogramRenderer(model));
    }

    private static SKBitmap Decode(byte[] png) => SKBitmap.Decode(png);

    [Fact]
    public void ThePngCarriesThePanelAtTheUsualExportSize()
    {
        var (on, plot) = Histogram(showStatistics: true);
        var (off, _) = Histogram(showStatistics: false);
        var service = new GraphExportService();

        using var withPanel = Decode(service.RenderPng(new GraphExportSnapshot(on, plot, GraphThemes.Light)));
        using var withoutPanel = Decode(service.RenderPng(new GraphExportSnapshot(off, plot, GraphThemes.Light)));

        Assert.Equal(GraphExportService.ExportWidth, withPanel.Width);
        Assert.Equal(GraphExportService.ExportHeight, withPanel.Height);
        Assert.Equal(GraphExportService.ExportWidth, withoutPanel.Width);
        Assert.Equal(GraphExportService.ExportHeight, withoutPanel.Height);
        Assert.False(Identical(withPanel, withoutPanel));

        // The panel is drawn where the layout of the export canvas puts it (layout units at the export scale).
        var layout = SkiaGraphRenderer.Layout(on, new SKRect(0, 0,
            GraphExportService.ExportWidth / GraphExportService.ExportScale,
            GraphExportService.ExportHeight / GraphExportService.ExportScale), GraphThemes.Light);
        var area = layout.StatisticsPanelArea;
        var scaled = new SKRect(area.Left * GraphExportService.ExportScale, area.Top * GraphExportService.ExportScale,
            area.Right * GraphExportService.ExportScale, area.Bottom * GraphExportService.ExportScale);
        var pixels = PixelsIn(withPanel, scaled).ToHashSet();
        Assert.Contains(GraphThemes.Light.SeriesColor(0), pixels);
        Assert.Contains(GraphThemes.Light.SeriesColor(2), pixels);
    }

    [Fact]
    public void WithoutAPanelThePngIsWhatItAlwaysWas()
    {
        var (off, plot) = Histogram(showStatistics: false);
        var service = new GraphExportService();

        Assert.Null(off.StatisticsPanel);
        Assert.Equal(
            service.RenderPng(new GraphExportSnapshot(off, plot, GraphThemes.Light)),
            service.RenderPng(new GraphExportSnapshot(off.WithStatisticsPanel(null), plot, GraphThemes.Light)));
    }

    [Fact]
    public void ThePowerPointSlideCarriesThePngWithThePanel()
    {
        var (on, plot) = Histogram(showStatistics: true);
        var png = new GraphExportService().RenderPng(new GraphExportSnapshot(on, plot, GraphThemes.Light));

        using var directory = new TemporaryDirectory();
        var path = directory.File("graph.pptx");
        new PowerPointGraphExporter().Save(
            path, new PowerPointSlideImage(png, GraphExportService.ExportWidth, GraphExportService.ExportHeight, "FFFFFF"));

        using var document = PresentationDocument.Open(path, false);
        var slide = Assert.Single(document.PresentationPart!.SlideParts);
        var image = Assert.Single(slide.ImageParts);
        using var stream = image.GetStream();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);

        Assert.Equal(png, bytes.ToArray());
    }
}
