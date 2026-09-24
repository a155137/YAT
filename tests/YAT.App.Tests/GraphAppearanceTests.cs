using SkiaSharp;
using YAT.Application.Graphs;
using YAT.App.Tests.TestDoubles;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// A graph's appearance (Task #046): put on the theme it is drawn in, never on its frame. With nothing chosen the theme
// itself comes back; a custom palette colours every series of every graph type and their swatches; the grid can be
// hidden or recoloured; the plot and the graph backgrounds - and the normal fit's halo with the plot - take the colours
// chosen, in the light and the dark theme alike, on screen, in a PNG and on a slide. An ungrouped histogram's bars are
// outlined in the theme's neutral bin outline.
public class GraphAppearanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 1200;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)) + (i % 7 * 0.003))];
    private static readonly double[] Reg2 = [.. Enumerable.Range(0, Count).Select(i => 14.9 + (0.15 * Math.Cos(i * 0.23)))];

    private static readonly GraphColor Red = new(0xD0, 0x00, 0x00);
    private static readonly GraphColor Green = new(0x00, 0xB0, 0x00);
    private static readonly GraphColor Blue = new(0x00, 0x00, 0xD0);
    private static readonly GraphColor Magenta = new(0xFF, 0x00, 0xFF);
    private static readonly GraphColor Navy = new(0x12, 0x34, 0x56);
    private static readonly GraphColor Sand = new(0xF0, 0xE0, 0xD0);

    private static readonly GraphPalette Three = new([Red, Green, Blue]);

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData? Groups(int groups) => groups == 0
        ? null
        : new(Column("Lot", WorksheetDataType.String), (string?[])[.. Enumerable.Range(0, Count).Select(i => (string?)$"Lot {i % groups}")]);

    private sealed record Built(GraphPresentationState State, IGraphPlotRenderer Plot, object Model);

    // A graph as the graph preparation presents it, with the appearance given.
    private static Built Build(GraphType type, int groups, GraphAppearanceOptions? appearance = null, HistogramOptions? histogram = null)
    {
        var group = Groups(groups);
        GraphData data;
        (GraphRenderModel Frame, IGraphPlotRenderer Plot, object Model) built;
        switch (type)
        {
            case GraphType.ScatterPlot:
            {
                var scatter = new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), Reg1, Reg2, group);
                var model = new ScatterRenderModelBuilder().Build(scatter, new ScatterPlotLabels("Reg1", "Reg2", group?.Column.Name), Token)!;
                (data, built) = (scatter, (model.Frame, new ScatterRenderer(model), model));
                break;
            }

            case GraphType.BoxPlot:
            {
                var multi = new MultiVariableGraphData(type, Guid.Empty,
                [
                    new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, group),
                    new UnivariateGraphData(type, Guid.Empty, Column("Reg2"), Reg2, group)
                ]);
                var model = new BoxPlotRenderModelBuilder().Build(multi, new BoxPlotLabels(["Reg1", "Reg2"], group?.Column.Name), Token)!;
                (data, built) = (multi, (model.Frame, new BoxPlotRenderer(model), model));
                break;
            }

            case GraphType.Histogram:
            {
                var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, group);
                var model = new HistogramRenderModelBuilder().Build(
                    univariate, new HistogramPlotLabels("Reg1", group?.Column.Name), histogram ?? HistogramOptions.Default, Token)!;
                (data, built) = (univariate, (model.Frame, new HistogramRenderer(model), model));
                break;
            }

            case GraphType.ProbabilityPlot:
            {
                var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, group);
                var model = new ProbabilityPlotRenderModelBuilder().Build(univariate, new ProbabilityPlotLabels("Reg1", group?.Column.Name), Token)!;
                (data, built) = (univariate, (model.Frame, new ProbabilityPlotRenderer(model), model));
                break;
            }

            default:
            {
                var univariate = new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, group);
                var model = new EmpiricalCdfRenderModelBuilder().Build(univariate, new EmpiricalCdfLabels("Reg1", group?.Column.Name), Token)!;
                (data, built) = (univariate, (model.Frame, new EmpiricalCdfRenderer(model), model));
                break;
            }
        }

        var configuration = new GraphConfiguration(type, Guid.Empty, []) { AppearanceOptions = appearance ?? GraphAppearanceOptions.Default };
        return new Built(GraphPresentation.Present(built.Frame, data, configuration, Token), built.Plot, built.Model);
    }

    // The graph as it is exported, drawn in the theme its appearance resolves.
    private static SKBitmap Draw(Built built, GraphTheme? theme = null) =>
        SKBitmap.Decode(Png(built, theme));

    private static byte[] Png(Built built, GraphTheme? theme = null) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(
            built.State.Frame, built.Plot, GraphAppearance.Resolve(theme ?? GraphThemes.Light, built.State.AppearanceOptions)));

    private static GraphLayout Layout(Built built, GraphTheme? theme = null) =>
        SkiaGraphRenderer.Layout(
            built.State.Frame,
            new SKRect(0, 0, GraphExportService.ExportWidth / GraphExportService.ExportScale, GraphExportService.ExportHeight / GraphExportService.ExportScale),
            theme ?? GraphThemes.Light);

    // How many pixels of the image are exactly this colour, within a region of the graph (layout units), shrunk by a
    // margin so what stands on its edge is left out.
    private static int CountOf(SKBitmap image, SKColor color, SKRect? region = null, float inset = 0)
    {
        var scale = GraphExportService.ExportScale;
        var area = region ?? new SKRect(0, 0, image.Width / scale, image.Height / scale);
        var left = Math.Max(0, (int)Math.Ceiling((area.Left + inset) * scale));
        var top = Math.Max(0, (int)Math.Ceiling((area.Top + inset) * scale));
        var right = Math.Min(image.Width, (int)Math.Floor((area.Right - inset) * scale));
        var bottom = Math.Min(image.Height, (int)Math.Floor((area.Bottom - inset) * scale));
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (image.GetPixel(x, y) == color)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static SKColor Skia(GraphColor color) => GraphAppearance.ToSkia(color);

    // ---- Resolving the theme ----

    [Fact]
    public void NothingChosenIsTheVeryTheme()
    {
        Assert.Same(GraphThemes.Light, GraphAppearance.Resolve(GraphThemes.Light, GraphAppearanceOptions.Default));
        Assert.Same(GraphThemes.Dark, GraphAppearance.Resolve(GraphThemes.Dark, GraphAppearanceOptions.Default));
        Assert.Same(GraphThemes.Light, GraphAppearance.Resolve(GraphThemes.Light, new GraphAppearanceOptions()));
        Assert.Same(GraphThemes.Dark, GraphAppearance.Resolve(GraphThemes.Dark, new GraphAppearanceOptions(null, GraphGridMode.Auto)));
    }

    // Each choice replaces its own part of the theme, and nothing else.
    [Theory]
    [InlineData("palette")]
    [InlineData("hide")]
    [InlineData("show")]
    [InlineData("grid")]
    [InlineData("plot")]
    [InlineData("graph")]
    public void EachChoiceReplacesItsOwnPartOnly(string what)
    {
        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
        {
            var appearance = what switch
            {
                "palette" => new GraphAppearanceOptions(Three),
                "hide" => new GraphAppearanceOptions(GridMode: GraphGridMode.Hide),
                "show" => new GraphAppearanceOptions(GridMode: GraphGridMode.Show),
                "grid" => new GraphAppearanceOptions(GridColor: Magenta),
                "plot" => new GraphAppearanceOptions(PlotBackground: Navy),
                _ => new GraphAppearanceOptions(GraphBackground: Sand)
            };

            var resolved = GraphAppearance.Resolve(theme, appearance);
            var expected = what switch
            {
                "palette" => theme with { SeriesPalette = resolved.SeriesPalette },
                "hide" => theme with { ShowGrid = false },
                "show" => theme,
                "grid" => theme with { Grid = Skia(Magenta) },
                "plot" => theme with { PlotBackground = Skia(Navy) },
                _ => theme with { Background = Skia(Sand) }
            };

            Assert.Equal(expected, resolved);
            Assert.Equal(theme.Text, resolved.Text);
            Assert.Equal(theme.Axis, resolved.Axis);
            Assert.Equal(theme.Annotation, resolved.Annotation);
            Assert.Equal(theme.BinOutline, resolved.BinOutline);
        }
    }

    // A colour chosen is that colour in either theme; what is not chosen follows the theme.
    [Fact]
    public void ChosenColorsStayWhatTheyAreInEitherTheme()
    {
        var appearance = new GraphAppearanceOptions(Three, GraphGridMode.Auto, null, Navy, null);
        var light = GraphAppearance.Resolve(GraphThemes.Light, appearance);
        var dark = GraphAppearance.Resolve(GraphThemes.Dark, appearance);

        Assert.Equal(Skia(Navy), light.PlotBackground);
        Assert.Equal(Skia(Navy), dark.PlotBackground);
        Assert.Equal(light.SeriesPalette, dark.SeriesPalette);
        Assert.Equal(GraphThemes.Light.Grid, light.Grid);
        Assert.Equal(GraphThemes.Dark.Grid, dark.Grid);
        Assert.Equal(GraphThemes.Light.Background, light.Background);
        Assert.Equal(GraphThemes.Dark.Background, dark.Background);
        Assert.Equal(GraphThemes.Dark.Text, dark.Text);
    }

    // Series i takes colour i modulo the palette's length - and the themes' own palette is never touched.
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(16)]
    public void APaletteColorsSeriesInTurn(int colors)
    {
        var defaultPalette = GraphThemes.Light.SeriesPalette;
        var defaults = defaultPalette.ToArray();
        var palette = new GraphPalette([.. Enumerable.Range(0, colors).Select(index => new GraphColor((byte)(index * 13), (byte)(200 - index), 7))]);

        var resolved = GraphAppearance.Resolve(GraphThemes.Light, new GraphAppearanceOptions(palette));

        for (var series = 0; series < 100; series++)
        {
            Assert.Equal(Skia(palette.Colors[series % colors]), resolved.SeriesColor(series));
        }

        Assert.Same(defaultPalette, GraphThemes.Light.SeriesPalette);
        Assert.Same(GraphThemes.Light.SeriesPalette, GraphThemes.Dark.SeriesPalette);
        Assert.Equal(defaults, GraphThemes.Light.SeriesPalette);
        Assert.Equal(8, GraphThemes.Light.SeriesPalette.Count);
        for (var series = 0; series < 100; series++)
        {
            Assert.Equal(defaults[series % 8], GraphThemes.Light.SeriesColor(series));
        }
    }

    [Fact]
    public void AnAppearanceThatIsNotValidIsRefused()
    {
        Assert.Throws<ArgumentException>(() => GraphAppearance.Resolve(GraphThemes.Light, new GraphAppearanceOptions(new GraphPalette([]))));
        Assert.Throws<ArgumentException>(() => GraphAppearance.Resolve(GraphThemes.Light, new GraphAppearanceOptions(GridMode: (GraphGridMode)9)));
        var graph = Build(GraphType.Histogram, 0).State;
        Assert.Throws<ArgumentException>(() => graph.WithAppearance(new GraphAppearanceOptions(GridMode: (GraphGridMode)9)));
    }

    // ---- The graph's frame is never touched ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AnAppearanceKeepsTheVeryFrames(GraphType type)
    {
        var graph = Build(type, 4).State;
        var styled = graph.WithAppearance(new GraphAppearanceOptions(Three, GraphGridMode.Hide, Magenta, Navy, Sand));

        Assert.Same(graph.Frame, styled.Frame);
        Assert.Same(graph.BaseFrame, styled.BaseFrame);
        Assert.Same(graph.UnlabelledFrame, styled.UnlabelledFrame);
        Assert.Same(graph.LabelOptions, styled.LabelOptions);
        Assert.Same(graph.LegendOptions, styled.LegendOptions);
        Assert.Same(graph.StatisticsOptions, styled.StatisticsOptions);
        Assert.Same(graph.AxisRangeOptions, styled.AxisRangeOptions);
        Assert.Same(graph.Frame, styled.WithAppearance(GraphAppearanceOptions.Default).Frame);

        // The other editors keep the appearance, and it keeps what they chose.
        var relabelled = styled.WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Auto, GraphLabelOption.Auto))
            .WithLegend(new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Top));
        Assert.Equal(styled.AppearanceOptions, relabelled.AppearanceOptions);
        Assert.Equal("Wafer", relabelled.WithAppearance(GraphAppearanceOptions.Default).Frame.Title);
    }

    [Fact]
    public void ThePresentationCarriesTheConfigurationsAppearance()
    {
        var appearance = new GraphAppearanceOptions(Three, GraphGridMode.Hide);

        Assert.Equal(appearance, Build(GraphType.ScatterPlot, 2, appearance).State.AppearanceOptions);
        Assert.Same(GraphAppearanceOptions.Default, Build(GraphType.BoxPlot, 2).State.AppearanceOptions);
    }

    // ---- Series colours ----

    // A custom palette colours the plot of every graph type, and the default colours are gone from it.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void ACustomPaletteColorsEveryGraphType(GraphType type)
    {
        var built = Build(type, 3, new GraphAppearanceOptions(Three));
        using var image = Draw(built);
        var plot = Layout(built).PlotArea;

        foreach (var color in Three.Colors)
        {
            Assert.True(CountOf(image, Skia(color), plot, 2) > 0, $"{type}: {color} is not in the plot");
        }

        foreach (var color in GraphThemes.Light.SeriesPalette.Take(3))
        {
            Assert.Equal(0, CountOf(image, color, plot, 2));
        }
    }

    // The legend's swatches and the statistics panel's swatches are the series' colours too.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void TheSwatchesFollowThePalette(GraphType type)
    {
        var built = Build(type, 3, new GraphAppearanceOptions(Three));
        using var image = Draw(built);
        var layout = Layout(built);

        foreach (var color in Three.Colors)
        {
            Assert.True(CountOf(image, Skia(color), layout.LegendArea) > 20, $"{type}: legend swatch {color}");
            if (!layout.StatisticsPanelArea.IsEmpty)
            {
                Assert.True(CountOf(image, Skia(color), layout.StatisticsPanelArea) > 20, $"{type}: statistics swatch {color}");
            }
        }

        Assert.Equal(0, CountOf(image, GraphThemes.Light.SeriesColor(0), layout.LegendArea));
    }

    // The palette changes colours only: series, their order, the legend and the statistics are the graph's.
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(32)]
    [InlineData(100)]
    public void ThePaletteChangesNoSeries(int series)
    {
        var appearance = new GraphAppearanceOptions(new GraphPalette([Red, Green, Blue, Magenta, Navy]));
        var plain = Build(GraphType.ProbabilityPlot, series == 1 ? 0 : series);
        var styled = Build(GraphType.ProbabilityPlot, series == 1 ? 0 : series, appearance);
        var theme = GraphAppearance.Resolve(GraphThemes.Dark, styled.State.AppearanceOptions);

        Assert.Equal(plain.State.Frame.Legend?.Entries, styled.State.Frame.Legend?.Entries);
        Assert.Equal(
            plain.State.Frame.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex, row.CountText, row.MeanText)),
            styled.State.Frame.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex, row.CountText, row.MeanText)));
        foreach (var entry in styled.State.Frame.Legend?.Entries ?? [])
        {
            Assert.Equal(Skia(appearance.Palette!.Colors[entry.SeriesIndex % 5]), theme.SeriesColor(entry.SeriesIndex));
        }

        Assert.NotEmpty(Png(styled, GraphThemes.Dark));
    }

    // ---- The grid ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheGridIsDrawnInItsColourOrNotAtAll(GraphType type)
    {
        foreach (var (mode, drawn) in new[] { (GraphGridMode.Auto, true), (GraphGridMode.Show, true), (GraphGridMode.Hide, false) })
        {
            var built = Build(type, 0, new GraphAppearanceOptions(GridMode: mode, GridColor: Magenta));
            using var image = Draw(built);
            var grid = CountOf(image, Skia(Magenta), Layout(built).PlotArea, 2);

            Assert.True(drawn ? grid > 100 : grid == 0, $"{type} {mode}: {grid} grid pixels");
            Assert.Equal(0, CountOf(image, GraphThemes.Light.Grid, Layout(built).PlotArea, 2));
        }

        var hide = Build(type, 0, new GraphAppearanceOptions(GridMode: GraphGridMode.Hide));
        using var hidden = Draw(hide);
        using var shown = Draw(Build(type, 0));
        Assert.Equal(0, CountOf(hidden, GraphThemes.Light.Grid, Layout(hide).PlotArea, 2));
        Assert.True(CountOf(shown, GraphThemes.Light.Grid, Layout(hide).PlotArea, 2) > 100);
    }

    // ---- The backgrounds ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheBackgroundsTakeTheirColours(GraphType type)
    {
        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
        {
            var built = Build(type, 2, new GraphAppearanceOptions(PlotBackground: Sand, GraphBackground: Navy));
            using var image = Draw(built, theme);
            var layout = Layout(built, theme);

            // Around the plot, in its corners, in the legend's and the panel's boxes: the graph background.
            Assert.Equal(Skia(Navy), image.GetPixel(0, 0));
            Assert.Equal(Skia(Navy), image.GetPixel(image.Width - 1, image.Height - 1));
            Assert.True(CountOf(image, Skia(Navy), layout.LegendArea, 2) > 0);
            Assert.Equal(0, CountOf(image, Skia(Navy), layout.PlotArea, 2));

            // In the plot: the plot background, and nothing of the theme's own.
            Assert.True(CountOf(image, Skia(Sand), layout.PlotArea, 2) > 1000);
            Assert.Equal(0, CountOf(image, theme.PlotBackground, layout.PlotArea, 2));
            Assert.Equal(0, CountOf(image, theme.Background));
        }
    }

    // The normal fit's halo is the plot background - the one chosen, when one is.
    [Fact]
    public void TheNormalFitsHaloFollowsThePlotBackground()
    {
        var fit = new HistogramOptions(ShowNormalFit: true);
        foreach (var groups in new[] { 0, 3 })
        {
            using var plain = Draw(Build(GraphType.Histogram, groups, histogram: fit), GraphThemes.Dark);
            var built = Build(GraphType.Histogram, groups, new GraphAppearanceOptions(PlotBackground: Navy), fit);
            using var image = Draw(built, GraphThemes.Dark);
            var plot = Layout(built, GraphThemes.Dark).PlotArea;

            Assert.True(CountOf(plain, GraphThemes.Dark.PlotBackground, plot, 2) > 0);
            Assert.Equal(0, CountOf(image, GraphThemes.Dark.PlotBackground, plot, 2));
            Assert.True(CountOf(image, Skia(Navy), plot, 2) > 0);
        }
    }

    // A PNG is filled with the graph background, and so is the slide it goes on.
    [Fact]
    public async Task ExportsTakeTheChosenBackground()
    {
        var built = Build(GraphType.Histogram, 3, new GraphAppearanceOptions(Three, GraphBackground: Sand));
        var theme = GraphAppearance.Resolve(GraphThemes.Dark, built.State.AppearanceOptions);
        var snapshot = new GraphExportSnapshot(built.State.Frame, built.Plot, theme);
        using var directory = new TemporaryDirectory();
        var dialogs = new FakeGraphExportDialogs { PowerPointPath = directory.File("graph.pptx"), PngPath = directory.File("graph.png") };
        var powerPoint = new FakePowerPointGraphExporter();
        var clipboard = new FakeGraphImageClipboard();
        var controller = new GraphExportController(dialogs, new GraphExportService(), powerPoint, clipboard);

        await controller.ExportPowerPointAsync(snapshot, Token);
        await controller.ExportPngAsync(snapshot, Token);
        await controller.CopyImageAsync(snapshot, Token);

        Assert.Equal("F0E0D0", powerPoint.Last.Image.BackgroundHex);
        var png = File.ReadAllBytes(dialogs.PngPath!);
        Assert.Equal(png, powerPoint.Last.Image.Png);
        Assert.Equal(png, clipboard.Copied.Single());
        using var image = SKBitmap.Decode(png);
        Assert.Equal(Skia(Sand), image.GetPixel(0, 0));
    }

    // ---- An ungrouped histogram's bin outline ----

    [Fact]
    public void AnUngroupedHistogramsBarsAreOutlined()
    {
        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
        {
            foreach (var appearance in new[] { GraphAppearanceOptions.Default, new GraphAppearanceOptions(Three, PlotBackground: Sand) })
            {
                var built = Build(GraphType.Histogram, 0, appearance);
                using var image = Draw(built, theme);

                Assert.True(CountOf(image, theme.BinOutline, Layout(built, theme).PlotArea, 3) > 200, $"{appearance}: no outline");
            }
        }
    }

    // Overlaid series keep the outline of their own colour, which is what tells their shapes apart.
    [Fact]
    public void AGroupedHistogramKeepsItsSeriesOutlines()
    {
        var built = Build(GraphType.Histogram, 3, new GraphAppearanceOptions(Three));
        using var image = Draw(built);
        var plot = Layout(built).PlotArea;

        Assert.Equal(0, CountOf(image, GraphThemes.Light.BinOutline, plot, 3));
        Assert.All(Three.Colors, color => Assert.True(CountOf(image, Skia(color), plot, 3) > 100));
    }

    // Bars too narrow to outline are left as they were drawn, and outlining changes no bar.
    [Fact]
    public void OutliningChangesNoBarAndLeavesNarrowOnesAlone()
    {
        var narrow = Build(GraphType.Histogram, 0, histogram: new HistogramOptions(HistogramYScale.Frequency, HistogramBinningMode.Count, BinCount: HistogramOptions.MaximumBinCount));
        using var image = Draw(narrow);
        Assert.Equal(0, CountOf(image, GraphThemes.Light.BinOutline, Layout(narrow).PlotArea, 3));

        var built = Build(GraphType.Histogram, 0, histogram: new HistogramOptions(ShowNormalFit: true));
        var model = (HistogramRenderModel)built.Model;
        var counts = model.Series.Select(series => series.Counts.ToArray()).ToList();
        var heights = model.Series.Select(series => series.Heights.ToArray()).ToList();
        var fit = model.Series[0].NormalFit!.Points.ToArray();

        _ = Png(built);
        _ = Png(built, GraphThemes.Dark);

        Assert.Equal(counts, model.Series.Select(series => series.Counts.ToArray()));
        Assert.Equal(heights, model.Series.Select(series => series.Heights.ToArray()));
        Assert.Equal(fit, model.Series[0].NormalFit!.Points);
    }

    // ---- The default ----

    // With nothing chosen the graph is drawn in the theme itself: the very same image.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheDefaultDrawsTheGraphAsTheThemeDoes(GraphType type)
    {
        var built = Build(type, 3);
        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
        {
            Assert.Equal(
                new GraphExportService().RenderPng(new GraphExportSnapshot(built.State.Frame, built.Plot, theme)),
                Png(built, theme));
        }

        // Custom and back: the same image again.
        var back = built.State.WithAppearance(new GraphAppearanceOptions(Three, GraphGridMode.Hide, Magenta, Navy, Sand))
            .WithAppearance(GraphAppearanceOptions.Default);
        Assert.Equal(Png(built), Png(built with { State = back }));
    }
}
