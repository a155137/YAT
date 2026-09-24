using SkiaSharp;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The legend on a presented graph (Task #044): kept, hidden or moved by the presentation, laid out on its side by the
// layout, and drawn - arranged in columns or rows, cut short with an ellipsis, "… N more" for what does not fit - by the
// renderer. With the legend where it always was the very frame comes back; nothing but the legend and the room it takes
// ever changes.
public class GraphLegendPresentationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 1200;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)) + (i % 7 * 0.003))];
    private static readonly double[] Reg2 = [.. Enumerable.Range(0, Count).Select(i => 14.9 + (0.15 * Math.Cos(i * 0.23)))];

    private static readonly SKRect Canvas = new(0, 0, 800, 500);

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData? Groups(int groups, Func<int, string>? name = null) => groups == 0
        ? null
        : new(Column("Lot", WorksheetDataType.String), (string?[])[.. Enumerable.Range(0, Count).Select(i => (string?)(name ?? (k => $"Lot {k}"))(i % groups))]);

    private sealed record Built(GraphRenderModel Frame, IGraphPlotRenderer Plot, GraphData Data, object Model);

    private static Built Build(GraphType type, int groups, bool statistics = true, Func<int, string>? name = null)
    {
        var group = Groups(groups, name);
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
                var model = new HistogramRenderModelBuilder().Build(univariate, new HistogramPlotLabels("Reg1", group?.Column.Name), Token)!;
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

        var configuration = new GraphConfiguration(type, Guid.Empty, [])
        {
            StatisticsOptions = new GraphStatisticsOptions(statistics ? GraphStatisticsMode.Auto : GraphStatisticsMode.Hide),
            Specification = GraphTypeDefinitions.For(type).Supports(GraphCapability.SpecificationLines) ? new Specification(14.8, 15, 15.3) : Specification.None
        };
        var frame = GraphPresentation.Present(built.Frame, data, configuration, Token).Frame;
        return new Built(frame, built.Plot, data, built.Model);
    }

    private static GraphLegendOptions Options(GraphLegendMode mode, GraphLegendPosition position = GraphLegendPosition.Right) => new(mode, position);

    private static GraphRenderModel Attach(GraphRenderModel frame, GraphType type, GraphLegendOptions options) =>
        GraphLegendPresentationBuilder.Attach(frame, GraphTypeDefinitions.For(type), options);

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    public static TheoryData<GraphLegendPosition> Positions => [.. Enum.GetValues<GraphLegendPosition>()];

    // ---- The presentation ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void TheLegendWhereItAlwaysWasIsTheVeryFrame(GraphType type)
    {
        foreach (var groups in new[] { 0, 3, 40 })
        {
            var frame = Build(type, groups).Frame;
            Assert.Same(frame, Attach(frame, type, GraphLegendOptions.Default));
            Assert.Same(frame, Attach(frame, type, Options(GraphLegendMode.Show)));
            Assert.Same(frame, Attach(frame, type, new GraphLegendOptions()));
        }
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void HideTakesTheLegendOffTheFrameAndNothingElse(GraphType type)
    {
        var built = Build(type, 5);
        var hidden = Attach(built.Frame, type, Options(GraphLegendMode.Hide));

        Assert.Null(hidden.Legend);
        Assert.Same(built.Frame.XAxis, hidden.XAxis);
        Assert.Same(built.Frame.YAxis, hidden.YAxis);
        Assert.Same(built.Frame.StatisticsPanel, hidden.StatisticsPanel);
        Assert.Same(built.Frame.ReferenceLines, hidden.ReferenceLines);
        Assert.Equal(built.Frame.Title, hidden.Title);

        // The layout gives the room back to the plot.
        var shown = SkiaGraphRenderer.Layout(built.Frame, Canvas, GraphThemes.Light);
        var without = SkiaGraphRenderer.Layout(hidden, Canvas, GraphThemes.Light);
        Assert.True(without.LegendArea.IsEmpty);
        Assert.True(without.PlotArea.Width >= shown.PlotArea.Width);
    }

    // A graph of one unnamed series has no legend: Show makes none up, and Hide has none to hide.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AGraphWithoutALegendGetsNoneWhateverTheOptions(GraphType type)
    {
        var frame = Build(type, 0).Frame;
        Assert.Null(frame.Legend);

        foreach (var mode in Enum.GetValues<GraphLegendMode>())
        {
            foreach (var position in Enum.GetValues<GraphLegendPosition>())
            {
                var shown = Attach(frame, type, Options(mode, position));
                Assert.Null(shown.Legend);
                Assert.Same(frame.XAxis, shown.XAxis);
                if (position == GraphLegendPosition.Right)
                {
                    Assert.Same(frame, shown);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void APositionOnlySetsTheSideTheLegendStandsOn(GraphLegendPosition position)
    {
        var built = Build(GraphType.Histogram, 6);
        var moved = Attach(built.Frame, GraphType.Histogram, Options(GraphLegendMode.Auto, position));

        Assert.Equal(position, moved.LegendPosition);
        Assert.Same(built.Frame.Legend, moved.Legend);
        Assert.Same(built.Frame.XAxis, moved.XAxis);
        Assert.Same(built.Frame.StatisticsPanel, moved.StatisticsPanel);
    }

    [Fact]
    public void OptionsThatAreNotChoicesAreRefused() =>
        Assert.Throws<ArgumentException>(() => Attach(Build(GraphType.Histogram, 3).Frame, GraphType.Histogram,
            new GraphLegendOptions((GraphLegendMode)9, GraphLegendPosition.Right)));

    // Legend, axis ranges and labels are all put on the one base frame; each change keeps the others.
    [Fact]
    public void LegendRangesAndLabelsAreEditedOnOneBaseFrame()
    {
        var built = Build(GraphType.ProbabilityPlot, 4);
        var state = new GraphPresentationState(built.Frame, GraphTypeDefinitions.For(GraphType.ProbabilityPlot), GraphLabelOptions.Default);
        var labels = new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Auto, GraphLabelOption.Auto);
        var ranges = new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, 15.1), GraphAxisRangeOption.Auto);

        var edited = state.WithLabels(labels).WithAxisRanges(ranges).WithLegend(Options(GraphLegendMode.Auto, GraphLegendPosition.Bottom));
        Assert.Equal("Wafer", edited.Frame.Title);
        Assert.Equal(new GraphAxisRange(14.9, 15.1), edited.Frame.XAxis.Range);
        Assert.Equal(GraphLegendPosition.Bottom, edited.Frame.LegendPosition);
        Assert.Same(built.Frame.Legend, edited.Frame.Legend);

        var hidden = edited.WithLegend(Options(GraphLegendMode.Hide));
        Assert.Null(hidden.Frame.Legend);
        Assert.Equal("Wafer", hidden.Frame.Title);
        Assert.Equal(new GraphAxisRange(14.9, 15.1), hidden.Frame.XAxis.Range);

        var back = hidden.WithLegend(GraphLegendOptions.Default).WithLabels(GraphLabelOptions.Default).WithAxisRanges(GraphAxisRangeOptions.Default);
        Assert.Same(back.BaseFrame, back.Frame);
    }

    // ---- Where it stands ----

    [Theory]
    [MemberData(nameof(Positions))]
    public void EachSideTakesItsRoomFromThePlotAndLeavesTheAxesTheirs(GraphLegendPosition position)
    {
        var built = Build(GraphType.Histogram, 6);
        var frame = Attach(built.Frame, GraphType.Histogram, Options(GraphLegendMode.Show, position));
        var layout = SkiaGraphRenderer.Layout(frame, Canvas, GraphThemes.Light);
        var legend = layout.LegendArea;
        var plot = layout.PlotArea;

        Assert.False(legend.IsEmpty);
        Assert.True(Canvas.Contains(legend));
        Assert.False(legend.IntersectsWith(plot));
        Assert.False(legend.IntersectsWith(layout.XAxisArea));
        Assert.False(legend.IntersectsWith(layout.YAxisArea));
        Assert.False(legend.IntersectsWith(layout.StatisticsPanelArea));
        switch (position)
        {
            case GraphLegendPosition.Right:
                Assert.True(legend.Left >= plot.Right);
                break;
            case GraphLegendPosition.Left:
                Assert.True(legend.Right <= layout.YAxisArea.Left);
                Assert.Equal(plot.Top, layout.StatisticsPanelArea.Top);
                break;
            case GraphLegendPosition.Top:
                Assert.True(legend.Top >= layout.TitleArea.Bottom && legend.Bottom <= plot.Top);
                Assert.Equal(plot.Top, layout.StatisticsPanelArea.Top);
                break;
            default:
                Assert.True(legend.Top >= layout.XAxisArea.Bottom);
                Assert.Equal(plot.Bottom, layout.StatisticsPanelArea.Bottom);
                break;
        }
    }

    // A legend its single column on the right holds is laid out and drawn exactly as it always was.
    [Fact]
    public void ALegendThatFitsOnTheRightIsLaidOutAsItAlwaysWas()
    {
        var frame = Build(GraphType.Histogram, 4).Frame;
        var layout = SkiaGraphRenderer.Layout(frame, Canvas, GraphThemes.Light);

        Assert.Null(layout.LegendArrangement);
        Assert.Equal(layout.PlotArea.Top, layout.LegendArea.Top);
    }

    [Theory]
    [InlineData(GraphLegendPosition.Right, false)]
    [InlineData(GraphLegendPosition.Right, true)]
    [InlineData(GraphLegendPosition.Left, false)]
    [InlineData(GraphLegendPosition.Top, true)]
    [InlineData(GraphLegendPosition.Bottom, false)]
    public void AHundredSeriesKeepToTheirShareAndCountTheRest(GraphLegendPosition position, bool statistics)
    {
        var built = Build(GraphType.ProbabilityPlot, 100, statistics);
        var frame = Attach(built.Frame, GraphType.ProbabilityPlot, Options(GraphLegendMode.Auto, position));
        var layout = SkiaGraphRenderer.Layout(frame, Canvas, GraphThemes.Light);
        var content = new SKRect(Canvas.Left + 12, Canvas.Top + 12, Canvas.Right - 12, Canvas.Bottom - 12);
        var arrangement = layout.LegendArrangement!;

        Assert.True(arrangement.HiddenCount > 0);
        Assert.Contains(arrangement.Cells, cell => cell.IsMore);
        Assert.Equal(100, arrangement.Cells.Count(cell => !cell.IsMore) + arrangement.HiddenCount);
        if (position is GraphLegendPosition.Right or GraphLegendPosition.Left)
        {
            Assert.True(layout.LegendArea.Width <= (content.Width * GraphLayoutCalculator.MaximumLegendShareBeside) + 0.01f);
            var share = position == GraphLegendPosition.Right && statistics ? GraphLayoutCalculator.MaximumLegendShareWithPanel : 1f;
            Assert.True(layout.LegendArea.Height <= (layout.PlotArea.Height * share) + 0.01f);
        }
        else
        {
            Assert.True(layout.LegendArea.Height <= (content.Height * GraphLayoutCalculator.MaximumLegendShareAboveOrBelow) + 0.01f);
            Assert.True(layout.LegendArea.Width <= layout.PlotArea.Width + 0.01f);
        }
    }

    // ---- How it is drawn ----

    [Theory]
    [InlineData("Lot 7 of the wafer fabrication run measured at site north-east")]
    [InlineData("批次 7 晶圓 產品A 站點 北東 測定 批次 7 晶圓 產品A 站點 北東 測定")]
    [InlineData("𠀀𠀁𠀂𠀃𠀄𠀅𠀆𠀇𠀈𠀉𠀊𠀋𠀌𠀍𠀎𠀏𠀐𠀑𠀒𠀓𠀔𠀕")]
    public void ALongLabelIsCutShortWithAnEllipsisNeverThroughACharacter(string label)
    {
        using var font = new SKFont { Size = GraphThemes.Light.TickLabelFontSize, Edging = SKFontEdging.Antialias, Subpixel = true };
        const float Width = 220 - 16 - 16;
        var shown = SkiaGraphRenderer.Ellipsize(label, font, Width);

        Assert.EndsWith("…", shown);
        Assert.StartsWith(shown[..^1], label, StringComparison.Ordinal);
        Assert.False(char.IsHighSurrogate(shown[^2]));
        Assert.True(GraphTextFallback.MeasureText(font, shown) <= Width);
    }

    [Fact]
    public void WhatStandsForTheEntriesLeftOut()
    {
        Assert.Equal("… 29 more", SkiaGraphRenderer.MoreText(29));
        Assert.Equal("… 000 more", SkiaGraphRenderer.MoreText(100, digitsOnly: true));
    }

    [Theory]
    [MemberData(nameof(Positions))]
    public void TheSwatchesAreDrawnInTheLegendsAreaOnItsSide(GraphLegendPosition position)
    {
        var built = Build(GraphType.EmpiricalCdf, 30);
        var frame = Attach(built.Frame, GraphType.EmpiricalCdf, Options(GraphLegendMode.Auto, position));
        var layout = SkiaGraphRenderer.Layout(frame, Canvas, GraphThemes.Dark);
        using var bitmap = new SKBitmap((int)Canvas.Width, (int)Canvas.Height);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, frame, Canvas, GraphThemes.Dark, built.Plot);

        // The swatch of series 9 (colour 1 again, past the palette) is drawn in the legend's area, its first cell in place.
        var cell = layout.LegendArrangement!.Cells.First(item => item.EntryIndex == 9);
        var swatch = new SKPointI((int)(layout.LegendArea.Left + cell.Bounds.Left + 5), (int)(layout.LegendArea.Top + cell.Bounds.MidY));
        Assert.Equal(GraphThemes.Dark.SeriesColor(9), bitmap.GetPixel(swatch.X, swatch.Y));
    }

    // The export draws the legend as the window does: moved, hidden, or where it always was.
    [Fact]
    public void ThePngCarriesTheLegendAsPresented()
    {
        var built = Build(GraphType.Histogram, 12);
        var state = new GraphPresentationState(built.Frame, GraphTypeDefinitions.For(GraphType.Histogram), GraphLabelOptions.Default);
        var service = new GraphExportService();
        byte[] Png(GraphPresentationState graph) => service.RenderPng(new GraphExportSnapshot(graph.Frame, built.Plot, GraphThemes.Light));

        var right = Png(state);
        var top = Png(state.WithLegend(Options(GraphLegendMode.Auto, GraphLegendPosition.Top)));
        var hidden = Png(state.WithLegend(Options(GraphLegendMode.Hide)));

        Assert.Equal(3, new[] { right, top, hidden }.Select(Convert.ToBase64String).Distinct().Count());
        Assert.Equal(right, Png(state.WithLegend(Options(GraphLegendMode.Hide)).WithLegend(GraphLegendOptions.Default)));
    }
}
