using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The histogram bars, drawn into an off-screen surface through the same frame renderer the graph window uses. What is
// checked is behaviour - where the bars are, which colours they use, that a grouped one is see-through - not pixels.
public class HistogramRendererTests
{
    private static readonly HistogramPlotLabels Labels = new("Reg1", "SITE");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static HistogramRenderModel Model(double[] values, string?[]? groups = null) =>
        new HistogramRenderModelBuilder().Build(
            new UnivariateGraphData(
                GraphType.Histogram,
                Guid.NewGuid(),
                Column("Reg1"),
                values,
                groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups)),
            Labels,
            Token)!;

    private static SKBitmap Render(HistogramRenderModel model, GraphTheme theme, int width = 640, int height = 480)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, width, height), theme, new HistogramRenderer(model));
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
    public void BarsAreDrawnInTheSeriesColourOfTheTheme()
    {
        using var bitmap = Render(Model([1, 2, 2, 3, 3, 3, 4]), GraphThemes.Light);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 2
    [Fact]
    public void AnUngroupedHistogramIsDrawnSolidAndAGroupedOneSeeThrough()
    {
        var values = new double[60];
        var groups = new string?[60];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = index;
            groups[index] = index < 30 ? "A" : "B";
        }

        using var solid = Render(Model(values), GraphThemes.Light);
        using var overlaid = Render(Model(values, groups), GraphThemes.Light);

        var solidPixels = PixelsOf(solid, GraphThemes.Light.SeriesColor(0)).Count;
        var overlaidPixels = PixelsOf(overlaid, GraphThemes.Light.SeriesColor(0)).Count;

        // Solid bars are filled with the series colour itself; a grouped series keeps it only in its outline.
        Assert.True(solidPixels > 1_000, $"{solidPixels} solid pixels");
        Assert.True(overlaidPixels > 0, "the grouped series has no outline");
        Assert.True(overlaidPixels < solidPixels / 4, $"{overlaidPixels} outline pixels is not see-through next to {solidPixels}");
    }

    // 3
    [Fact]
    public void EveryGroupIsDrawnInItsOwnColour()
    {
        using var bitmap = Render(Model([1, 2, 3, 4, 5, 6], ["A", "B", null, "A", "B", null]), GraphThemes.Light);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(1)));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(2)));
    }

    // 4
    [Fact]
    public void BarsStandWhereTheirBinsAre()
    {
        // Four values over 0..30 are three bins; only the first one is filled, so nothing may be drawn on the right.
        var model = Model([0, 1, 2, 30]);
        Assert.Equal([3, 0, 1], Assert.Single(model.Series).Counts);

        using var bitmap = Render(model, GraphThemes.Light);
        var bars = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));

        Assert.NotEmpty(bars);

        // The tall bar is the left third of the plot, the single count of the last bin barely leaves the axis.
        var tall = bars.Where(pixel => pixel.Y < bitmap.Height / 2).ToList();
        Assert.NotEmpty(tall);
        Assert.All(tall, pixel => Assert.True(pixel.X < bitmap.Width / 2, $"a tall bar at x = {pixel.X}"));
    }

    // 5
    [Fact]
    public void BarsAreClippedToThePlotArea()
    {
        // A frame whose frequency axis is far shorter than the bar: the clip is what keeps the bar off the title.
        var range = new GraphAxisRange(0, 10);
        var counts = GraphAxisTicks.NiceCounts(5);
        var frame = new GraphRenderModel(
            "Clipped",
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "Reg1"),
            new GraphAxisModel(counts.Range, counts.Ticks, "Frequency"));
        var model = new HistogramRenderModel(
            frame,
            [new HistogramBin(0, 10)],
            [new HistogramSeriesRenderModel("tall", 0, [1_000])],
            1_000);

        using var bitmap = Render(model, GraphThemes.Light);
        var bars = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));

        Assert.NotEmpty(bars);
        Assert.All(bars, pixel =>
        {
            Assert.InRange(pixel.X, 1, bitmap.Width - 2);

            // The plot area starts well below the graph title; a bar taller than its axis must not reach it.
            Assert.True(pixel.Y > 30, $"a bar at y = {pixel.Y} is drawn over the title");
        });
    }

    // 6
    [Fact]
    public void AHistogramWithoutGroupsNeedsNoLegend()
    {
        var model = Model([1, 2, 3, 4]);

        Assert.Null(model.Frame.Legend);

        using var bitmap = Render(model, GraphThemes.Light);
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 7
    [Fact]
    public void AGroupedHistogramCarriesItsLegend()
    {
        var model = Model([1, 2, 3, 4], ["A", "B", "A", "B"]);

        Assert.NotNull(model.Frame.Legend);
        Assert.Equal(["A", "B"], model.Frame.Legend.Entries.Select(entry => entry.Label));

        using var bitmap = Render(model, GraphThemes.Light);
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(1)));
    }

    // 8
    [Fact]
    public void TheSameHistogramIsDrawnInTheDarkTheme()
    {
        using var bitmap = Render(Model([1, 2, 2, 3]), GraphThemes.Dark);

        Assert.Equal(GraphThemes.Dark.Background, bitmap.GetPixel(0, 0));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Dark.SeriesColor(0)));
    }

    // 9
    [Theory]
    [InlineData(320, 240)]
    [InlineData(1920, 1080)]
    [InlineData(260, 900)]
    public void AHistogramIsDrawnAtAnyUsableCanvasSize(int width, int height)
    {
        using var bitmap = Render(Model([.. Enumerable.Range(0, 300).Select(value => (double)(value % 40))]), GraphThemes.Light, width, height);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 10
    [Fact]
    public void TheCanvasIsLeftExactlyAsItWasFound()
    {
        using var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        var matrix = canvas.TotalMatrix;
        var saveCount = canvas.SaveCount;
        var model = Model([1, 2, 3, 4], ["A", "B", "A", "B"]);

        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, 640, 480), GraphThemes.Light, new HistogramRenderer(model));

        Assert.Equal(saveCount, canvas.SaveCount);
        Assert.Equal(matrix, canvas.TotalMatrix);
    }

    // 11
    [Fact]
    public void TheRendererNeedsAModelACanvasATransformAndATheme()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        var renderer = new HistogramRenderer(Model([1, 2, 3]));
        var transform = new GraphCoordinateTransform(new GraphAxisRange(0, 1), new GraphAxisRange(0, 1), new SKRect(0, 0, 64, 64));

        Assert.Throws<ArgumentNullException>(() => new HistogramRenderer(null!));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(null!, transform, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, null!, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, transform, null!));
    }
}
