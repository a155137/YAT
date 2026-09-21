using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Exporting draws the graph again off screen with the renderers the window uses. Nothing here has a window, a canvas or
// a screen, which is the point: the image cannot depend on any of them.
public class GraphExportServiceTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static ScatterRenderModel Scatter(string?[]? groups = null)
    {
        var x = new double[60];
        var y = new double[60];
        for (var index = 0; index < x.Length; index++)
        {
            x[index] = index;
            y[index] = Math.Sin(index / 6d) * 40;
        }

        return new ScatterRenderModelBuilder().Build(
            new ScatterGraphData(
                Guid.NewGuid(),
                Column("Reg1"),
                Column("Reg2"),
                x,
                y,
                groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups)),
            new ScatterPlotLabels("Reg1", "Reg2", "SITE"),
            Token)!;
    }

    private static HistogramRenderModel Histogram()
    {
        var values = new double[200];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = index % 40;
        }

        return new HistogramRenderModelBuilder().Build(
            new UnivariateGraphData(GraphType.Histogram, Guid.NewGuid(), Column("Reg1"), values, null),
            new HistogramPlotLabels("Reg1"),
            Token)!;
    }

    private static ProbabilityPlotRenderModel ProbabilityPlot()
    {
        var values = new double[120];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = 10 + (index * 0.25);
        }

        return new ProbabilityPlotRenderModelBuilder().Build(
            new UnivariateGraphData(GraphType.ProbabilityPlot, Guid.NewGuid(), Column("Reg1"), values, null),
            new ProbabilityPlotLabels("Reg1"),
            Token)!;
    }

    private static GraphExportSnapshot ScatterSnapshot(GraphTheme? theme = null)
    {
        var model = Scatter();
        return new GraphExportSnapshot(model.Frame, new ScatterRenderer(model), theme ?? GraphThemes.Light);
    }

    private static GraphExportSnapshot HistogramSnapshot(GraphTheme? theme = null)
    {
        var model = Histogram();
        return new GraphExportSnapshot(model.Frame, new HistogramRenderer(model), theme ?? GraphThemes.Light);
    }

    // 1
    [Fact]
    public void AScatterPlotIsExportedAsAPng()
    {
        var png = new GraphExportService().RenderPng(ScatterSnapshot());

        Assert.NotEmpty(png);
        Assert.Equal(PngSignature, png.Take(PngSignature.Length));
    }

    // 2
    [Fact]
    public void AHistogramIsExportedAsAPng()
    {
        var png = new GraphExportService().RenderPng(HistogramSnapshot());

        Assert.NotEmpty(png);
        Assert.Equal(PngSignature, png.Take(PngSignature.Length));
    }

    // 2a
    [Fact]
    public void AProbabilityPlotIsExportedThroughTheSamePathAsEveryOtherGraph()
    {
        var model = ProbabilityPlot();
        var snapshot = new GraphExportSnapshot(model.Frame, new ProbabilityPlotRenderer(model), GraphThemes.Light);

        var png = new GraphExportService().RenderPng(snapshot);

        Assert.Equal(PngSignature, png.Take(PngSignature.Length));

        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(GraphExportService.ExportWidth, bitmap.Width);
        Assert.Equal(GraphExportService.ExportHeight, bitmap.Height);
        Assert.Equal(GraphThemes.Light.Background, bitmap.GetPixel(0, 0));
    }

    // 2c
    [Fact]
    public void ABoxPlotIsExportedThroughTheSamePathAsEveryOtherGraph()
    {
        var values = new double[120];
        var groups = new string?[120];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = index % 40;
            groups[index] = $"SITE{index % 3}";
        }

        var data = new MultiVariableGraphData(
            GraphType.BoxPlot,
            Guid.NewGuid(),
            [
                new UnivariateGraphData(
                    GraphType.BoxPlot,
                    Guid.NewGuid(),
                    Column("Reg1"),
                    values,
                    new StringGroupData(Column("SITE", WorksheetDataType.String), groups))
            ]);

        var model = new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels(["Reg1"], "SITE"), Token)!;

        // The same render model and renderer the window draws, given to the shared export path.
        var png = new GraphExportService().RenderPng(
            new GraphExportSnapshot(model.Frame, new BoxPlotRenderer(model), GraphThemes.Light));

        Assert.Equal(PngSignature, png.Take(PngSignature.Length));

        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(GraphExportService.ExportWidth, bitmap.Width);
        Assert.Equal(GraphExportService.ExportHeight, bitmap.Height);
        Assert.Equal(GraphThemes.Light.Background, bitmap.GetPixel(0, 0));
    }

    // 2b
    [Fact]
    public void AnEmpiricalCdfIsExportedThroughTheSamePathAsEveryOtherGraph()
    {
        var values = new double[120];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = 10 + (index * 0.25);
        }

        var model = new EmpiricalCdfRenderModelBuilder().Build(
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.NewGuid(), Column("Reg1"), values, null),
            new EmpiricalCdfLabels("Reg1"),
            Token)!;

        var png = new GraphExportService().RenderPng(
            new GraphExportSnapshot(model.Frame, new EmpiricalCdfRenderer(model), GraphThemes.Dark));

        Assert.Equal(PngSignature, png.Take(PngSignature.Length));

        using var bitmap = SKBitmap.Decode(png);
        Assert.Equal(GraphExportService.ExportWidth, bitmap.Width);
        Assert.Equal(GraphExportService.ExportHeight, bitmap.Height);
        Assert.Equal(GraphThemes.Dark.Background, bitmap.GetPixel(0, 0));
    }

    // 3
    [Fact]
    public void TheImageIsAlwaysTheExportSizeWhateverAWindowWouldHaveBeen()
    {
        using var scatter = SKBitmap.Decode(new GraphExportService().RenderPng(ScatterSnapshot()));
        using var histogram = SKBitmap.Decode(new GraphExportService().RenderPng(HistogramSnapshot()));

        Assert.Equal(GraphExportService.ExportWidth, scatter.Width);
        Assert.Equal(GraphExportService.ExportHeight, scatter.Height);
        Assert.Equal(1600, scatter.Width);
        Assert.Equal(1000, scatter.Height);
        Assert.Equal(scatter.Width, histogram.Width);
        Assert.Equal(scatter.Height, histogram.Height);
    }

    // 4
    [Fact]
    public void AnExportSizeCanBeAskedForExplicitlyAndIsHonoured()
    {
        using var bitmap = SKBitmap.Decode(new GraphExportService().RenderPng(ScatterSnapshot(), 800, 400));

        Assert.Equal(800, bitmap.Width);
        Assert.Equal(400, bitmap.Height);
    }

    // 5
    [Fact]
    public void TheImageIsOpaqueAndPaintedInTheThemeOfTheSnapshot()
    {
        using var light = SKBitmap.Decode(new GraphExportService().RenderPng(ScatterSnapshot(GraphThemes.Light)));
        using var dark = SKBitmap.Decode(new GraphExportService().RenderPng(ScatterSnapshot(GraphThemes.Dark)));

        Assert.Equal(GraphThemes.Light.Background, light.GetPixel(0, 0));
        Assert.Equal(GraphThemes.Dark.Background, dark.GetPixel(0, 0));
        Assert.Equal(255, light.GetPixel(0, 0).Alpha);
        Assert.Equal(255, dark.GetPixel(4, 4).Alpha);
        Assert.NotEqual(light.GetPixel(0, 0), dark.GetPixel(0, 0));
    }

    // 6
    [Fact]
    public void TheGraphItselfIsInTheImage()
    {
        using var bitmap = SKBitmap.Decode(new GraphExportService().RenderPng(ScatterSnapshot()));

        var drawn = 0;
        for (var y = 0; y < bitmap.Height; y += 4)
        {
            for (var x = 0; x < bitmap.Width; x += 4)
            {
                if (bitmap.GetPixel(x, y) == GraphThemes.Light.SeriesColor(0))
                {
                    drawn++;
                }
            }
        }

        Assert.True(drawn > 0, "the exported image has no scatter markers");
    }

    // 7
    [Fact]
    public void AGroupedGraphExportsItsSeriesColours()
    {
        var groups = new string?[60];
        for (var index = 0; index < groups.Length; index++)
        {
            groups[index] = index % 2 == 0 ? "A" : "B";
        }

        var model = Scatter(groups);
        var snapshot = new GraphExportSnapshot(model.Frame, new ScatterRenderer(model), GraphThemes.Light);

        using var bitmap = SKBitmap.Decode(new GraphExportService().RenderPng(snapshot));
        var colours = new HashSet<SKColor>();
        for (var y = 0; y < bitmap.Height; y += 2)
        {
            for (var x = 0; x < bitmap.Width; x += 2)
            {
                colours.Add(bitmap.GetPixel(x, y));
            }
        }

        Assert.Contains(GraphThemes.Light.SeriesColor(0), colours);
        Assert.Contains(GraphThemes.Light.SeriesColor(1), colours);
    }

    // 8
    [Fact]
    public void TheSameSnapshotAlwaysExportsTheSameImage()
    {
        var snapshot = HistogramSnapshot();
        var service = new GraphExportService();

        Assert.Equal(service.RenderPng(snapshot), service.RenderPng(snapshot));
    }

    // 9
    [Fact]
    public void AGraphWithNothingInThePlotAreaStillExports()
    {
        var range = new GraphAxisRange(0, 10);
        var frame = new GraphRenderModel(
            "Empty",
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "X"),
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "Y"));

        using var bitmap = SKBitmap.Decode(new GraphExportService().RenderPng(new GraphExportSnapshot(frame, null, GraphThemes.Light)));

        Assert.Equal(GraphExportService.ExportWidth, bitmap.Width);
    }

    // 10
    [Fact]
    public void TheBytesAreWrittenWhereTheyAreAskedFor()
    {
        using var directory = new TestDoubles.TemporaryDirectory();
        var path = directory.File("graph.png");
        var service = new GraphExportService();
        var png = service.RenderPng(ScatterSnapshot());

        service.Write(path, png);

        Assert.True(File.Exists(path));
        Assert.Equal(png, File.ReadAllBytes(path));
    }

    // 11
    [Fact]
    public void AnExportNeedsASnapshotAndAUsableSize()
    {
        var service = new GraphExportService();

        Assert.Throws<ArgumentNullException>(() => service.RenderPng(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.RenderPng(ScatterSnapshot(), 0, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => service.RenderPng(ScatterSnapshot(), 100, -1));
        Assert.Throws<ArgumentNullException>(() => new GraphExportSnapshot(null!, null, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => new GraphExportSnapshot(HistogramSnapshot().Frame, null, null!));
    }
}
