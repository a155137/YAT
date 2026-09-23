using System.Text;
using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Graphs of Chinese data (Task #040.1): Chinese column names, group values, custom labels and statistics rows, through
// the production builders and presentation, drawn by the renderer with every glyph present, laid out by the width the
// text is drawn at, and exported - PNG, PowerPoint, clipboard - as one image. Needs the fonts Windows has for Chinese,
// Japanese and Korean; skipped where they are not installed.
public class GraphCjkRenderingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 300;

    private static readonly double[] Thickness = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)) + ((i % 7) * 0.003))];
    private static readonly double[] Resistance = [.. Enumerable.Range(0, Count).Select(i => 30 + (0.2 * Math.Cos(i * 0.23)))];

    private static readonly string[] Groups = ["產品A / Lot 12", "Site A 平均值", "批次 站點", "ロット", "로트 7"];

    private static void RequireCjkFonts() =>
        Assert.SkipUnless(FontFallbackCache.Shared.Find(SKTypeface.Default, '厚') is not null, "No installed font has Chinese glyphs on this machine.");

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData GroupData() =>
        new(Column("批次", WorksheetDataType.String), Enumerable.Range(0, Count).Select(i => (string?)Groups[i % Groups.Length]).ToArray());

    private static readonly GraphLabelOptions ChineseLabels = new(
        GraphLabelOption.Custom("晶圓厚度 直方圖"),
        GraphLabelOption.Custom("PS 感度 (µA)"),
        GraphLabelOption.Custom("片數"));

    private sealed record Built(GraphRenderModel Frame, IGraphPlotRenderer Plot);

    private static Built Build(GraphType type, GraphLabelOptions labels)
    {
        var configuration = new GraphConfiguration(type, Guid.Empty, [])
        {
            Specification = type is GraphType.Histogram or GraphType.ProbabilityPlot or GraphType.EmpiricalCdf
                ? new YAT.Application.Specifications.Specification(14.8, 15, 15.2)
                : YAT.Application.Specifications.Specification.None,
            LabelOptions = labels
        };

        GraphData data;
        GraphRenderModel frame;
        IGraphPlotRenderer plot;
        switch (type)
        {
            case GraphType.ScatterPlot:
            {
                var scatter = new ScatterGraphData(Guid.Empty, Column("Wafer 厚度"), Column("電阻 (Ω)"), Thickness, Resistance, GroupData());
                var model = new ScatterRenderModelBuilder().Build(scatter, new ScatterPlotLabels("Wafer 厚度", "電阻 (Ω)", "批次"), Token)!;
                (data, frame, plot) = (scatter, model.Frame, new ScatterRenderer(model));
                break;
            }

            case GraphType.BoxPlot:
            {
                var parts = new[]
                {
                    new UnivariateGraphData(type, Guid.Empty, Column("Wafer 厚度"), Thickness, GroupData()),
                    new UnivariateGraphData(type, Guid.Empty, Column("電阻 (Ω)"), Resistance, GroupData())
                };
                var multi = new MultiVariableGraphData(type, Guid.Empty, parts);
                var model = new BoxPlotRenderModelBuilder().Build(multi, new BoxPlotLabels(["Wafer 厚度", "電阻 (Ω)"], "批次"), Token)!;
                (data, frame, plot) = (multi, model.Frame, new BoxPlotRenderer(model));
                break;
            }

            default:
            {
                var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Wafer 厚度"), Thickness, GroupData());
                (data, frame, plot) = type switch
                {
                    GraphType.Histogram => Histogram(univariate),
                    GraphType.ProbabilityPlot => Probability(univariate),
                    _ => Ecdf(univariate)
                };
                break;
            }
        }

        return new Built(GraphPresentation.Apply(frame, data, configuration, Token), plot);
    }

    private static (GraphData, GraphRenderModel, IGraphPlotRenderer) Histogram(UnivariateGraphData data)
    {
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Wafer 厚度", "批次"), Token)!;
        return (data, model.Frame, new HistogramRenderer(model));
    }

    private static (GraphData, GraphRenderModel, IGraphPlotRenderer) Probability(UnivariateGraphData data)
    {
        var model = new ProbabilityPlotRenderModelBuilder().Build(data, new ProbabilityPlotLabels("Wafer 厚度", "批次"), Token)!;
        return (data, model.Frame, new ProbabilityPlotRenderer(model));
    }

    private static (GraphData, GraphRenderModel, IGraphPlotRenderer) Ecdf(UnivariateGraphData data)
    {
        var model = new EmpiricalCdfRenderModelBuilder().Build(data, new EmpiricalCdfLabels("Wafer 厚度", "批次"), Token)!;
        return (data, model.Frame, new EmpiricalCdfRenderer(model));
    }

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    // Every piece of text the renderer draws for a frame.
    private static IEnumerable<string> Texts(GraphRenderModel frame)
    {
        IEnumerable<string?> texts =
        [
            frame.Title, frame.XAxis.Title, frame.YAxis.Title,
            .. frame.XAxis.Ticks.Select(tick => tick.Label), .. frame.YAxis.Ticks.Select(tick => tick.Label),
            frame.Legend?.Title, .. frame.Legend?.Entries.Select(entry => entry.Label) ?? [],
            frame.StatisticsPanel?.Title, frame.StatisticsPanel?.GroupHeader,
            .. frame.StatisticsPanel?.Rows.Select(row => row.Label) ?? [],
            .. frame.ReferenceLines.Select(line => line.Label)
        ];
        return texts.OfType<string>().Where(text => text.Length > 0);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void EveryTextOfAChineseGraphHasEveryGlyph(GraphType type)
    {
        RequireCjkFonts();
        var built = Build(type, ChineseLabels);
        var texts = Texts(built.Frame).ToList();

        Assert.Contains(texts, text => text.Contains('厚'));
        Assert.Contains("晶圓厚度 直方圖", texts);
        foreach (var size in new[] { 11f, 12f, 16f })
        {
            using var font = new SKFont { Size = size, Edging = SKFontEdging.Antialias, Subpixel = true };
            foreach (var text in texts)
            {
                foreach (var run in GraphTextFallback.Runs(font, text, FontFallbackCache.Shared))
                {
                    using var runFont = run.Typeface is null ? null : new SKFont(run.Typeface, size);
                    var segment = text.Substring(run.Start, run.Length);
                    Assert.True(
                        segment.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || (runFont ?? font).GetGlyph(rune.Value) != 0),
                        $"'{segment}' of '{text}' has a missing glyph in {(run.Typeface?.FamilyName ?? "the graph font")}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AChineseGraphDrawsInBothThemesAtEverySize(GraphType type)
    {
        RequireCjkFonts();
        var built = Build(type, ChineseLabels);

        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
        {
            foreach (var (width, height) in new[] { (1600, 1000), (760, 488), (360, 260), (120, 90) })
            {
                using var bitmap = new SKBitmap(width, height);
                using var canvas = new SKCanvas(bitmap);
                new SkiaGraphRenderer().Render(canvas, built.Frame, new SKRect(0, 0, width, height), theme, built.Plot);
            }
        }
    }

    // The legend is as wide as its Chinese entries are drawn (up to its maximum), not as wide as empty boxes would be.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void TheLegendMakesRoomForItsChineseEntries(GraphType type)
    {
        RequireCjkFonts();
        var built = Build(type, ChineseLabels);
        var layout = SkiaGraphRenderer.Layout(built.Frame, new SKRect(0, 0, 1600, 1000), GraphThemes.Light);

        using var font = new SKFont { Size = GraphThemes.Light.TickLabelFontSize, Edging = SKFontEdging.Antialias, Subpixel = true };
        var widest = built.Frame.Legend!.Entries.Max(entry => GraphTextFallback.MeasureText(font, entry.Label));
        var widestAsBoxes = built.Frame.Legend.Entries.Max(entry => font.MeasureText(entry.Label));

        Assert.True(widest > widestAsBoxes);
        Assert.True(layout.LegendArea.Width >= widest + 11f + 5f + 16f - 0.01f,
            $"legend {layout.LegendArea.Width} is narrower than its widest entry {widest} with swatch and padding");
    }

    // Box plot categories are X tick labels: the room kept for the last one to overhang the plot follows the width
    // the Chinese category is drawn at.
    [Fact]
    public void ChineseCategoriesAreMeasuredAsTheyAreDrawn()
    {
        RequireCjkFonts();
        var built = Build(GraphType.BoxPlot, GraphLabelOptions.Default);
        var labels = built.Frame.XAxis.Ticks.Select(tick => tick.Label).ToList();
        Assert.Contains(labels, label => label.Any(character => character >= 0x2E80));

        var layout = SkiaGraphRenderer.Layout(built.Frame, new SKRect(0, 0, 1600, 1000), GraphThemes.Light);
        using var font = new SKFont { Size = GraphThemes.Light.TickLabelFontSize, Edging = SKFontEdging.Antialias, Subpixel = true };
        var overhang = labels.Max(label => GraphTextFallback.MeasureText(font, label)) / 2f;

        var legendColumn = layout.Canvas.Right - layout.PlotArea.Right;
        Assert.True(legendColumn >= overhang, $"right band {legendColumn} leaves no room for half of the widest category ({overhang})");
    }

    // Statistics rows are cut to their column by the width they are drawn at, so a cut Chinese label never overruns
    // the numbers beside it.
    [Fact]
    public void ChineseStatisticsRowsAreCutToTheirColumn()
    {
        RequireCjkFonts();
        using var font = new SKFont { Size = GraphThemes.Light.TickLabelFontSize, Edging = SKFontEdging.Antialias, Subpixel = true };

        foreach (var label in Groups.Append("批次 產品 A 標準差 平均值 站點"))
        {
            for (var width = 8f; width <= 120f; width += 4f)
            {
                var cut = SkiaGraphRenderer.Ellipsize(label, font, width);
                Assert.True(cut.Length == 0 || GraphTextFallback.MeasureText(font, cut) <= width, $"'{cut}' overruns {width}");
            }
        }
    }

    // Window, PNG, PowerPoint and clipboard: one image of the Chinese graph, whichever way it leaves the window.
    [Fact]
    public async Task EveryExportOfAChineseGraphIsTheSameImage()
    {
        RequireCjkFonts();
        var built = Build(GraphType.Histogram, ChineseLabels);
        var snapshot = new GraphExportSnapshot(built.Frame, built.Plot, GraphThemes.Dark);
        var expected = new GraphExportService().RenderPng(snapshot);

        using var directory = new TemporaryDirectory();
        var dialogs = new FakeGraphExportDialogs { PngPath = directory.File("graph.png"), PowerPointPath = directory.File("graph.pptx") };
        var powerPoint = new FakePowerPointGraphExporter();
        var clipboard = new FakeGraphImageClipboard();
        var controller = new GraphExportController(dialogs, new GraphExportService(), powerPoint, clipboard);

        await controller.ExportPngAsync(snapshot, Token);
        await controller.ExportPowerPointAsync(snapshot, Token);
        await controller.CopyImageAsync(snapshot, Token);

        Assert.Equal(expected, File.ReadAllBytes(dialogs.PngPath!));
        Assert.Equal(expected, powerPoint.Last.Image.Png);
        Assert.Equal(expected, Assert.Single(clipboard.Copied));
        Assert.Equal(["晶圓厚度 直方圖.png", "晶圓厚度 直方圖.pptx"], dialogs.SuggestedNames);
    }
}
