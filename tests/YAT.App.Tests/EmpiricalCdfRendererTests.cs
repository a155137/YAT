using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The step functions of an empirical CDF, drawn into an off-screen surface through the same frame renderer the graph
// window uses.
public class EmpiricalCdfRendererTests
{
    private static readonly EmpiricalCdfLabels Labels = new("Reg1", "SITE");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static EmpiricalCdfRenderModel Model(double[] values, string?[]? groups = null) =>
        new EmpiricalCdfRenderModelBuilder().Build(
            new UnivariateGraphData(
                GraphType.EmpiricalCdf,
                Guid.NewGuid(),
                Column("Reg1"),
                values,
                groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups)),
            Labels,
            Token)!;

    private static SKBitmap Render(EmpiricalCdfRenderModel model, GraphTheme theme, int width = 640, int height = 480)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, width, height), theme, new EmpiricalCdfRenderer(model));
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
    public void TheStepsAreDrawnInTheSeriesColourOfTheTheme()
    {
        using var bitmap = Render(Model([1, 2, 3, 4, 5]), GraphThemes.Light);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 2
    [Fact]
    public void TheCurveReachesBothEndsOfTheAxis()
    {
        // Nothing of the sample is below its smallest observation, and all of it is at or below its largest: the curve
        // says so across the whole axis.
        using var bitmap = Render(Model([10, 11, 12]), GraphThemes.Light);
        var drawn = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));

        Assert.Contains(drawn, pixel => pixel.X < bitmap.Width / 4);
        Assert.Contains(drawn, pixel => pixel.X > bitmap.Width * 3 / 4);
    }

    // 3
    [Fact]
    public void BetweenTwoObservationsTheCurveIsFlat()
    {
        // Two steps far apart: everything between them must be drawn at one height, not sloping from one to the other.
        var model = Model([0, 100]);
        using var bitmap = Render(model, GraphThemes.Light);

        var drawn = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));
        Assert.NotEmpty(drawn);

        // The middle half of the canvas is inside the flat stretch between the two observations.
        var middle = drawn
            .Where(pixel => pixel.X > bitmap.Width * 3 / 8 && pixel.X < bitmap.Width * 5 / 8)
            .GroupBy(pixel => pixel.X)
            .Select(column => column.Min(pixel => pixel.Y))
            .Distinct()
            .ToArray();

        Assert.NotEmpty(middle);
        Assert.Single(middle);
    }

    // 4
    [Fact]
    public void EveryGroupIsDrawnInItsOwnColour()
    {
        var values = new double[60];
        var groups = new string?[60];
        for (var index = 0; index < 60; index++)
        {
            values[index] = index % 3 == 0 ? index : index * 1.5;
            groups[index] = index % 3 == 0 ? "A" : index % 3 == 1 ? "B" : null;
        }

        using var bitmap = Render(Model(values, groups), GraphThemes.Light);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(1)));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(2)));
    }

    // 5
    [Fact]
    public void ADistributionWithNoSpreadIsStillDrawn()
    {
        using var bitmap = Render(Model([5, 5, 5, 5]), GraphThemes.Light);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 6
    [Fact]
    public void NothingIsDrawnOutsideThePlotArea()
    {
        // A frame whose X axis holds none of the data: the clip is what keeps the curve off the labels and the title.
        var model = Model([1, 2, 3]);
        var range = new GraphAxisRange(1_000, 1_100);
        var frame = new GraphRenderModel(
            "Clipped",
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "Reg1"),
            model.Frame.YAxis);

        using var bitmap = Render(new EmpiricalCdfRenderModel(frame, model.Series, model.SourceObservationCount), GraphThemes.Light);
        var drawn = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));

        Assert.All(drawn, pixel =>
        {
            Assert.InRange(pixel.X, 1, bitmap.Width - 2);
            Assert.True(pixel.Y > 30, $"a step at y = {pixel.Y} is drawn over the title");
        });
    }

    // 7
    [Fact]
    public void TheSameDistributionIsDrawnInTheDarkTheme()
    {
        using var bitmap = Render(Model([1, 2, 3, 4]), GraphThemes.Dark);

        Assert.Equal(GraphThemes.Dark.Background, bitmap.GetPixel(0, 0));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Dark.SeriesColor(0)));
    }

    // 8
    [Theory]
    [InlineData(320, 240)]
    [InlineData(1920, 1080)]
    public void ADistributionIsDrawnAtAnyUsableCanvasSize(int width, int height)
    {
        using var bitmap = Render(Model([.. Enumerable.Range(0, 300).Select(index => (double)index)]), GraphThemes.Light, width, height);

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 9
    [Fact]
    public void ManyStepsAreDrawnWithoutTrouble()
    {
        using var bitmap = Render(Model([.. Enumerable.Range(0, 50_000).Select(index => index * 0.001)]), GraphThemes.Light, 1280, 720);

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

        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, 640, 480), GraphThemes.Light, new EmpiricalCdfRenderer(model));

        Assert.Equal(saveCount, canvas.SaveCount);
        Assert.Equal(matrix, canvas.TotalMatrix);
    }

    // 11
    [Fact]
    public void TheRendererNeedsAModelACanvasATransformAndATheme()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        var renderer = new EmpiricalCdfRenderer(Model([1, 2, 3]));
        var transform = new GraphCoordinateTransform(new GraphAxisRange(0, 1), new GraphAxisRange(0, 1), new SKRect(0, 0, 64, 64));

        Assert.Throws<ArgumentNullException>(() => new EmpiricalCdfRenderer(null!));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(null!, transform, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, null!, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, transform, null!));
    }
}
