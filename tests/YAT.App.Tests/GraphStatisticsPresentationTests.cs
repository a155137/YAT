using SkiaSharp;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The statistics panel on a presented graph (Task #045): worked out whole with the graph, then kept, trimmed to the
// statistics chosen or hidden by the presentation, laid out beside the plot and drawn by the renderer. With every
// statistic shown the very frame comes back; nothing but the panel and the room it takes ever changes, and the panel's
// numbers are never worked out again.
public class GraphStatisticsPresentationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 1200;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)) + (i % 7 * 0.003))];
    private static readonly double[] Reg2 = [.. Enumerable.Range(0, Count).Select(i => 14.9 + (0.15 * Math.Cos(i * 0.23)))];

    private static readonly SKRect Canvas = new(0, 0, 800, 500);

    private static readonly GraphType[] PanelTypes = [GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf];

    public static TheoryData<GraphType> GraphTypesWithAPanel => [.. PanelTypes];

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData? Groups(int groups, Func<int, string>? name = null) => groups == 0
        ? null
        : new(Column("Lot", WorksheetDataType.String), (string?[])[.. Enumerable.Range(0, Count).Select(i => (string?)(name ?? (k => $"Lot {k}"))(i % groups))]);

    private sealed record Built(GraphPresentationState State, IGraphPlotRenderer Plot, GraphData Data, GraphRenderModel BuilderFrame);

    // A graph as the graph preparation presents it: the graph type's own frame, the panel, a specification, and the
    // options given.
    private static Built Build(
        GraphType type,
        int groups = 0,
        GraphStatisticsOptions? statistics = null,
        GraphLegendOptions? legend = null,
        Func<int, string>? name = null,
        UnivariateGraphData? data = null)
    {
        var univariate = data ?? new UnivariateGraphData(type, Guid.Empty, Column("Reg1"), Reg1, Groups(groups, name));
        var header = univariate.Group?.Column.Name;
        (GraphRenderModel Frame, IGraphPlotRenderer Plot) built = type switch
        {
            GraphType.Histogram => new HistogramRenderModelBuilder().Build(univariate, new HistogramPlotLabels(univariate.Variable.Name, header), Token) is { } histogram
                ? (histogram.Frame, new HistogramRenderer(histogram))
                : throw new InvalidOperationException(),
            GraphType.ProbabilityPlot => new ProbabilityPlotRenderModelBuilder().Build(univariate, new ProbabilityPlotLabels(univariate.Variable.Name, header), Token) is { } probability
                ? (probability.Frame, new ProbabilityPlotRenderer(probability))
                : throw new InvalidOperationException(),
            _ => new EmpiricalCdfRenderModelBuilder().Build(univariate, new EmpiricalCdfLabels(univariate.Variable.Name, header), Token) is { } ecdf
                ? (ecdf.Frame, new EmpiricalCdfRenderer(ecdf))
                : throw new InvalidOperationException()
        };

        var configuration = new GraphConfiguration(type, Guid.Empty, [])
        {
            StatisticsOptions = statistics ?? GraphStatisticsOptions.Default,
            LegendOptions = legend ?? GraphLegendOptions.Default,
            Specification = new Specification(14.8, 15, 15.3)
        };
        return new Built(GraphPresentation.Present(built.Frame, univariate, configuration, Token), built.Plot, univariate, built.Frame);
    }

    private static GraphStatisticsOptions Hide => new(GraphStatisticsMode.Hide);

    private static GraphStatisticsOptions Only(bool mean, bool deviation, bool count, GraphStatisticsMode mode = GraphStatisticsMode.Auto) =>
        new(mode, mean, deviation, count);

    private static GraphRenderModel Attach(GraphRenderModel frame, GraphType type, GraphStatisticsOptions options) =>
        GraphStatisticsPresentationBuilder.Attach(frame, GraphTypeDefinitions.For(type), options);

    private static byte[] Png(GraphRenderModel frame, IGraphPlotRenderer plot, GraphTheme? theme = null) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, theme ?? GraphThemes.Light));

    private static GraphLayout Layout(GraphRenderModel frame, SKRect? canvas = null) =>
        SkiaGraphRenderer.Layout(frame, canvas ?? Canvas, GraphThemes.Light);

    // Every choice of statistics a shown panel can have, in panel order.
    public static TheoryData<bool, bool, bool> Choices => new()
    {
        { true, false, false },
        { false, true, false },
        { false, false, true },
        { true, true, false },
        { true, false, true },
        { false, true, true },
        { true, true, true }
    };

    // ---- The presentation ----

    [Theory]
    [MemberData(nameof(GraphTypesWithAPanel))]
    public void EveryStatisticShownIsTheVeryFrame(GraphType type)
    {
        foreach (var groups in new[] { 0, 3, 40 })
        {
            var built = Build(type, groups);
            var frame = built.State.BaseFrame;

            Assert.NotNull(frame.StatisticsPanel);
            Assert.Same(GraphStatisticsOptions.AllItems, frame.StatisticsPanel!.Items);
            Assert.Same(frame, Attach(frame, type, GraphStatisticsOptions.Default));
            Assert.Same(frame, Attach(frame, type, new GraphStatisticsOptions(GraphStatisticsMode.Show)));
            Assert.Same(frame, Attach(frame, type, new GraphStatisticsOptions()));

            // With every other option at its default, the frame shown is the frame the graph was prepared with.
            Assert.Same(frame, built.State.Frame);
        }
    }

    [Theory]
    [MemberData(nameof(GraphTypesWithAPanel))]
    public void HideTakesThePanelOffTheFrameAndNothingElse(GraphType type)
    {
        var built = Build(type, 5);
        var frame = built.State.BaseFrame;
        var hidden = Attach(frame, type, Hide);

        Assert.Null(hidden.StatisticsPanel);
        Assert.Same(frame.XAxis, hidden.XAxis);
        Assert.Same(frame.YAxis, hidden.YAxis);
        Assert.Same(frame.Legend, hidden.Legend);
        Assert.Same(frame.ReferenceLines, hidden.ReferenceLines);
        Assert.Equal(frame.Title, hidden.Title);
        Assert.Equal(frame.LegendPosition, hidden.LegendPosition);

        // The layout gives the room back to the plot.
        var shown = Layout(frame);
        var without = Layout(hidden);
        Assert.True(without.StatisticsPanelArea.IsEmpty);
        Assert.True(without.PlotArea.Width > shown.PlotArea.Width);
    }

    // The panel is worked out with the graph whatever the options: hidden from the start, it is still there to show.
    [Theory]
    [MemberData(nameof(GraphTypesWithAPanel))]
    public void AHiddenPanelIsStillWorkedOut(GraphType type)
    {
        var hidden = Build(type, 4, Hide);
        var shown = Build(type, 4);

        Assert.Null(hidden.State.Frame.StatisticsPanel);
        var panel = hidden.State.BaseFrame.StatisticsPanel!;
        Assert.Equal(
            shown.State.BaseFrame.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex, row.Count, row.Mean, row.StandardDeviation, row.MeanText, row.StandardDeviationText, row.CountText)),
            panel.Rows.Select(row => (row.Label, row.SeriesIndex, row.Count, row.Mean, row.StandardDeviation, row.MeanText, row.StandardDeviationText, row.CountText)));

        var again = hidden.State.WithStatistics(GraphStatisticsOptions.Default);
        Assert.Same(hidden.State.BaseFrame, again.BaseFrame);
        Assert.Same(panel, again.Frame.StatisticsPanel);
        Assert.Equal(Png(shown.State.Frame, shown.Plot), Png(again.Frame, hidden.Plot));
    }

    [Theory]
    [MemberData(nameof(Choices))]
    public void TheStatisticsChosenAreShownInPanelOrder(bool mean, bool deviation, bool count)
    {
        foreach (var type in PanelTypes)
        {
            var built = Build(type, 3);
            var frame = built.State.BaseFrame;
            var options = Only(mean, deviation, count);
            var trimmed = Attach(frame, type, options);

            Assert.Equal(options.Items, trimmed.StatisticsPanel!.Items);
            Assert.Equal(
                [.. new[] { GraphStatisticsItem.Mean, GraphStatisticsItem.StandardDeviation, GraphStatisticsItem.Count }.Where(options.Items.Contains)],
                trimmed.StatisticsPanel.Items);

            // The same rows - numbers, texts, series and their order - only fewer of their statistics drawn.
            Assert.Equal(frame.StatisticsPanel!.Rows, trimmed.StatisticsPanel.Rows);
            Assert.Equal(frame.StatisticsPanel.Title, trimmed.StatisticsPanel.Title);
            Assert.Equal(frame.StatisticsPanel.GroupHeader, trimmed.StatisticsPanel.GroupHeader);
            Assert.Same(frame.Legend, trimmed.Legend);
            Assert.Same(frame.XAxis, trimmed.XAxis);

            // Show is Auto for a graph with a panel.
            var show = Attach(frame, type, Only(mean, deviation, count, GraphStatisticsMode.Show)).StatisticsPanel!;
            Assert.Equal(trimmed.StatisticsPanel.Items, show.Items);
            Assert.Equal(trimmed.StatisticsPanel.Rows, show.Rows);
        }
    }

    // Statistics chosen for a hidden panel stay chosen; they do not decide anything until it is shown again.
    [Fact]
    public void AHiddenPanelKeepsNoMatterWhatWasChosen()
    {
        var frame = Build(GraphType.Histogram, 3).State.BaseFrame;

        foreach (var options in new[] { Hide, Only(false, false, false, GraphStatisticsMode.Hide), Only(true, false, true, GraphStatisticsMode.Hide) })
        {
            Assert.Null(Attach(frame, GraphType.Histogram, options).StatisticsPanel);
        }
    }

    [Theory]
    [MemberData(nameof(GraphTypesWithAPanel))]
    public void AFrameWithoutAPanelGetsNoneWhateverTheOptions(GraphType type)
    {
        var frame = Build(type).BuilderFrame;
        Assert.Null(frame.StatisticsPanel);

        foreach (var options in new[] { GraphStatisticsOptions.Default, new GraphStatisticsOptions(GraphStatisticsMode.Show), Hide, Only(false, false, true) })
        {
            Assert.Same(frame, Attach(frame, type, options));
        }
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot)]
    [InlineData(GraphType.BoxPlot)]
    public void GraphTypesWithoutAPanelAreNeverTouched(GraphType type)
    {
        var frame = Build(GraphType.Histogram, 3).State.BaseFrame;

        Assert.Same(frame, Attach(frame, type, Hide));
        Assert.Same(frame, Attach(frame, type, Only(true, false, false)));
        Assert.Same(frame, Attach(frame, type, new GraphStatisticsOptions((GraphStatisticsMode)9, false, false, false)));
    }

    [Fact]
    public void OptionsThatAreNotValidAreRefused()
    {
        var frame = Build(GraphType.Histogram).State.BaseFrame;

        Assert.Throws<ArgumentException>(() => Attach(frame, GraphType.Histogram, Only(false, false, false)));
        Assert.Throws<ArgumentException>(() => Attach(frame, GraphType.Histogram, new GraphStatisticsOptions((GraphStatisticsMode)9)));
        Assert.Throws<ArgumentException>(() => Build(GraphType.Histogram).State.WithStatistics(Only(false, false, false, GraphStatisticsMode.Show)));
    }

    // A panel shows at least one statistic, each once, in panel order.
    [Fact]
    public void APanelsStatisticsAreCheckedWhenItIsMade()
    {
        var rows = Build(GraphType.Histogram).State.BaseFrame.StatisticsPanel!.Rows;

        Assert.Throws<ArgumentException>(() => new GraphStatisticsPanel("Statistics", null, rows, []));
        Assert.Throws<ArgumentException>(() => new GraphStatisticsPanel("Statistics", null, rows, [GraphStatisticsItem.Count, GraphStatisticsItem.Mean]));
        Assert.Throws<ArgumentException>(() => new GraphStatisticsPanel("Statistics", null, rows, [GraphStatisticsItem.Mean, GraphStatisticsItem.Mean]));
        Assert.Throws<ArgumentException>(() => new GraphStatisticsPanel("Statistics", null, rows, [(GraphStatisticsItem)5]));
        Assert.Same(GraphStatisticsOptions.AllItems, new GraphStatisticsPanel("Statistics", null, rows).Items);
    }

    // Any number of changes, in any order, and back to the default: the graph as it was first shown, to the pixel.
    [Theory]
    [MemberData(nameof(GraphTypesWithAPanel))]
    public void ChangesComeBackToTheGraphAsItWas(GraphType type)
    {
        var built = Build(type, 6);
        var first = Png(built.State.Frame, built.Plot);
        var state = built.State;

        foreach (var options in new[] { Hide, Only(true, false, false), Only(false, false, true), Hide, Only(false, true, true), GraphStatisticsOptions.Default, Only(true, true, false), Hide })
        {
            state = state
                .WithStatistics(options)
                .WithLegend(new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Top))
                .WithLegend(GraphLegendOptions.Default);
            Assert.Same(built.State.BaseFrame, state.BaseFrame);
        }

        var back = state.WithStatistics(GraphStatisticsOptions.Default);
        Assert.Same(built.State.BaseFrame, back.Frame);
        Assert.Equal(first, Png(back.Frame, built.Plot));

        // The same options twice give the same graph.
        var trimmed = back.WithStatistics(Only(true, false, true));
        Assert.Equal(Png(trimmed.Frame, built.Plot), Png(trimmed.WithStatistics(Only(true, false, true)).Frame, built.Plot));
    }

    // The statistics, the legend, the axis ranges and the labels are put on the one graph: each keeps the others.
    [Fact]
    public void TheStatisticsKeepTheLabelsRangesAndLegendAndTheyKeepThem()
    {
        var state = Build(GraphType.Histogram, 4).State
            .WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Auto, GraphLabelOption.Auto))
            .WithAxisRanges(new GraphAxisRangeOptions(new GraphAxisRangeOption(14.9, 15.1), GraphAxisRangeOption.Auto))
            .WithLegend(new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Bottom))
            .WithStatistics(Only(false, false, true));

        Assert.Equal("Wafer", state.Frame.Title);
        Assert.Equal(new GraphAxisRange(14.9, 15.1), state.Frame.XAxis.Range);
        Assert.Equal(GraphLegendPosition.Bottom, state.Frame.LegendPosition);
        Assert.Equal([GraphStatisticsItem.Count], state.Frame.StatisticsPanel!.Items);

        var relabelled = state.WithLabels(GraphLabelOptions.Default).WithLegend(GraphLegendOptions.Default);
        Assert.Equal([GraphStatisticsItem.Count], relabelled.Frame.StatisticsPanel!.Items);
        Assert.Equal(Only(false, false, true), relabelled.StatisticsOptions);
    }

    // The series behind the panel - their order, indexes and colours - are the graph's; the statistics never touch them.
    [Theory]
    [MemberData(nameof(GraphTypesWithAPanel))]
    public void TheSeriesAreTheGraphsWhateverIsShown(GraphType type)
    {
        var built = Build(type, 7);
        var rows = built.State.BaseFrame.StatisticsPanel!.Rows;
        var legend = built.State.BaseFrame.Legend!;

        Assert.Equal(Enumerable.Range(0, 7), rows.Select(row => row.SeriesIndex!.Value));
        Assert.Equal(legend.Entries.Select(entry => (entry.Label, entry.SeriesIndex)), rows.Select(row => (row.Label, row.SeriesIndex!.Value)));

        foreach (var options in new[] { Hide, Only(true, false, false), Only(false, false, true) })
        {
            var frame = built.State.WithStatistics(options).Frame;
            Assert.Same(legend, frame.Legend);
            Assert.All(frame.StatisticsPanel?.Rows ?? [], row => Assert.Contains(row, rows));
        }
    }

    // ---- Hide and the old "Show statistics" off ----

    // Hiding the panel draws exactly what the graph drew before Task #045 with statistics turned off: the graph type's
    // frame and its specification lines, without a panel ever put on it.
    [Theory]
    [MemberData(nameof(GraphTypesWithAPanel))]
    public void HideDrawsWhatStatisticsOffAlwaysDrew(GraphType type)
    {
        foreach (var groups in new[] { 0, 5, 30 })
        {
            var built = Build(type, groups, Hide);
            var definition = GraphTypeDefinitions.For(type);
            var neverPanelled = GraphSpecificationLinesBuilder.Attach(built.BuilderFrame, definition, new Specification(14.8, 15, 15.3));

            Assert.Null(built.State.Frame.StatisticsPanel);
            Assert.Equal(neverPanelled.ReferenceLines, built.State.Frame.ReferenceLines);
            Assert.Equal(neverPanelled.XAxis.Range, built.State.Frame.XAxis.Range);
            Assert.Equal(Png(neverPanelled, built.Plot), Png(built.State.Frame, built.Plot));
            Assert.Equal(Png(neverPanelled, built.Plot, GraphThemes.Dark), Png(built.State.Frame, built.Plot, GraphThemes.Dark));
        }
    }

    // ---- Drawing ----

    [Theory]
    [MemberData(nameof(Choices))]
    public void FewerStatisticsTakeLessRoom(bool mean, bool deviation, bool count)
    {
        foreach (var groups in new[] { 0, 4 })
        {
            var built = Build(GraphType.Histogram, groups);
            var all = Layout(built.State.Frame);
            var trimmed = Layout(built.State.WithStatistics(Only(mean, deviation, count)).Frame);

            Assert.False(trimmed.StatisticsPanelArea.IsEmpty);
            Assert.True(trimmed.StatisticsPanelArea.Width <= all.StatisticsPanelArea.Width + 0.001f);
            Assert.True(trimmed.PlotArea.Width >= all.PlotArea.Width - 0.001f);
        }
    }

    // Ungrouped, a panel has a line per statistic it shows: the room it needs grows with them.
    [Fact]
    public void AnUngroupedPanelHasALinePerStatistic()
    {
        var panel = Build(GraphType.Histogram).State.BaseFrame.StatisticsPanel!;
        var rowHeight = SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light);

        Assert.Equal((3, 0), SkiaGraphRenderer.FitStatisticsLines(panel, 500, rowHeight));
        Assert.Equal((1, 0), SkiaGraphRenderer.FitStatisticsLines(panel.WithItems([GraphStatisticsItem.Count]), 500, rowHeight));
        Assert.Equal((2, 0), SkiaGraphRenderer.FitStatisticsLines(panel.WithItems([GraphStatisticsItem.Mean, GraphStatisticsItem.Count]), 500, rowHeight));
    }

    // What a panel shows is what is drawn: a panel of N alone draws neither Mean nor StDev.
    [Fact]
    public void OnlyTheStatisticsShownAreDrawn()
    {
        var built = Build(GraphType.Histogram, 3);
        var all = Png(built.State.Frame, built.Plot);

        var images = new[] { Only(true, false, false), Only(false, true, false), Only(false, false, true), Only(true, false, true) }
            .Select(options => Png(built.State.WithStatistics(options).Frame, built.Plot))
            .ToList();

        Assert.All(images, image => Assert.NotEqual(all, image));
        Assert.Equal(images.Count, images.Select(Convert.ToHexString).Distinct().Count());
    }

    // A single observation has no standard deviation: "—", shown alone or with the others.
    [Fact]
    public void ASingleObservationStillShowsNoStandardDeviation()
    {
        var single = new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), new double[] { 15 }, null);
        var built = Build(GraphType.Histogram, data: single);
        var row = Assert.Single(built.State.BaseFrame.StatisticsPanel!.Rows);

        Assert.Equal(GraphStatisticsPanelBuilder.UndefinedText, row.StandardDeviationText);
        Assert.Null(row.StandardDeviation);
        var deviation = built.State.WithStatistics(Only(false, true, false)).Frame;
        Assert.Same(row, Assert.Single(deviation.StatisticsPanel!.Rows));
        Assert.NotEmpty(Png(deviation, built.Plot));
    }

    // Several variables drawn together: one panel of series named "Variable / Lot", trimmed like any other.
    [Fact]
    public void VariablesDrawnTogetherShareOnePanel()
    {
        var group = Groups(3);
        var together = GraphVariablesTogether.Combine(new MultiVariableGraphData(GraphType.EmpiricalCdf, Guid.Empty,
        [
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.Empty, Column("Reg1"), Reg1, group),
            new UnivariateGraphData(GraphType.EmpiricalCdf, Guid.Empty, Column("Reg2"), Reg2, group)
        ]), Token);
        var built = Build(GraphType.EmpiricalCdf, data: together, statistics: Only(true, false, false));

        var panel = built.State.Frame.StatisticsPanel!;
        Assert.Equal("Variable / Lot", panel.GroupHeader);
        Assert.Equal(6, panel.Rows.Count);
        Assert.Equal([GraphStatisticsItem.Mean], panel.Items);
        Assert.Equal(built.State.BaseFrame.StatisticsPanel!.Rows, panel.Rows);
    }

    // ---- The panel beside the legend (Task #044) ----

    public static TheoryData<GraphLegendPosition> Positions => [.. Enum.GetValues<GraphLegendPosition>()];

    [Theory]
    [MemberData(nameof(Positions))]
    public void AHiddenPanelGivesItsRoomBackWhereverTheLegendIs(GraphLegendPosition position)
    {
        var legend = new GraphLegendOptions(GraphLegendMode.Auto, position);
        var shown = Build(GraphType.Histogram, 6, legend: legend).State.Frame;
        var hidden = Build(GraphType.Histogram, 6, Hide, legend).State.Frame;

        var with = Layout(shown);
        var without = Layout(hidden);
        Assert.False(with.StatisticsPanelArea.IsEmpty);
        Assert.True(without.StatisticsPanelArea.IsEmpty);
        Assert.False(without.LegendArea.IsEmpty);
        Assert.True(without.PlotArea.Right > with.PlotArea.Right);
        Assert.Equal(shown.Legend!.Entries, hidden.Legend!.Entries);
        Assert.Equal(position, hidden.LegendPosition);

        if (position == GraphLegendPosition.Right)
        {
            // The legend has the right-hand column to itself again: the plot's full height to grow into.
            Assert.True(without.LegendArea.Height >= with.LegendArea.Height);
        }
        else
        {
            // Beside no panel, the legend on another side stays where it was put.
            Assert.True(position switch
            {
                GraphLegendPosition.Left => without.LegendArea.Right <= without.PlotArea.Left,
                GraphLegendPosition.Top => without.LegendArea.Bottom <= without.PlotArea.Top,
                _ => without.LegendArea.Top >= without.PlotArea.Bottom
            });
        }
    }

    // Above a panel a long legend on the right is arranged into half the column; with the panel hidden it has the whole
    // column, as a legend without a panel always had.
    [Fact]
    public void ARightLegendArrangedAboveThePanelHasTheColumnToItselfWhenThePanelIsHidden()
    {
        var shown = Build(GraphType.ProbabilityPlot, 14).State.Frame;
        var hidden = Build(GraphType.ProbabilityPlot, 14, Hide).State.Frame;

        var with = Layout(shown);
        var without = Layout(hidden);
        Assert.NotNull(with.LegendArrangement);
        Assert.True(with.LegendArea.Height <= with.PlotArea.Height * 0.5f + 0.5f);
        Assert.True(without.LegendArea.Height > with.LegendArea.Height);
    }

    // A hundred series: the legend says how many more there are and so does the panel, each in its own room.
    [Fact]
    public void AHundredSeriesInTheLegendAndThePanel()
    {
        foreach (var options in new[] { GraphStatisticsOptions.Default, Only(false, false, true), Only(true, true, false) })
        {
            var frame = Build(GraphType.ProbabilityPlot, 100, options).State.Frame;
            var layout = Layout(frame);

            Assert.True(layout.LegendArrangement!.HiddenCount > 0);
            var (lines, more) = SkiaGraphRenderer.FitStatisticsLines(
                frame.StatisticsPanel!, layout.StatisticsPanelArea.Height, SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light));
            Assert.True(more > 0);
            Assert.Equal(100, lines - 1 + more);
            Assert.False(layout.LegendArea.IntersectsWith(layout.StatisticsPanelArea));
            Assert.NotEmpty(Png(frame, Build(GraphType.ProbabilityPlot, 100, options).Plot));
        }
    }

    // ---- Names and canvases ----

    public static TheoryData<string> Names => new()
    {
        "Lot",
        "晶圓批次",
        "非常に長いロット名称の測定データグループ",
        "A very long lot name that is much wider than the statistics panel could ever be"
    };

    [Theory]
    [MemberData(nameof(Names))]
    public void LongAndCjkNamesAreDrawnWhateverIsShown(string name)
    {
        foreach (var type in PanelTypes)
        {
            foreach (var options in new[] { GraphStatisticsOptions.Default, Only(true, false, false), Only(false, false, true), Hide })
            {
                var built = Build(type, 4, options, name: k => $"{name} {k}");
                foreach (var canvas in new[] { new SKRect(0, 0, 1600, 1000), new SKRect(0, 0, 760, 488), new SKRect(0, 0, 360, 260) })
                {
                    var layout = Layout(built.State.Frame, canvas);
                    Assert.True(layout.StatisticsPanelArea.IsEmpty || canvas.Contains(layout.StatisticsPanelArea));
                }

                Assert.NotEmpty(Png(built.State.Frame, built.Plot));
            }
        }
    }

    [Fact]
    public void ATinyCanvasDrawsNothingButTheBackgroundWhateverIsShown()
    {
        var built = Build(GraphType.Histogram, 3, Only(false, false, true));
        using var bitmap = new SKBitmap(60, 40);
        using var canvas = new SKCanvas(bitmap);

        new SkiaGraphRenderer().Render(canvas, built.State.Frame, new SKRect(0, 0, 60, 40), GraphThemes.Light, built.Plot);
        Assert.False(Layout(built.State.Frame, new SKRect(0, 0, 60, 40)).HasPlotArea);
    }
}
