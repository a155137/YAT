using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// How specification lines are laid out and drawn: across the plot at their X values through the frame's transform,
// clipped to the plot, with their labels in a band above it - the same in the window, the PNG and the slide. Without
// lines everything is laid out and drawn exactly as before.
public class SpecificationLinesRenderingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly SKRect Canvas = new(0, 0, 800, 600);

    private static GraphAxisModel Axis(double minimum, double maximum, string? title) =>
        new(new GraphAxisRange(minimum, maximum), GraphAxisTicks.Nice(new GraphAxisRange(minimum, maximum)), title);

    private static GraphRenderModel Model(GraphLegendModel? legend = null) =>
        new("Sample Graph", Axis(0, 100, "X Axis"), Axis(0, 500, "Y Axis"), legend);

    private static GraphReferenceLine Line(double value, string label = "LSL", GraphReferenceLineKind kind = GraphReferenceLineKind.SpecificationLimit) =>
        new(GraphReferenceAxis.X, value, $"{label} {value}", kind);

    private static GraphRenderModel WithLines(GraphRenderModel model, params GraphReferenceLine[] lines) => model.WithReferenceLines(lines);

    private static SKBitmap Render(GraphRenderModel model, int width = 800, int height = 600, GraphTheme? theme = null)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, model, new SKRect(0, 0, width, height), theme ?? GraphThemes.Light);
        return bitmap;
    }

    private static bool Identical(SKBitmap first, SKBitmap second) =>
        first.Width == second.Width && first.Height == second.Height && first.Bytes.SequenceEqual(second.Bytes);

    // Every pixel where two drawings of the same size differ.
    private static List<(int X, int Y)> Differences(SKBitmap first, SKBitmap second)
    {
        var differences = new List<(int, int)>();
        for (var y = 0; y < first.Height; y++)
        {
            for (var x = 0; x < first.Width; x++)
            {
                if (first.GetPixel(x, y) != second.GetPixel(x, y))
                {
                    differences.Add((x, y));
                }
            }
        }

        return differences;
    }

    private static bool Contains(SKRect area, (int X, int Y) pixel, float slack = 1f) =>
        pixel.X >= area.Left - slack && pixel.X <= area.Right + slack && pixel.Y >= area.Top - slack && pixel.Y <= area.Bottom + slack;

    private static float Luminance(SKColor color) => (0.2126f * color.Red) + (0.7152f * color.Green) + (0.0722f * color.Blue);

    // The light theme with annotations in the plot background colour (which is also the canvas colour): the layout is
    // the same, and lines and labels leave no visible trace - so comparing against it isolates what they draw.
    private static readonly GraphTheme Invisible = GraphThemes.Light with { Annotation = GraphThemes.Light.PlotBackground };

    private static SKFont TickFont() => new() { Size = GraphThemes.Light.TickLabelFontSize, Edging = SKFontEdging.Antialias, Subpixel = true };

    // ---- Layout ----

    [Fact]
    public void WithoutLinesTheLayoutAndTheDrawingAreWhatTheyAlwaysWere()
    {
        foreach (var model in new[] { Model(), Model(new GraphLegendModel([new GraphLegendEntry("A", 0)], "Lot")) })
        {
            var layout = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light);
            var empty = SkiaGraphRenderer.Layout(model.WithReferenceLines([]), Canvas, GraphThemes.Light);

            Assert.Equal(layout, empty);
            Assert.True(layout.ReferenceLabelArea.IsEmpty);

            using var before = Render(model);
            using var after = Render(model.WithReferenceLines([]));
            Assert.True(Identical(before, after));
        }
    }

    [Fact]
    public void ALabelBandIsReservedAboveThePlotAndOnlyThere()
    {
        var plain = SkiaGraphRenderer.Layout(Model(), Canvas, GraphThemes.Light);
        var lined = SkiaGraphRenderer.Layout(WithLines(Model(), Line(50)), Canvas, GraphThemes.Light);
        var band = lined.ReferenceLabelArea;

        Assert.False(band.IsEmpty);
        Assert.Equal(lined.PlotArea.Top, band.Bottom);
        Assert.Equal(lined.PlotArea.Left, band.Left);
        Assert.Equal(lined.PlotArea.Right, band.Right);
        Assert.True(band.Top >= lined.TitleArea.Bottom);

        // The plot keeps its width and gives the band its height from the top.
        Assert.Equal(plain.PlotArea.Left, lined.PlotArea.Left);
        Assert.Equal(plain.PlotArea.Right, lined.PlotArea.Right);
        Assert.Equal(plain.PlotArea.Bottom, lined.PlotArea.Bottom);
        Assert.True(lined.PlotArea.Top > plain.PlotArea.Top);
    }

    [Fact]
    public void TheBandGrowsARowOnlyWhenLabelsWouldCollide()
    {
        using var font = TickFont();
        var apart = SkiaGraphRenderer.Layout(WithLines(Model(), Line(10), Line(50), Line(90)), Canvas, GraphThemes.Light);
        var close = SkiaGraphRenderer.Layout(WithLines(Model(), Line(50), Line(50.5), Line(51)), Canvas, GraphThemes.Light);

        Assert.Equal(SkiaGraphRenderer.ReferenceLabelBandHeight(1, font), apart.ReferenceLabelArea.Height, 0.01f);
        Assert.Equal(SkiaGraphRenderer.ReferenceLabelBandHeight(3, font), close.ReferenceLabelArea.Height, 0.01f);
    }

    [Fact]
    public void TheBandSharesTheCanvasWithTheLegendAndThePanelWithoutOverlap()
    {
        var panel = GraphStatisticsPanelBuilder.Build(
            new UnivariateGraphData(GraphType.Histogram, Guid.NewGuid(), new GraphColumnInfo(Guid.NewGuid(), "Reg1", WorksheetDataType.Numeric),
                new double[] { 1, 2, 3, 4 },
                new StringGroupData(new GraphColumnInfo(Guid.NewGuid(), "Lot", WorksheetDataType.String), new string?[] { "A", "B", "A", "B" })), Token)!;
        var model = WithLines(Model(new GraphLegendModel([new GraphLegendEntry("A", 0), new GraphLegendEntry("B", 1)], "Lot")), Line(0), Line(50), Line(100))
            .WithStatisticsPanel(panel);

        var layout = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light);
        var areas = new[]
        {
            layout.TitleArea, layout.ReferenceLabelArea, layout.PlotArea, layout.XAxisArea, layout.YAxisArea,
            layout.LegendArea, layout.StatisticsPanelArea
        };

        Assert.All(areas, area => Assert.True(Canvas.Contains(area), $"{area} is not inside the canvas."));
        for (var first = 0; first < areas.Length; first++)
        {
            for (var second = first + 1; second < areas.Length; second++)
            {
                Assert.False(areas[first].IntersectsWith(areas[second]), $"{areas[first]} overlaps {areas[second]}.");
            }
        }
    }

    // ---- Label placement ----

    [Fact]
    public void LabelsSitOverTheirLinesInOneRowWhenTheyHaveRoom()
    {
        using var font = TickFont();
        var model = WithLines(Model(), Line(20), Line(50, "Target", GraphReferenceLineKind.Target), Line(80, "USL"));
        var plot = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light).PlotArea;
        var transform = new GraphCoordinateTransform(model.XAxis.Range, model.YAxis.Range, plot);

        var labels = SkiaGraphRenderer.PlaceReferenceLabels(model, plot, font);

        Assert.Equal(3, labels.Count);
        Assert.All(labels, label =>
        {
            Assert.Equal(0, label.Row);
            Assert.Equal((float)transform.ToScreenX(label.Line.Value), label.Left + (label.Width / 2f), 0.01f);
        });
    }

    [Fact]
    public void CloseLabelsMoveUpARowInsteadOfOverlapping()
    {
        using var font = TickFont();
        var model = WithLines(Model(), Line(50), Line(50.2, "Target", GraphReferenceLineKind.Target), Line(50.4, "USL"));
        var plot = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light).PlotArea;

        var labels = SkiaGraphRenderer.PlaceReferenceLabels(model, plot, font);

        Assert.Equal([0, 1, 2], labels.Select(label => label.Row));
        foreach (var row in labels.GroupBy(label => label.Row))
        {
            var ordered = row.OrderBy(label => label.Left).ToArray();
            for (var index = 1; index < ordered.Length; index++)
            {
                Assert.True(ordered[index].Left >= ordered[index - 1].Left + ordered[index - 1].Width);
            }
        }
    }

    [Fact]
    public void LabelsAtEitherEndOfThePlotArePushedInsideIt()
    {
        using var font = TickFont();
        var model = WithLines(Model(), Line(0, "LSL"), Line(100, "USL"));
        var plot = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light).PlotArea;

        var labels = SkiaGraphRenderer.PlaceReferenceLabels(model, plot, font);

        Assert.Equal(2, labels.Count);
        Assert.Equal(plot.Left, labels[0].Left, 0.01f);
        Assert.Equal(plot.Right, labels[1].Left + labels[1].Width, 0.01f);
    }

    [Fact]
    public void LinesOutsideTheAxisGetNoLabel()
    {
        using var font = TickFont();
        var model = WithLines(Model(), Line(-50), Line(50), Line(1e6));
        var plot = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light).PlotArea;

        Assert.Equal(50, Assert.Single(SkiaGraphRenderer.PlaceReferenceLabels(model, plot, font)).Line.Value);
    }

    // ---- Drawing ----

    [Theory]
    [InlineData(25.0)]
    [InlineData(50.0)]
    [InlineData(73.3)]
    public void ALineIsDrawnAtItsTransformedXAndNowhereElseInThePlot(double value)
    {
        var model = WithLines(Model(), Line(value));
        var layout = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light);
        var x = new GraphCoordinateTransform(model.XAxis.Range, model.YAxis.Range, layout.PlotArea).ToScreenX(value);

        // The same layout with the line drawn invisibly: every difference inside the plot is the line itself.
        using var withLine = Render(model);
        using var withoutLine = Render(model, theme: Invisible);

        var inPlot = Differences(withLine, withoutLine).Where(pixel => Contains(layout.PlotArea, pixel, slack: 0)).ToList();
        Assert.NotEmpty(inPlot);
        Assert.All(inPlot, pixel => Assert.InRange(pixel.X, x - 2, x + 2));
    }

    [Fact]
    public void LinesAndLabelsStayInThePlotAndTheBand()
    {
        var model = WithLines(Model(), Line(0), Line(50, "Target", GraphReferenceLineKind.Target), Line(100, "USL"));
        var layout = SkiaGraphRenderer.Layout(model, Canvas, GraphThemes.Light);

        // Against the same model drawn invisibly: what differs is the lines and their labels.
        using var drawn = Render(model);
        using var reference = Render(model, theme: Invisible);
        var differences = Differences(drawn, reference);

        Assert.Contains(differences, pixel => Contains(layout.ReferenceLabelArea, pixel, slack: 0));
        Assert.Contains(differences, pixel => Contains(layout.PlotArea, pixel, slack: 0));
        Assert.All(differences, pixel => Assert.True(
            Contains(layout.PlotArea, pixel) || Contains(layout.ReferenceLabelArea, pixel),
            $"({pixel.X}, {pixel.Y}) is outside the plot and the label band."));
    }

    [Fact]
    public void LinesUseTheNeutralAnnotationColourInBothThemes()
    {
        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
        {
            var model = WithLines(Model(), Line(50.25));
            var layout = SkiaGraphRenderer.Layout(model, Canvas, theme);
            var x = (int)Math.Round(new GraphCoordinateTransform(model.XAxis.Range, model.YAxis.Range, layout.PlotArea).ToScreenX(50.25));

            using var bitmap = Render(model, theme: theme);
            var column = Enumerable.Range((int)layout.PlotArea.Top + 2, (int)layout.PlotArea.Height - 4)
                .SelectMany(y => new[] { bitmap.GetPixel(x - 1, y), bitmap.GetPixel(x, y), bitmap.GetPixel(x + 1, y) })
                .ToList();

            // The line stands out from the plot background: darker in the light theme, lighter in the dark one.
            var background = Luminance(theme.PlotBackground);
            var strongest = theme == GraphThemes.Light ? column.Min(Luminance) : column.Max(Luminance);
            Assert.True(Math.Abs(strongest - background) > 60, $"The line is barely visible ({strongest} on {background}).");

            // And no series colour is used for it.
            Assert.DoesNotContain(column, pixel => theme.SeriesPalette.Contains(pixel));
        }
    }

    [Fact]
    public void AnnotationColoursAreNeutralAndReadable()
    {
        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
        {
            var colour = theme.Annotation;
            Assert.Equal(colour.Red, colour.Green);
            Assert.Equal(colour.Green, colour.Blue);
            Assert.DoesNotContain(colour, theme.SeriesPalette);
            Assert.True(Math.Abs(Luminance(colour) - Luminance(theme.PlotBackground)) > 100);
        }

        Assert.True(GraphThemes.Light.TargetThickness > GraphThemes.Light.SpecificationLimitThickness);
    }

    [Fact]
    public void ATargetIsDrawnDifferentlyFromALimit()
    {
        var limit = WithLines(Model(), Line(50));
        var target = WithLines(Model(), Line(50, "LSL", GraphReferenceLineKind.Target));

        using var limitBitmap = Render(limit);
        using var targetBitmap = Render(target);

        var plot = SkiaGraphRenderer.Layout(limit, Canvas, GraphThemes.Light).PlotArea;
        Assert.Contains(Differences(limitBitmap, targetBitmap), pixel => Contains(plot, pixel, slack: 0));
    }

    [Theory]
    [InlineData(160, 120)]
    [InlineData(240, 180)]
    [InlineData(90, 400)]
    public void ANarrowGraphWithCloseLinesDrawsWithoutThrowingInsideTheCanvas(int width, int height)
    {
        var model = WithLines(Model(), Line(49.9), Line(50, "Target", GraphReferenceLineKind.Target), Line(50.1, "USL"));
        var canvas = new SKRect(0, 0, width, height);

        var layout = SkiaGraphRenderer.Layout(model, canvas, GraphThemes.Light);
        using var bitmap = Render(model, width, height);

        if (layout.HasPlotArea)
        {
            Assert.True(canvas.Contains(layout.ReferenceLabelArea));
            Assert.True(layout.ReferenceLabelArea.Bottom <= layout.PlotArea.Top + 0.01f);
        }
    }

    // ---- The real graphs, and export ----

    private static (GraphRenderModel Frame, IGraphPlotRenderer Plot) Histogram(Specification specification, bool showStatistics = true)
    {
        var values = Enumerable.Range(0, 300).Select(index => 15 + ((index % 41) - 20) * 0.01).ToArray();
        var groups = Enumerable.Range(0, 300).Select(index => $"Lot {index % 3}").ToArray();
        var data = new UnivariateGraphData(GraphType.Histogram, Guid.NewGuid(), new GraphColumnInfo(Guid.NewGuid(), "Reg1", WorksheetDataType.Numeric),
            values, new StringGroupData(new GraphColumnInfo(Guid.NewGuid(), "Lot", WorksheetDataType.String), groups));
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.NewGuid(), [])
        {
            StatisticsOptions = new GraphStatisticsOptions(showStatistics ? GraphStatisticsMode.Auto : GraphStatisticsMode.Hide),
            Specification = specification
        };

        return (GraphPresentation.Apply(model.Frame, data, configuration, Token), new HistogramRenderer(model));
    }

    [Fact]
    public void AGroupedHistogramDrawsEachLineOnceOverItsBars()
    {
        var (frame, plot) = Histogram(new Specification(14.9, 15, 15.1));
        var layout = SkiaGraphRenderer.Layout(frame, Canvas, GraphThemes.Light);
        var transform = new GraphCoordinateTransform(frame.XAxis.Range, frame.YAxis.Range, layout.PlotArea);

        Assert.Equal(3, frame.ReferenceLines.Count);

        using var bitmap = new SKBitmap(800, 600);
        using (var canvas = new SKCanvas(bitmap))
        {
            new SkiaGraphRenderer().Render(canvas, frame, Canvas, GraphThemes.Light, plot);
        }

        // The target runs through the middle of the distribution, where the bars are: it is drawn over them.
        var x = (int)Math.Round(transform.ToScreenX(15));
        var bottom = (int)layout.PlotArea.Bottom - 3;
        var nearAxis = Enumerable.Range(bottom - 20, 20).SelectMany(y => new[] { bitmap.GetPixel(x - 1, y), bitmap.GetPixel(x, y), bitmap.GetPixel(x + 1, y) });
        Assert.Contains(nearAxis, pixel => Math.Abs(Luminance(pixel) - Luminance(GraphThemes.Light.Annotation)) < 40);
    }

    [Fact]
    public void ThePngCarriesTheLinesAtTheUsualExportSize()
    {
        var (with, plot) = Histogram(new Specification(14.5, 15, 15.5));
        var (without, _) = Histogram(Specification.None);
        var service = new GraphExportService();

        using var withLines = SKBitmap.Decode(service.RenderPng(new GraphExportSnapshot(with, plot, GraphThemes.Light)));
        using var withoutLines = SKBitmap.Decode(service.RenderPng(new GraphExportSnapshot(without, plot, GraphThemes.Light)));

        Assert.Equal(GraphExportService.ExportWidth, withLines.Width);
        Assert.Equal(GraphExportService.ExportHeight, withLines.Height);
        Assert.False(Identical(withLines, withoutLines));

        // Without a specification the PNG is exactly what it was before lines existed.
        Assert.Equal(
            service.RenderPng(new GraphExportSnapshot(without, plot, GraphThemes.Light)),
            service.RenderPng(new GraphExportSnapshot(without.WithReferenceLines([]), plot, GraphThemes.Light)));
    }

    [Fact]
    public void ThePowerPointSlideCarriesTheSamePngAndIsAValidPackage()
    {
        var (frame, plot) = Histogram(new Specification(14.5, 15, 15.5), showStatistics: false);
        var png = new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, GraphThemes.Dark));

        using var directory = new TemporaryDirectory();
        var path = directory.File("graph.pptx");
        new PowerPointGraphExporter().Save(
            path, new PowerPointSlideImage(png, GraphExportService.ExportWidth, GraphExportService.ExportHeight, "202020"));

        using var document = PresentationDocument.Open(path, false);
        Assert.Empty(new OpenXmlValidator().Validate(document, Token));

        var slide = Assert.Single(document.PresentationPart!.SlideParts);
        var image = Assert.Single(slide.ImageParts);
        using var stream = image.GetStream();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        Assert.Equal(png, bytes.ToArray());
    }
}
