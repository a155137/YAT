using SkiaSharp;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// The renderer drawing into an off-screen surface, the same way the graph window draws into Avalonia's. These are
// behaviour checks, not pixel comparisons: what must be true is that a graph is painted in the theme it was given, that
// it survives any canvas size, and that it leaves the canvas as it found it.
public class SkiaGraphRendererTests
{
    private static GraphAxisModel Axis(double minimum, double maximum, string? title) =>
        new(new GraphAxisRange(minimum, maximum), GraphAxisTicks.Evenly(new GraphAxisRange(minimum, maximum)), title);

    private static GraphRenderModel Model(
        string? title = "Sample Graph",
        string? xTitle = "X Axis",
        string? yTitle = "Y Axis",
        GraphLegendModel? legend = null) =>
        new(title, Axis(0, 100, xTitle), Axis(0, 500, yTitle), legend);

    private static SKBitmap Render(GraphRenderModel model, GraphTheme theme, int width = 640, int height = 480, float scale = 1f)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(scale);
        new SkiaGraphRenderer().Render(canvas, model, new SKRect(0, 0, width / scale, height / scale), theme);
        return bitmap;
    }

    private static IEnumerable<SKColor> Pixels(SKBitmap bitmap)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                yield return bitmap.GetPixel(x, y);
            }
        }
    }

    private static int CountOtherThan(SKBitmap bitmap, SKColor color) => Pixels(bitmap).Count(pixel => pixel != color);

    // 1
    [Fact]
    public void TheWholeCanvasIsPaintedInTheThemeBackground()
    {
        using var bitmap = Render(Model(), GraphThemes.Light);

        Assert.Equal(GraphThemes.Light.Background, bitmap.GetPixel(0, 0));
        Assert.Equal(GraphThemes.Light.Background, bitmap.GetPixel(bitmap.Width - 1, bitmap.Height - 1));
    }

    // 2
    [Fact]
    public void TheSameGraphLooksDifferentInTheDarkTheme()
    {
        using var light = Render(Model(), GraphThemes.Light);
        using var dark = Render(Model(), GraphThemes.Dark);

        Assert.Equal(GraphThemes.Light.Background, light.GetPixel(0, 0));
        Assert.Equal(GraphThemes.Dark.Background, dark.GetPixel(0, 0));
        Assert.NotEqual(light.GetPixel(0, 0), dark.GetPixel(0, 0));
    }

    // 3
    [Fact]
    public void AGraphDrawsItsFrameOverTheBackground()
    {
        using var bitmap = Render(Model(), GraphThemes.Light);

        // Axes, grid lines, ticks and text: far more than a stray pixel, and nothing like a full canvas.
        var painted = CountOtherThan(bitmap, GraphThemes.Light.Background);
        Assert.InRange(painted, 500, bitmap.Width * bitmap.Height / 2);
    }

    // 4
    [Fact]
    public void ALegendIsDrawnInTheSeriesColoursOfTheTheme()
    {
        var legend = new GraphLegendModel([new GraphLegendEntry("Lot A", 0), new GraphLegendEntry("Lot B", 1)], "Lot");
        using var bitmap = Render(Model(legend: legend), GraphThemes.Light);

        var colours = Pixels(bitmap).ToHashSet();
        Assert.Contains(GraphThemes.Light.SeriesColor(0), colours);
        Assert.Contains(GraphThemes.Light.SeriesColor(1), colours);
    }

    // 5
    [Fact]
    public void AGraphWithoutSeriesDrawsNoLegendColours()
    {
        using var bitmap = Render(Model(), GraphThemes.Light);

        var colours = Pixels(bitmap).ToHashSet();
        Assert.DoesNotContain(GraphThemes.Light.SeriesColor(0), colours);
    }

    // 6
    [Theory]
    [InlineData(32, 24)]
    [InlineData(120, 90)]
    [InlineData(1, 1)]
    public void ACanvasTooSmallForAPlotKeepsTheBackgroundAndNothingElse(int width, int height)
    {
        using var bitmap = Render(Model(), GraphThemes.Dark, width, height);

        Assert.Equal(0, CountOtherThan(bitmap, GraphThemes.Dark.Background));
    }

    // 7
    [Theory]
    [InlineData(320, 240)]
    [InlineData(640, 480)]
    [InlineData(1920, 1080)]
    [InlineData(240, 1200)]
    [InlineData(2400, 200)]
    public void AGraphIsDrawnAtAnyUsableCanvasSize(int width, int height)
    {
        using var bitmap = Render(Model(), GraphThemes.Light, width, height);

        Assert.True(CountOtherThan(bitmap, GraphThemes.Light.Background) > 0);
    }

    // 8
    [Fact]
    public void AScaledCanvasFillsTheSameAreaAsAnUnscaledOne()
    {
        // What a high DPI window does: the same graph in the same layout units on a canvas that scales every unit.
        using var bitmap = Render(Model(), GraphThemes.Light, 800, 600, scale: 2f);

        Assert.Equal(GraphThemes.Light.Background, bitmap.GetPixel(799, 599));
        Assert.True(CountOtherThan(bitmap, GraphThemes.Light.Background) > 0);
    }

    // 9
    [Fact]
    public void TheCanvasIsLeftExactlyAsItWasFound()
    {
        using var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        var matrix = canvas.TotalMatrix;
        var saveCount = canvas.SaveCount;

        var legend = new GraphLegendModel([new GraphLegendEntry("Lot A", 0)]);
        new SkiaGraphRenderer().Render(canvas, Model(legend: legend), new SKRect(0, 0, 640, 480), GraphThemes.Dark);

        Assert.Equal(saveCount, canvas.SaveCount);
        Assert.Equal(matrix, canvas.TotalMatrix);
    }

    // 10
    [Fact]
    public void AGraphWithoutTitlesIsDrawnJustAsWell()
    {
        using var bitmap = Render(Model(title: null, xTitle: null, yTitle: null), GraphThemes.Light);

        Assert.True(CountOtherThan(bitmap, GraphThemes.Light.Background) > 0);
    }

    // 11
    [Fact]
    public void AnEmptyCanvasIsNotDrawnOnAtAll()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);

        var renderer = new SkiaGraphRenderer();
        renderer.Render(canvas, Model(), new SKRect(0, 0, 0, 0), GraphThemes.Light);
        renderer.Render(canvas, Model(), new SKRect(0, 0, -10, 20), GraphThemes.Light);
        renderer.Render(canvas, Model(), new SKRect(0, 0, float.NaN, 20), GraphThemes.Light);

        Assert.All(Pixels(bitmap), pixel => Assert.Equal(default, pixel));
    }

    // 12
    [Fact]
    public void TheRendererNeedsACanvasAModelAndATheme()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        var renderer = new SkiaGraphRenderer();

        var bounds = new SKRect(0, 0, 64, 64);
        Assert.Throws<ArgumentNullException>(() => renderer.Render(null!, Model(), bounds, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.Render(canvas, null!, bounds, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.Render(canvas, Model(), bounds, null!));
    }
}
