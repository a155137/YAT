using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The points and fitted lines of a probability plot, drawn into an off-screen surface through the same frame renderer
// the graph window uses.
public class ProbabilityPlotRendererTests
{
    private static readonly ProbabilityPlotLabels Labels = new("Reg1", "SITE");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static ProbabilityPlotRenderModel Model(double[] values, string?[]? groups = null) =>
        new ProbabilityPlotRenderModelBuilder().Build(
            new UnivariateGraphData(
                GraphType.ProbabilityPlot,
                Guid.NewGuid(),
                Column("Reg1"),
                values,
                groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups)),
            Labels,
            Token)!;

    private static double[] Spread(int count) =>
        [.. Enumerable.Range(0, count).Select(index => 10 + (index * 0.5))];

    private static SKBitmap Render(ProbabilityPlotRenderModel model, GraphTheme theme, int width = 640, int height = 480)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, width, height), theme, new ProbabilityPlotRenderer(model));
        return bitmap;
    }

    private static int CountOf(SKBitmap bitmap, SKColor color)
    {
        var found = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == color)
                {
                    found++;
                }
            }
        }

        return found;
    }

    private static int CountOtherThan(SKBitmap bitmap, SKColor color)
    {
        var found = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) != color)
                {
                    found++;
                }
            }
        }

        return found;
    }

    // 1
    [Fact]
    public void PointsAreDrawnInTheSeriesColourOfTheTheme()
    {
        using var bitmap = Render(Model(Spread(40)), GraphThemes.Light);

        Assert.True(CountOf(bitmap, GraphThemes.Light.SeriesColor(0)) > 0);
    }

    // 2
    [Fact]
    public void TheFittedLineIsDrawnAsWellAsThePoints()
    {
        // Skewed data, so the points curve away from the straight line their mean and standard deviation describe:
        // on perfectly normal data the markers would cover the line completely.
        var withLine = Model([.. Enumerable.Range(0, 40).Select(index => Math.Exp(index / 8d))]);
        Assert.NotNull(withLine.Series[0].FittedLine);

        // The same points without the line: the line has to add to what is drawn.
        var withoutLine = new ProbabilityPlotRenderModel(
            withLine.Frame,
            [new ProbabilityPlotSeriesRenderModel("", 0, withLine.Series[0].Points, null, withLine.Series[0].ObservationCount)],
            withLine.SourceObservationCount);

        using var lineBitmap = Render(withLine, GraphThemes.Light);
        using var pointsBitmap = Render(withoutLine, GraphThemes.Light);

        // The line is thin and drawn with antialiasing, so it is counted as what it adds to the picture rather than by
        // an exact colour: everything else in the two images is the same.
        var withLinePixels = CountOtherThan(lineBitmap, GraphThemes.Light.Background);
        var withoutLinePixels = CountOtherThan(pointsBitmap, GraphThemes.Light.Background);

        Assert.True(withLinePixels > withoutLinePixels, $"the fitted line added nothing ({withLinePixels} vs {withoutLinePixels})");
    }

    // 3
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

        Assert.True(CountOf(bitmap, GraphThemes.Light.SeriesColor(0)) > 0);
        Assert.True(CountOf(bitmap, GraphThemes.Light.SeriesColor(1)) > 0);
        Assert.True(CountOf(bitmap, GraphThemes.Light.SeriesColor(2)) > 0);
    }

    // 4
    [Fact]
    public void APlotWithoutSpreadIsDrawnAsPointsAlone()
    {
        var model = Model([5, 5, 5, 5, 5]);

        Assert.Null(model.Series[0].FittedLine);

        using var bitmap = Render(model, GraphThemes.Light);
        Assert.True(CountOf(bitmap, GraphThemes.Light.SeriesColor(0)) > 0);
    }

    // 5
    [Fact]
    public void NothingIsDrawnOutsideThePlotArea()
    {
        // A frame whose axes hold none of the data: the clip is what keeps the points off the labels and the title.
        var model = Model(Spread(40));
        var range = new GraphAxisRange(1_000, 1_100);
        var frame = new GraphRenderModel(
            "Clipped",
            new GraphAxisModel(range, GraphAxisTicks.Nice(range), "Reg1"),
            model.Frame.YAxis);

        var clipped = new ProbabilityPlotRenderModel(frame, model.Series, model.SourceObservationCount);

        using var bitmap = Render(clipped, GraphThemes.Light);
        Assert.Equal(0, CountOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 6
    [Fact]
    public void TheSamePlotIsDrawnInTheDarkTheme()
    {
        using var bitmap = Render(Model(Spread(40)), GraphThemes.Dark);

        Assert.Equal(GraphThemes.Dark.Background, bitmap.GetPixel(0, 0));
        Assert.True(CountOf(bitmap, GraphThemes.Dark.SeriesColor(0)) > 0);
    }

    // 7
    [Theory]
    [InlineData(320, 240)]
    [InlineData(1920, 1080)]
    public void APlotIsDrawnAtAnyUsableCanvasSize(int width, int height)
    {
        using var bitmap = Render(Model(Spread(200)), GraphThemes.Light, width, height);

        Assert.True(CountOf(bitmap, GraphThemes.Light.SeriesColor(0)) > 0);
    }

    // 8
    [Fact]
    public void ManyPointsAreDrawnInBatchesWithoutTrouble()
    {
        using var bitmap = Render(Model(Spread(50_000)), GraphThemes.Light, 1280, 720);

        Assert.True(CountOf(bitmap, GraphThemes.Light.SeriesColor(0)) > 0);
    }

    // 9
    [Fact]
    public void TheCanvasIsLeftExactlyAsItWasFound()
    {
        using var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        var matrix = canvas.TotalMatrix;
        var saveCount = canvas.SaveCount;
        var model = Model(Spread(20));

        new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, 640, 480), GraphThemes.Light, new ProbabilityPlotRenderer(model));

        Assert.Equal(saveCount, canvas.SaveCount);
        Assert.Equal(matrix, canvas.TotalMatrix);
    }

    // 10
    [Fact]
    public void TheRendererNeedsAModelACanvasATransformAndATheme()
    {
        using var bitmap = new SKBitmap(64, 64);
        using var canvas = new SKCanvas(bitmap);
        var renderer = new ProbabilityPlotRenderer(Model(Spread(5)));
        var transform = new GraphCoordinateTransform(new GraphAxisRange(0, 1), new GraphAxisRange(0, 1), new SKRect(0, 0, 64, 64));

        Assert.Throws<ArgumentNullException>(() => new ProbabilityPlotRenderer(null!));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(null!, transform, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, null!, GraphThemes.Light));
        Assert.Throws<ArgumentNullException>(() => renderer.RenderPlot(canvas, transform, null!));
    }
}
