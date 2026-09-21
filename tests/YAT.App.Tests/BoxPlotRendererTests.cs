using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The boxes of a box plot, drawn into an off-screen surface through the same frame renderer the graph window uses.
public class BoxPlotRendererTests
{
    private const int Width = 640;
    private const int Height = 480;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static MultiVariableGraphData Data(params (string Name, double[] Values, string?[]? Groups)[] variables) =>
        new(GraphType.BoxPlot,
            Guid.NewGuid(),
            [
                .. variables.Select(variable => new UnivariateGraphData(
                    GraphType.BoxPlot,
                    Guid.NewGuid(),
                    Column(variable.Name),
                    variable.Values,
                    variable.Groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), variable.Groups)))
            ]);

    private static BoxPlotRenderModel Model(MultiVariableGraphData data, string? group = null) =>
        new BoxPlotRenderModelBuilder().Build(
            data, new BoxPlotLabels([.. data.Variables.Select(variable => variable.Variable.Name)], group), Token)!;

    // Renders the graph the way the window does, and keeps the transform the frame laid the plot out with, so a test
    // can say where a value ended up without measuring fonts itself.
    private sealed class Capturing(IGraphPlotRenderer inner) : IGraphPlotRenderer
    {
        public GraphCoordinateTransform? Transform { get; private set; }

        public void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme)
        {
            Transform = transform;
            inner.RenderPlot(canvas, transform, theme);
        }
    }

    private static (SKBitmap Bitmap, GraphCoordinateTransform Transform) Render(BoxPlotRenderModel model, GraphTheme theme)
    {
        var bitmap = new SKBitmap(Width, Height);
        var plot = new Capturing(new BoxPlotRenderer(model));
        using (var canvas = new SKCanvas(bitmap))
        {
            new SkiaGraphRenderer().Render(canvas, model.Frame, new SKRect(0, 0, Width, Height), theme, plot);
        }

        Assert.NotNull(plot.Transform);
        return (bitmap, plot.Transform);
    }

    // Whether anything was drawn within radius pixels of (x, y): antialiased strokes are rarely the pure series
    // colour, so geometry that is thin is looked for as ink on the plot background.
    private static bool HasInk(SKBitmap bitmap, int x, int y, int radius)
    {
        for (var offsetY = -radius; offsetY <= radius; offsetY++)
        {
            for (var offsetX = -radius; offsetX <= radius; offsetX++)
            {
                var point = new SKPointI(x + offsetX, y + offsetY);
                if (point.X < 0 || point.Y < 0 || point.X >= bitmap.Width || point.Y >= bitmap.Height)
                {
                    continue;
                }

                if (bitmap.GetPixel(point.X, point.Y) != GraphThemes.Light.PlotBackground)
                {
                    return true;
                }
            }
        }

        return false;
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

    // 0
    [Fact]
    public void TheBoxIsDrawnInTheSeriesColourOfItsTheme()
    {
        var model = Model(Data(("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9], null)));

        var (light, _) = Render(model, GraphThemes.Light);
        var (dark, _) = Render(model, GraphThemes.Dark);
        using var lightBitmap = light;
        using var darkBitmap = dark;

        Assert.NotEmpty(PixelsOf(light, GraphThemes.Light.SeriesColor(0)));
        Assert.NotEmpty(PixelsOf(dark, GraphThemes.Dark.SeriesColor(0)));
    }

    // 1
    [Fact]
    public void TheBoxStandsWhereItsCategoryAndItsQuartilesSay()
    {
        var model = Model(Data(("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9], null)));
        var (bitmap, transform) = Render(model, GraphThemes.Light);
        using var rendered = bitmap;

        var drawn = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));

        // The drawing sits around the category's centre, and between its whiskers.
        var centre = transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(0));
        var top = transform.ToScreenY(model.Boxes[0].UpperWhisker);
        var bottom = transform.ToScreenY(model.Boxes[0].LowerWhisker);

        Assert.All(drawn, point => Assert.InRange(point.X, centre - BoxPlotRenderer.MaximumBoxWidth, centre + BoxPlotRenderer.MaximumBoxWidth));
        Assert.All(drawn, point => Assert.InRange(point.Y, top - BoxPlotRenderer.MeanMarkerSize, bottom + BoxPlotRenderer.MeanMarkerSize));

        // The box body is filled, so the middle of the box is coloured rather than blank.
        var inside = bitmap.GetPixel((int)centre, (int)transform.ToScreenY((model.Boxes[0].FirstQuartile + model.Boxes[0].Median) / 2));
        Assert.NotEqual(GraphThemes.Light.PlotBackground, inside);
    }

    // 2
    [Fact]
    public void TheMedianIsDrawnAcrossTheBox()
    {
        var model = Model(Data(("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9], null)));
        var (bitmap, transform) = Render(model, GraphThemes.Light);
        using var rendered = bitmap;

        var median = (int)Math.Round(transform.ToScreenY(model.Boxes[0].Median));
        var centre = (int)Math.Round(transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(0)));

        // The median line is the widest thing at its own height: it spans the box body.
        var onMedian = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)).Where(point => Math.Abs(point.Y - median) <= 1).ToArray();
        Assert.NotEmpty(onMedian);
        Assert.True(onMedian.Max(point => point.X) - onMedian.Min(point => point.X) >= BoxPlotRenderer.MinimumBoxWidth);
        Assert.Contains(onMedian, point => Math.Abs(point.X - centre) <= 2);
    }

    // 3
    [Fact]
    public void TheWhiskersReachTheirEndsAndAreCapped()
    {
        var model = Model(Data(("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9], null)));
        var (bitmap, transform) = Render(model, GraphThemes.Light);
        using var rendered = bitmap;

        var drawn = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0));

        foreach (var whisker in (double[])[model.Boxes[0].LowerWhisker, model.Boxes[0].UpperWhisker])
        {
            var y = (int)Math.Round(transform.ToScreenY(whisker));
            var cap = drawn.Where(point => Math.Abs(point.Y - y) <= 1).ToArray();

            // A cap is a horizontal line, narrower than the box body.
            Assert.NotEmpty(cap);
            Assert.True(cap.Max(point => point.X) - cap.Min(point => point.X) >= 2);
        }
    }

    // 4
    [Fact]
    public void TheMeanIsDrawnAsItsOwnMarkerAwayFromTheMedian()
    {
        // A sample whose mean is pulled well above its median by one large observation.
        var model = Model(Data(("Reg1", [1, 1, 1, 1, 2, 2, 2, 3, 3, 40], null)));
        var (bitmap, transform) = Render(model, GraphThemes.Light);
        using var rendered = bitmap;

        var mean = (int)Math.Round(transform.ToScreenY(model.Boxes[0].Mean));
        var median = (int)Math.Round(transform.ToScreenY(model.Boxes[0].Median));
        var centre = (int)Math.Round(transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(0)));

        // The mean sits well away from the median here, and its marker is drawn there - the cross is antialiased, so
        // the test asks for ink rather than for one exact colour.
        Assert.True(Math.Abs(mean - median) > 5);
        Assert.True(HasInk(bitmap, centre, mean, (int)BoxPlotRenderer.MeanMarkerSize / 2));

        // It is a cross, not a line: its arms run diagonally out to all four corners of its own square.
        var arm = (int)BoxPlotRenderer.MeanMarkerSize / 2;
        Assert.True(HasInk(bitmap, centre - arm, mean - arm, 1));
        Assert.True(HasInk(bitmap, centre + arm, mean + arm, 1));
        Assert.True(HasInk(bitmap, centre - arm, mean + arm, 1));
        Assert.True(HasInk(bitmap, centre + arm, mean - arm, 1));
    }

    // 5
    [Fact]
    public void OutliersAreDrawnOnTheCategoriesCentreLine()
    {
        var model = Model(Data(("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9, 1000], null)));
        var (bitmap, transform) = Render(model, GraphThemes.Light);
        using var rendered = bitmap;

        var outlier = (int)Math.Round(transform.ToScreenY(1000));
        var centre = (int)Math.Round(transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(0)));

        var drawn = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)).Where(point => Math.Abs(point.Y - outlier) <= 2).ToArray();
        Assert.NotEmpty(drawn);
        Assert.All(drawn, point => Assert.InRange(point.X, centre - BoxPlotRenderer.OutlierDiameter, centre + BoxPlotRenderer.OutlierDiameter));
    }

    // 6
    [Fact]
    public void EveryCategoryIsDrawnInItsOwnSlot()
    {
        var model = Model(Data(("Reg1", [1, 2, 3], null), ("Reg2", [4, 5, 6], null), ("Reg3", [7, 8, 9], null)));
        var (bitmap, transform) = Render(model, GraphThemes.Light);
        using var rendered = bitmap;

        for (var index = 0; index < 3; index++)
        {
            var centre = transform.ToScreenX(BoxPlotRenderModelBuilder.CategoryPosition(index));
            var drawn = PixelsOf(bitmap, GraphThemes.Light.SeriesColor(index));

            Assert.NotEmpty(drawn);
            Assert.All(drawn, point => Assert.InRange(point.X, centre - BoxPlotRenderer.MaximumBoxWidth, centre + BoxPlotRenderer.MaximumBoxWidth));
        }
    }

    // 7
    [Fact]
    public void GroupedBoxesOfTheSameGroupShareTheirColourAcrossVariables()
    {
        var model = Model(
            Data(("Reg1", [1, 2, 3, 4], ["A", "B", "A", "B"]), ("Reg2", [5, 6, 7, 8], ["B", "A", "B", "A"])),
            "SITE");

        var (bitmap, _) = Render(model, GraphThemes.Light);
        using var drawnBitmap = bitmap;

        // Two groups, four boxes, two colours.
        Assert.Equal(4, model.Boxes.Count);
        Assert.Equal([0, 1], model.Boxes.Select(box => box.SeriesIndex).Distinct().Order());
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(1)));
    }

    // 8
    [Fact]
    public void ConstantDataIsDrawnWithoutAnythingDegenerate()
    {
        var model = Model(Data(("Reg1", [100, 100, 100, 100], null)));

        var (bitmap, _) = Render(model, GraphThemes.Light);
        using var drawnBitmap = bitmap;

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 9
    [Fact]
    public void ASingleObservationIsDrawnAsAFlatBox()
    {
        var model = Model(Data(("Reg1", [42], null)));

        var (bitmap, _) = Render(model, GraphThemes.Light);
        using var drawnBitmap = bitmap;

        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

    // 10
    [Fact]
    public void ManyCategoriesKeepTheirBoxesApart()
    {
        var variables = Enumerable.Range(0, 12)
            .Select(index => ($"Reg{index}", new double[] { index, index + 1, index + 2 }, (string?[]?)null))
            .ToArray();

        var model = Model(Data(variables));
        var (bitmap, _) = Render(model, GraphThemes.Light);
        using var drawnBitmap = bitmap;

        Assert.Equal(12, model.Boxes.Count);
        Assert.NotEmpty(PixelsOf(bitmap, GraphThemes.Light.SeriesColor(0)));
    }

}
