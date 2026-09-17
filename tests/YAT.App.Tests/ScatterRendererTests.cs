using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The scatter markers, drawn into an off-screen surface through the same frame renderer the graph window uses. What is
// checked is behaviour - the colours of the series, the clip to the plot area, the canvas left as it was - not pixels.
public class ScatterRendererTests
{
    private static readonly ScatterPlotLabels Labels = new("Reg1", "Reg2", "SITE");

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static ScatterRenderModel Model(double[] x, double[] y, string?[]? groups = null) =>
        new ScatterRenderModelBuilder().Build(
            new ScatterGraphData(
                Guid.NewGuid(),
                Column("Reg1"),
                Column("Reg2"),
                x,
                y,
                groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups)),
            Labels,
            TestContext.Current.CancellationToken)!;

    private static SKBitmap Render(ScatterRenderModel model, GraphTheme theme, int width = 640, int height = 480)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, width, height), theme, new ScatterRenderer(model));
        return bitmap;
    }

    private static List<SKPointI> PixelsOf(SKBitmap bitmap, SKColor color)
    {
        var found = new List<SKPointI>();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == color)
                {
                    found.Add(new SKPointI(x, y));
                }
            }
        }

        return found;
    }

    // 1
    [Fact]
    public void MarkersAreDrawnInTheSeriesColourOfTheTheme()
    {
        using var bitmap = Render(Model([1, 2, 3], [10, 20, 30]), GraphThemes.Light);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 2
    [Fact]
    public void EveryGroupIsDrawnInItsOwnColour()
    {
        using var bitmap = Render(Model([1, 2, 3], [10, 20, 30], ["A", "B", null]), GraphThemes.Light);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(1)));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(2)));
    }

    // 3
    [Fact]
    public void TheSameMarkersAreDrawnInTheDarkTheme()
    {
        using var bitmap = Render(Model([1, 2, 3], [10, 20, 30]), GraphThemes.Dark);

        Assert.Equal(GraphThemes.Dark.Background, bitmap.GetPixel(0, 0));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Dark.SeriesColor(0)));
    }

    // 4
    [Fact]
    public void MarkersStayInsideThePlotArea()
    {
        using var bitmap = Render(Model([0, 50, 100], [0, 250, 500]), GraphThemes.Light);

        var markers = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));

        Assert.NotEmpty(markers);

        // The plot area never reaches the edge of the canvas: the frame around it needs the space.
        Assert.All(markers, pixel =>
        {
            Assert.InRange(pixel.X, bitmap.Width / 20, bitmap.Width - (bitmap.Width / 20));
            Assert.InRange(pixel.Y, bitmap.Height / 20, bitmap.Height - (bitmap.Height / 20));
        });
    }

    // 5
    [Fact]
    public void APointOutsideTheAxisRangesIsClippedAway()
    {
        // A frame whose ranges do not contain the point: the transform still places it (far outside), and the clip to
        // the plot area is what keeps it off the labels, the titles and the window.
        var range = new GraphAxisRange(0, 1);
        var frame = new GraphRenderModel(
            "Clipped",
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "Reg1"),
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "Reg2"));
        var model = new ScatterRenderModel(
            frame,
            [new ScatterSeriesRenderModel("out", 0, new ScatterPoint[] { new(1000, 1000), new(-1000, -1000) })],
            2);

        var bitmap = new SKBitmap(640, 480);
        using (var canvas = new SKCanvas(bitmap))
        {
            new SkiaGraphRenderer().Render(canvas, frame, new SKRect(0, 0, 640, 480), GraphThemes.Light, new ScatterRenderer(model));
        }

        using (bitmap)
        {
            Assert.Empty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
        }
    }

    // 6
    [Fact]
    public void AGraphWithoutGroupsNeedsNoLegend()
    {
        var model = Model([1, 2, 3], [10, 20, 30]);

        Assert.Null(model.Frame.Legend);

        using var bitmap = Render(model, GraphThemes.Light);
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 7
    [Fact]
    public void ManyMarkersAreDrawnInBatchesWithoutTrouble()
    {
        var count = 50_000;
        var x = new double[count];
        var y = new double[count];
        for (var index = 0; index < count; index++)
        {
            x[index] = index;
            y[index] = Math.Sin(index / 500d) * 100;
        }

        using var bitmap = Render(Model(x, y), GraphThemes.Light, 1280, 720);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 8
    [Fact]
    public void TheCanvasIsLeftExactlyAsItWasFound()
    {
        using var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        var matrix = canvas.TotalMatrix;
        var saveCount = canvas.SaveCount;
        var model = Model([1, 2, 3], [10, 20, 30], ["A", "B", "A"]);

        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, 640, 480), GraphThemes.Light, new ScatterRenderer(model));

        Assert.Equal(saveCount, canvas.SaveCount);
        Assert.Equal(matrix, canvas.TotalMatrix);
    }

    // 9
    [Fact]
    public void TheRendererNeedsAModelACanvasATransformAndATheme()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        var renderer = new ScatterRenderer(Model([1, 2], [3, 4]));
        var transform = new GraphCoordinateTransform(new GraphAxisRange(0, 1), new GraphAxisRange(0, 1), new SKRect(0, 0, 64, 64));

        Assert.Throws<ArgumentNullException>(() => new ScatterRenderer(null!));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(null!, transform, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, null!, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, transform, null!));
    }
}
