using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The outline of a grouped histogram's bars (Task #046.1): overlaid series outline their bars in their own colour only
// where a bar is wider than 2 px. There the 1 px edges leave the semi-transparent fill between them; a bar 2 px wide or
// narrower keeps its fill alone, so no series drawn later hides one drawn before it behind an opaque bar. An ungrouped
// histogram keeps its own rule, unchanged: its neutral outline from 4 px up.
//
// The bars are drawn at widths known exactly: bins one unit wide from 0, drawn by the renderer onto a plot exactly as
// wide as the bins times the width asked for. The fills are semi-transparent, so a pixel of a series' opaque colour can
// only be its outline.
public class HistogramOutlineThresholdTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 400;

    private const float PlotHeight = 100;

    private static readonly HistogramOptions UnitBins = new(HistogramYScale.Frequency, HistogramBinningMode.WidthAndStart, BinWidth: 1, BinStart: 0);

    // Values in the middle of the unit bins 0..9, the two lots spread differently so their bars overlap unevenly.
    private static readonly double[] Values = [.. Enumerable.Range(0, Count).Select(i => (i % 2 == 0 ? i % 10 : (i * 7 % 10)) + 0.5)];

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static HistogramRenderModel Grouped() =>
        new HistogramRenderModelBuilder().Build(
            new UnivariateGraphData(
                GraphType.Histogram,
                Guid.Empty,
                Column("Reg1"),
                Values,
                new StringGroupData(Column("Lot", WorksheetDataType.String), (string?[])[.. Enumerable.Range(0, Count).Select(i => i % 2 == 0 ? "A" : "B")])),
            new HistogramPlotLabels("Reg1", "Lot"),
            UnitBins,
            Token)!;

    private static HistogramRenderModel Ungrouped(bool normalFit = false) =>
        new HistogramRenderModelBuilder().Build(
            new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), Values, null),
            new HistogramPlotLabels("Reg1"),
            UnitBins with { ShowNormalFit = normalFit },
            Token)!;

    // The plot alone, its bars exactly this wide, on a transparent canvas.
    private static SKBitmap Plot(HistogramRenderModel model, float barWidth, GraphTheme theme)
    {
        var bins = model.Bins;
        var width = bins.Count * barWidth;
        var transform = new GraphCoordinateTransform(
            new GraphAxisRange(bins[0].LowerEdge, bins[^1].UpperEdge),
            new GraphAxisRange(0, model.MaximumHeight),
            new SKRect(0, 0, width, PlotHeight));
        var bitmap = new SKBitmap((int)Math.Ceiling(width) + 2, (int)PlotHeight + 2);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        new HistogramRenderer(model).RenderPlot(canvas, transform, theme);
        return bitmap;
    }

    private static int Opaque(SKBitmap image, SKColor color) => image.Pixels.Count(pixel => pixel == color.WithAlpha(255));

    private static int Translucent(SKBitmap image) => image.Pixels.Count(pixel => pixel.Alpha is > 0 and < 255);

    public static TheoryData<float, bool> Widths => new()
    {
        { 1f, false },
        { 1.5f, false },
        { 2f, false },
        { 2.25f, true },
        { 3f, true },
        { 3.97f, true },
        { 4f, true },
        { 6f, true },
        { 12f, true }
    };

    private static readonly GraphTheme[] Themes =
    [
        GraphThemes.Light,
        GraphThemes.Dark,
        GraphAppearance.Resolve(GraphThemes.Light, new GraphAppearanceOptions(new GraphPalette([new GraphColor(0xD0, 0, 0), new GraphColor(0, 0x90, 0)])))
    ];

    [Fact]
    public void TheBarsAreAsWideAsAsked()
    {
        var model = Grouped();
        Assert.Equal(10, model.Bins.Count);
        Assert.Equal(0, model.Bins[0].LowerEdge);
        Assert.Equal(10, model.Bins[^1].UpperEdge);
        Assert.All(model.Bins, bin => Assert.Equal(1, bin.UpperEdge - bin.LowerEdge));
    }

    // Wider than 2 px: every series in its own opaque colour at its bars' edges. 2 px or narrower: none.
    [Theory]
    [MemberData(nameof(Widths))]
    public void AGroupedHistogramOutlinesOnlyItsBarsWiderThanTwoPixels(float width, bool outlined)
    {
        foreach (var theme in Themes)
        {
            using var image = Plot(Grouped(), width, theme);

            foreach (var series in new[] { 0, 1 })
            {
                var outline = Opaque(image, theme.SeriesColor(series));
                Assert.True(outlined ? outline > 0 : outline == 0, $"{width} px, series {series}: {outline} opaque outline pixels");
            }

            // The fills stay semi-transparent whatever the width: the overlap of the series still shows.
            Assert.True(Translucent(image) > 0, $"{width} px: no semi-transparent fill left");
        }
    }

    // Where a bar is outlined, its middle is still its fill: a 3 px bar is an outline on each side and fill between.
    [Theory]
    [InlineData(2.25f)]
    [InlineData(3f)]
    [InlineData(4f)]
    public void AnOutlinedNarrowBarKeepsItsFillInTheMiddle(float width)
    {
        using var image = Plot(Grouped(), width, GraphThemes.Light);

        // A column across the middle of each bin: semi-transparent somewhere down it.
        for (var bin = 0; bin < 10; bin++)
        {
            var x = (int)((bin + 0.5f) * width);
            var column = Enumerable.Range(0, (int)PlotHeight).Select(y => image.GetPixel(x, y)).ToList();
            Assert.Contains(column, pixel => pixel.Alpha is > 0 and < 255);
        }
    }

    // 2 px or narrower, the grouped bars are exactly what they were before the outline reached them: fills alone, the
    // later series never opaque over the earlier.
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    public void TwoPixelsOrNarrowerNoSeriesHidesAnother(float width)
    {
        using var image = Plot(Grouped(), width, GraphThemes.Light);

        Assert.DoesNotContain(image.Pixels, pixel => pixel.Alpha == 255);
        Assert.Equal(0, Opaque(image, GraphThemes.Light.SeriesColor(1)));
    }

    // One series keeps its own rule, unchanged by the grouped one: its neutral outline from 4 px up and none below - with
    // or without a normal fit, over the theme's plot background or one chosen - and its solid fill always there.
    [Theory]
    [InlineData(1f, false)]
    [InlineData(2f, false)]
    [InlineData(2.25f, false)]
    [InlineData(3f, false)]
    [InlineData(3.97f, false)]
    [InlineData(4f, true)]
    [InlineData(6f, true)]
    public void AnUngroupedHistogramKeepsItsFourPixelRule(float width, bool outlined)
    {
        var sand = GraphAppearance.Resolve(GraphThemes.Light, new GraphAppearanceOptions(PlotBackground: new GraphColor(0xF0, 0xE0, 0xD0)));
        foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark, sand })
        {
            foreach (var normalFit in new[] { false, true })
            {
                using var image = Plot(Ungrouped(normalFit), width, theme);
                var outline = Opaque(image, theme.BinOutline);

                Assert.True(outlined ? outline > 0 : outline == 0, $"{width} px, fit {normalFit}: {outline} bin outline pixels");
                Assert.True(Opaque(image, theme.SeriesColor(0)) > 0, $"{width} px: no solid fill left");
            }
        }
    }

    // Drawing - at any width, in any theme - changes nothing of what the histogram is.
    [Fact]
    public void OutliningChangesNothingOfTheHistogram()
    {
        var model = Grouped();
        var bins = model.Bins.Select(bin => (bin.LowerEdge, bin.UpperEdge)).ToList();
        var counts = model.Series.Select(series => series.Counts.ToArray()).ToList();
        var heights = model.Series.Select(series => series.Heights.ToArray()).ToList();
        var frame = model.Frame;

        foreach (var width in new[] { 1f, 2f, 2.25f, 3f, 4f, 6f })
        {
            foreach (var theme in Themes)
            {
                using var image = Plot(model, width, theme);
            }
        }

        Assert.Equal(bins, model.Bins.Select(bin => (bin.LowerEdge, bin.UpperEdge)));
        Assert.Equal(counts, model.Series.Select(series => series.Counts.ToArray()));
        Assert.Equal(heights, model.Series.Select(series => series.Heights.ToArray()));
        Assert.Same(frame, model.Frame);
        Assert.Equal([0, 1], model.Series.Select(series => series.SeriesIndex));
    }
}
