using SkiaSharp;
using YAT.Analytics.Statistics;
using YAT.Application.Graphs;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The five-number summary in a graph's statistics panel (Task #062): Min, Q1, Median, Q3 and Max, worked out with Mean,
// StDev and N - which come out exactly as they always did - in YAT's one quantile convention, off unless chosen, shown
// in a fixed order; and a grouped panel too wide for its table drawn as a block per series instead, decided from the
// room the panel is drawn in.
public class GraphStatisticsEnhancementTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static UnivariateGraphData Data(double[] values, GraphGroupData? group = null) =>
        new(GraphType.Histogram, Guid.Empty, Column("Reg1"), values, group);

    private static readonly double[] Values = [15.02, 14.97, 15.10, 15.00, 15.05, 14.95, 15.08, 15.12, 14.89, 15.01, 15.03];

    // ---- The numbers ----

    [Fact]
    public void TheFiveNumbersAreTheDescriptiveStatisticsTablesOwn()
    {
        var row = GraphStatisticsPanelBuilder.Build(Data(Values), Token)!.Rows.Single();

        var sorted = Values.ToArray();
        var summary = DescriptiveSummary.ComputeInPlaceSorting(sorted, 0);
        Assert.Equal(
            (summary.Minimum, summary.FirstQuartile, summary.Median, summary.ThirdQuartile, summary.Maximum),
            ((double?)row.FiveNumbers!.Minimum, (double?)row.FiveNumbers.FirstQuartile, (double?)row.FiveNumbers.Median,
                (double?)row.FiveNumbers.ThirdQuartile, (double?)row.FiveNumbers.Maximum));

        // R-7: with eleven values Q1 lies between the 3rd and 4th of them (14.97, 15.00), Q3 between the 8th and 9th.
        Assert.Equal(("14.89", "14.985", "15.02", "15.065", "15.12"), (row.FiveNumbers.MinimumText, row.FiveNumbers.FirstQuartileText, row.FiveNumbers.MedianText, row.FiveNumbers.ThirdQuartileText, row.FiveNumbers.MaximumText));
    }

    [Fact]
    public void MeanAndStDevAreWorkedOutAsTheyAlwaysWereAndTheDataIsNotReordered()
    {
        var values = Values.ToArray();
        var groups = new StringGroupData(Column("Lot", WorksheetDataType.String), (string?[])["B", "A", "B", "A", "B", "A", "B", "A", "B", "A", "B"]);

        var ungrouped = GraphStatisticsPanelBuilder.Build(Data(values), Token)!.Rows.Single();
        var grouped = GraphStatisticsPanelBuilder.Build(Data(values, groups), Token)!.Rows;

        // The very bits Descriptives gives the values in row order - before anything is sorted.
        Assert.Equal(Descriptives.Mean(Values), ungrouped.Mean);
        Assert.Equal(Descriptives.StandardDeviation(Values), ungrouped.StandardDeviation);
        double[] b = [.. Values.Where((_, index) => index % 2 == 0)];
        Assert.Equal(Descriptives.Mean(b), grouped[0].Mean);
        Assert.Equal(Descriptives.StandardDeviation(b), grouped[0].StandardDeviation);
        Assert.Equal(b.Min(), grouped[0].FiveNumbers!.Minimum);

        Assert.Equal(Values, values);
    }

    [Fact]
    public void ASingleObservationIsItsOwnFiveNumbers()
    {
        var row = GraphStatisticsPanelBuilder.Build(Data([15.5]), Token)!.Rows.Single();

        Assert.Equal((15.5, 15.5, 15.5, 15.5, 15.5), (row.FiveNumbers!.Minimum, row.FiveNumbers.FirstQuartile, row.FiveNumbers.Median, row.FiveNumbers.ThirdQuartile, row.FiveNumbers.Maximum));
        Assert.Equal(GraphStatisticsPanelBuilder.UndefinedText, row.StandardDeviationText);
    }

    // ---- The options ----

    [Fact]
    public void TheStatisticsComeInOneFixedOrder()
    {
        Assert.Equal(
            [GraphStatisticsItem.Mean, GraphStatisticsItem.StandardDeviation, GraphStatisticsItem.Count, GraphStatisticsItem.Minimum,
                GraphStatisticsItem.FirstQuartile, GraphStatisticsItem.Median, GraphStatisticsItem.ThirdQuartile, GraphStatisticsItem.Maximum],
            GraphStatisticsOptions.AllItems);

        var options = new GraphStatisticsOptions(GraphStatisticsMode.Auto, false, false, false, ShowMaximum: true, ShowFirstQuartile: true);
        Assert.Equal([GraphStatisticsItem.FirstQuartile, GraphStatisticsItem.Maximum], options.Items);
        Assert.True(options.IsValid);

        var frame = Frame(Data(Values));
        var shown = GraphStatisticsPresentationBuilder.Attach(frame, GraphTypeDefinitions.For(GraphType.Histogram), options);
        Assert.Equal([GraphStatisticsItem.FirstQuartile, GraphStatisticsItem.Maximum], shown.StatisticsPanel!.Items);
        Assert.Equal(frame.StatisticsPanel!.Rows, shown.StatisticsPanel.Rows);
    }

    // ---- Table or blocks ----

    private static readonly GraphStatisticsOptions Everything =
        new(GraphStatisticsMode.Auto, true, true, true, true, true, true, true, true);

    private static StringGroupData Lots(int count) =>
        new(Column("Lot", WorksheetDataType.String), Enumerable.Range(0, count).Select(index => (string?)$"Lot {(char)('A' + (index % 3))}").ToArray());

    private static GraphRenderModel Frame(UnivariateGraphData data, GraphStatisticsOptions? options = null)
    {
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", data.Group?.Column.Name), Token)!;
        var state = GraphPresentation.Present(model.Frame, data, new GraphConfiguration(GraphType.Histogram, Guid.Empty, []), Token);
        return (options is null ? state : state.WithStatistics(options)).Frame;
    }

    [Fact]
    public void MeanStDevAndNOfGroupsStayATable()
    {
        var panel = Frame(Data(Values, Lots(Values.Length))).StatisticsPanel!;
        using var font = new SKFont { Size = GraphThemes.Light.TickLabelFontSize };

        Assert.True(SkiaGraphRenderer.FitsAsTable(panel, font, 260));
    }

    [Fact]
    public void EveryStatisticOfGroupsIsTooWideForTheTableBesideTheGraph()
    {
        var panel = Frame(Data(Values, Lots(Values.Length)), Everything).StatisticsPanel!;
        using var font = new SKFont { Size = GraphThemes.Light.TickLabelFontSize };

        Assert.False(SkiaGraphRenderer.FitsAsTable(panel, font, 260));
        Assert.True(SkiaGraphRenderer.FitsAsTable(panel, font, 1000));
    }

    [Fact]
    public void BlocksFitWholeOrAreCountedInTheMoreLine()
    {
        var panel = Frame(Data(Values, Lots(Values.Length)), Everything).StatisticsPanel!;
        var rowHeight = SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light);

        // Three lots of nine lines each, under the title: all of them, or two and "… 1 more", or one and "… 2 more".
        Assert.Equal((3, 0, 0), SkiaGraphRenderer.FitStatisticsBlocks(panel, 2000, rowHeight));
        Assert.Equal((2, 0, 1), SkiaGraphRenderer.FitStatisticsBlocks(panel, Height(1 + 18 + 1, rowHeight), rowHeight));
        Assert.Equal((1, 0, 2), SkiaGraphRenderer.FitStatisticsBlocks(panel, Height(1 + 9 + 1, rowHeight), rowHeight));
    }

    // Room for no whole block: the first series as far as it fits - its label, at least one statistic, "…" for the rest -
    // then "… k more" for the others. Only with no room even for that is the panel just "… k more".
    [Theory]
    [InlineData(9, 0, 6, 2)]  // label, six statistics, "…", "… 2 more"
    [InlineData(8, 0, 5, 2)]
    [InlineData(4, 0, 1, 2)]  // label, one statistic, "…", "… 2 more": the least that shows a statistic
    [InlineData(3, 0, 0, 3)]  // no room for a statistic: "… 3 more" alone
    [InlineData(1, 0, 0, 3)]
    public void WithoutRoomForAWholeBlockTheFirstSeriesIsShownAsFarAsItFits(int lines, int blocks, int partial, int more)
    {
        var panel = Frame(Data(Values, Lots(Values.Length)), Everything).StatisticsPanel!;
        var rowHeight = SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light);

        Assert.Equal((blocks, partial, more), SkiaGraphRenderer.FitStatisticsBlocks(panel, Height(1 + lines, rowHeight), rowHeight));
    }

    [Fact]
    public void OneSeriesShownInPartNeedsNoMoreLine()
    {
        // Two series of one variable: with room for three lines under the title, the first shows one statistic and "…".
        var groups = new StringGroupData(Column("Lot", WorksheetDataType.String), Values.Select((_, index) => (string?)(index % 2 == 0 ? "A" : "B")).ToArray());
        var panel = Frame(Data(Values, groups), Everything).StatisticsPanel!;
        var rowHeight = SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light);
        Assert.Equal((0, 1, 1), SkiaGraphRenderer.FitStatisticsBlocks(panel, Height(1 + 4, rowHeight), rowHeight));

        var single = new GraphStatisticsPanel(panel.Title, panel.GroupHeader, [panel.Rows[0]], panel.Items);
        Assert.Equal((0, 1, 0), SkiaGraphRenderer.FitStatisticsBlocks(single, Height(1 + 3, rowHeight), rowHeight));
        Assert.Equal((1, 0, 0), SkiaGraphRenderer.FitStatisticsBlocks(single, Height(1 + 9, rowHeight), rowHeight));
    }

    [Fact]
    public void ASeriesShownInPartIsDrawnAndTheGraphOtherwiseUnchanged()
    {
        var data = Data(Values, Lots(Values.Length));
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;
        var everything = Frame(data, Everything);

        // 760 x 330: the statistics panel has room for a few lines only - the first lot in part, "… 2 more".
        var layout = SkiaGraphRenderer.Layout(everything, new SKRect(0, 0, 760, 330), GraphThemes.Light);
        var fit = SkiaGraphRenderer.FitStatisticsBlocks(everything.StatisticsPanel!, layout.StatisticsPanelArea.Height, SkiaGraphRenderer.StatisticsRowHeight(GraphThemes.Light));
        Assert.Equal(0, fit.Blocks);
        Assert.InRange(fit.Partial, 1, 7);
        Assert.Equal(2, fit.More);

        using var first = new SKBitmap(760, 330);
        using var second = new SKBitmap(760, 330);
        foreach (var bitmap in new[] { first, second })
        {
            using var canvas = new SKCanvas(bitmap);
            new SkiaGraphRenderer().Render(canvas, everything, new SKRect(0, 0, 760, 330), GraphThemes.Light, new HistogramRenderer(model));
        }

        Assert.Equal(first.Bytes, second.Bytes);
    }

    // The height of a panel area that holds this many lines - the title included - at the legend's padding and spacing.
    private static float Height(int lines, float rowHeight) => (lines * (rowHeight + 5f)) + 11f;

    [Fact]
    public void TheSameGraphDrawsTheSameAndOnlyTheRoomDecidesBetweenTableAndBlocks()
    {
        var data = Data(Values, Lots(Values.Length));
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;
        var everything = Frame(data, Everything);
        var plot = new HistogramRenderer(model);

        var png = new GraphExportService().RenderPng(new GraphExportSnapshot(everything, plot, GraphThemes.Light));
        Assert.Equal(png, new GraphExportService().RenderPng(new GraphExportSnapshot(everything, plot, GraphThemes.Light)));
        Assert.NotEqual(png, new GraphExportService().RenderPng(new GraphExportSnapshot(Frame(data), plot, GraphThemes.Light)));

        // The model is the same whichever way it is drawn: every row, every statistic chosen.
        Assert.Equal(GraphStatisticsOptions.AllItems, everything.StatisticsPanel!.Items);
        Assert.Equal(3, everything.StatisticsPanel.Rows.Count);
    }
}
