using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Observations to boxes: which category each box sits in, which colour it takes, what the axes cover, and what
// happens to a variable or group that has little or nothing to show.
public class BoxPlotRenderModelBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static UnivariateGraphData Variable(string name, double[] values, string?[]? groups = null) =>
        new(GraphType.BoxPlot,
            Guid.NewGuid(),
            Column(name),
            values,
            groups is null ? null : new StringGroupData(Column("SITE", WorksheetDataType.String), groups));

    private static UnivariateGraphData NumericGrouped(string name, double[] values, double?[] groups) =>
        new(GraphType.BoxPlot, Guid.NewGuid(), Column(name), values, new NumericGroupData(Column("SITE"), groups));

    private static MultiVariableGraphData Data(params UnivariateGraphData[] variables) =>
        new(GraphType.BoxPlot, Guid.NewGuid(), variables);

    private static BoxPlotLabels Labels(MultiVariableGraphData data, string? group = null) =>
        new([.. data.Variables.Select(variable => variable.Variable.Name)], group);

    private static BoxPlotRenderModel? Build(MultiVariableGraphData data, string? group = null, int maximumPoints = 100_000) =>
        new BoxPlotRenderModelBuilder(maximumPoints).Build(data, Labels(data, group), Token);

    // 0
    [Fact]
    public void OneVariableWithoutAGroupIsOneBoxInOneCategory()
    {
        var data = Data(Variable("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9]));

        var model = Build(data)!;

        Assert.Equal(["Reg1"], model.Categories);
        var box = Assert.Single(model.Boxes);
        Assert.Equal("Reg1", box.Label);
        Assert.Equal(0, box.CategoryIndex);
        Assert.Equal(9, box.ObservationCount);
        Assert.Equal(3, box.FirstQuartile);
        Assert.Equal(5, box.Median);
        Assert.Equal(7, box.ThirdQuartile);
        Assert.Equal(5, box.Mean);
        Assert.Equal(1, box.LowerWhisker);
        Assert.Equal(9, box.UpperWhisker);
        Assert.Empty(box.Outliers.ToArray());
        Assert.Equal("Boxplot of Reg1", model.Frame.Title);

        // The X axis names the boxes, so an ungrouped box plot needs no legend.
        Assert.Null(model.Frame.Legend);
    }

    // 1
    [Fact]
    public void SeveralVariablesAreOneCategoryEachInTheOrderTheyWereSelected()
    {
        var data = Data(Variable("Reg1", [1, 2, 3]), Variable("Reg2", [10, 20, 30]), Variable("Reg3", [100, 200, 300]));

        var model = Build(data)!;

        Assert.Equal(["Reg1", "Reg2", "Reg3"], model.Categories);
        Assert.Equal([0, 1, 2], model.Boxes.Select(box => box.CategoryIndex));
        Assert.Equal([2, 20, 200], model.Boxes.Select(box => box.Median));
        Assert.Equal("Boxplot of Reg1, Reg2, Reg3", model.Frame.Title);

        // Without groups the boxes take the sequential series colours.
        Assert.Equal([0, 1, 2], model.Boxes.Select(box => box.SeriesIndex));
    }

    // 2
    [Fact]
    public void AGroupedVariableIsOneBoxPerGroupInFirstObservedOrder()
    {
        var data = Data(Variable("Reg1", [5, 1, 6, 2], ["B", "A", "B", "A"]));

        var model = Build(data, "SITE")!;

        Assert.Equal(["Reg1 / B", "Reg1 / A"], model.Categories);
        Assert.Equal([5.5, 1.5], model.Boxes.Select(box => box.Median));
        Assert.Equal(["B", "A"], model.Frame.Legend!.Entries.Select(entry => entry.Label));
        Assert.Equal("SITE", model.Frame.Legend!.Title);
    }

    // 3
    [Fact]
    public void AGroupKeepsItsColourAcrossVariables()
    {
        var data = Data(
            Variable("Reg1", [1, 2, 3, 4], ["A", "B", "A", "B"]),
            Variable("Reg2", [10, 20, 30, 40], ["B", "A", "B", "A"]));

        var model = Build(data, "SITE")!;

        Assert.Equal(["Reg1 / A", "Reg1 / B", "Reg2 / B", "Reg2 / A"], model.Categories);

        // A is series 0 and B series 1 wherever they appear, so one colour means one site in the whole graph.
        Assert.Equal([0, 1, 1, 0], model.Boxes.Select(box => box.SeriesIndex));
        Assert.Equal(["A", "B"], model.Frame.Legend!.Entries.Select(entry => entry.Label));
        Assert.Equal([0, 1], model.Frame.Legend!.Entries.Select(entry => entry.SeriesIndex));
    }

    // 4
    [Fact]
    public void EveryBoxIsComputedFromItsOwnGroupsObservations()
    {
        var data = Data(Variable("Reg1", [1, 100, 2, 200, 3, 300], ["A", "B", "A", "B", "A", "B"]));

        var model = Build(data, "SITE")!;

        Assert.Equal([2, 200], model.Boxes.Select(box => box.Median));
        Assert.Equal([1, 100], model.Boxes.Select(box => box.LowerWhisker));
        Assert.Equal([3, 300], model.Boxes.Select(box => box.UpperWhisker));
        Assert.Equal([3, 3], model.Boxes.Select(box => box.ObservationCount));
    }

    // 5
    [Fact]
    public void ObservationsWithoutAGroupValueGetTheirOwnBox()
    {
        var data = Data(Variable("Reg1", [1, 2, 3], ["A", null, "A"]));

        var model = Build(data, "SITE")!;

        Assert.Equal(["Reg1 / A", $"Reg1 / {BoxPlotRenderModelBuilder.MissingGroupLabel}"], model.Categories);
        Assert.Equal("(Missing)", BoxPlotRenderModelBuilder.MissingGroupLabel);
        Assert.Equal([2, 1], model.Boxes.Select(box => box.ObservationCount));
    }

    // 6
    [Fact]
    public void NumericGroupValuesAreLabelledTheWayTheOtherGraphsLabelThem()
    {
        var data = Data(NumericGrouped("Reg1", [1, 2, 3], [1, 2.5, 1234.56789]));

        var model = Build(data, "SITE")!;

        Assert.Equal(["Reg1 / 1", "Reg1 / 2.5", "Reg1 / 1234.5679"], model.Categories);
    }

    // 7
    [Fact]
    public void AVariableWithoutObservationsKeepsItsCategoryAndDrawsNoBox()
    {
        var data = Data(Variable("Reg1", [1, 2, 3]), Variable("Empty", []));

        var model = Build(data)!;

        Assert.Equal(["Reg1", "Empty"], model.Categories);
        var box = Assert.Single(model.Boxes);
        Assert.Equal(0, box.CategoryIndex);
    }

    // 8
    [Fact]
    public void NothingToPlotProducesNoModelAtAll()
    {
        Assert.Null(Build(Data(Variable("Reg1", []))));
    }

    // 9
    [Fact]
    public void ASingleObservationIsADegenerateBoxRatherThanAFailure()
    {
        var data = Data(Variable("Reg1", [42]));

        var box = Assert.Single(Build(data)!.Boxes);

        Assert.Equal(42, box.LowerWhisker);
        Assert.Equal(42, box.FirstQuartile);
        Assert.Equal(42, box.Median);
        Assert.Equal(42, box.ThirdQuartile);
        Assert.Equal(42, box.UpperWhisker);
        Assert.Equal(42, box.Mean);
        Assert.Empty(box.Outliers.ToArray());
    }

    // 10
    [Fact]
    public void ConstantDataStillGetsAUsableAxis()
    {
        var data = Data(Variable("Reg1", [100, 100, 100, 100]));

        var model = Build(data)!;

        var box = Assert.Single(model.Boxes);
        Assert.Equal(100, box.Median);
        Assert.Equal(100, box.LowerWhisker);
        Assert.Equal(100, box.UpperWhisker);

        // The deterministic fallback of GraphAxisRanges: a window around the value, never an empty range.
        Assert.True(model.Frame.YAxis.Range.IsValid);
        Assert.True(model.Frame.YAxis.Range.Minimum < 100);
        Assert.True(model.Frame.YAxis.Range.Maximum > 100);
    }

    // 11
    [Fact]
    public void TheAxisCoversTheOutliersAndTheMeanAsWellAsTheBoxes()
    {
        var data = Data(Variable("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9, 1000]));

        var model = Build(data)!;

        var box = Assert.Single(model.Boxes);
        Assert.Equal([1000], box.Outliers.ToArray());
        Assert.True(model.Frame.YAxis.Range.Maximum >= 1000);
        Assert.True(model.Frame.YAxis.Range.Minimum <= 1);
        Assert.True(model.Frame.YAxis.Range.Maximum >= box.Mean);
    }

    // 12
    [Fact]
    public void EachCategoryOwnsItsOwnSlotOnTheHorizontalAxis()
    {
        var data = Data(Variable("Reg1", [1, 2]), Variable("Reg2", [3, 4]), Variable("Reg3", [5, 6]));

        var model = Build(data)!;

        Assert.Equal(0.5, model.Frame.XAxis.Range.Minimum);
        Assert.Equal(3.5, model.Frame.XAxis.Range.Maximum);
        Assert.Equal([1, 2, 3], model.Frame.XAxis.Ticks.Select(tick => tick.Value));
        Assert.Equal(["Reg1", "Reg2", "Reg3"], model.Frame.XAxis.Ticks.Select(tick => tick.Label));
        Assert.Equal(1, BoxPlotRenderModelBuilder.CategoryPosition(0));
        Assert.Equal(3, BoxPlotRenderModelBuilder.CategoryPosition(2));
    }

    // 13
    [Fact]
    public void TheBoxesAreComputedFromEveryObservationAndOnlyTheOutlierMarkersAreCapped()
    {
        // Twenty outliers over a budget of five: the statistics still see all of them.
        double[] values = [.. Enumerable.Range(0, 100).Select(index => (double)(index % 10)), .. Enumerable.Range(0, 20).Select(index => 1000d + index)];
        var data = Data(Variable("Reg1", values));

        var model = Build(data, maximumPoints: 5)!;

        var box = Assert.Single(model.Boxes);
        Assert.Equal(120, box.ObservationCount);
        Assert.Equal(20, box.OutlierCount);
        Assert.Equal(5, box.Outliers.Length);
        Assert.Equal(20, model.OutlierCount);
        Assert.Equal(5, model.RenderedOutlierCount);
        Assert.True(model.WasSampled);

        // The sample keeps the ends, so the axis and the shape of the tail do not change with the budget.
        Assert.Equal(1000, box.Outliers.Span[0]);
        Assert.Equal(1019, box.Outliers.Span[^1]);
        Assert.True(model.Frame.YAxis.Range.Maximum >= 1019);
    }

    // 14
    [Fact]
    public void AllOutliersAreDrawnWhenTheyFitInTheBudget()
    {
        var data = Data(Variable("Reg1", [1, 2, 3, 4, 5, 6, 7, 8, 9, 500, 600]));

        var model = Build(data)!;

        var box = Assert.Single(model.Boxes);
        Assert.Equal([500, 600], box.Outliers.ToArray());
        Assert.Equal(2, model.OutlierCount);
        Assert.False(model.WasSampled);
    }

    // 15
    [Fact]
    public void SeveralVariablesWithAGroupProduceOneBoxPerCombinationThatHasObservations()
    {
        var data = Data(
            Variable("Reg1", [1, 2, 3, 4], ["1", "2", "1", "2"]),
            Variable("Reg2", [10, 20], ["1", "1"]));

        var model = Build(data, "SITE")!;

        Assert.Equal(["Reg1 / 1", "Reg1 / 2", "Reg2 / 1"], model.Categories);
        Assert.Equal([2, 3, 15], model.Boxes.Select(box => box.Median));

        // Reg2 has nothing on site 2, so no box is invented for it.
        Assert.Equal(3, model.Boxes.Count);
    }

    // 16
    [Fact]
    public void TheModelCountsWhatItWasGivenAndWhatItDraws()
    {
        var data = Data(Variable("Reg1", [1, 2, 3]), Variable("Reg2", [4, 5]));

        var model = Build(data)!;

        Assert.Equal(5, model.SourceObservationCount);
        Assert.Equal(0, model.OutlierCount);
        Assert.Equal(0, model.RenderedOutlierCount);
    }

    // 17
    [Fact]
    public void BuildingIsCancellable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var data = Data(Variable("Reg1", [1, 2, 3]));

        Assert.Throws<OperationCanceledException>(
            () => new BoxPlotRenderModelBuilder().Build(data, Labels(data), cancellation.Token));
    }

    // 18
    [Fact]
    public void ALabelIsNeededForEveryVariable()
    {
        var data = Data(Variable("Reg1", [1, 2]), Variable("Reg2", [3, 4]));

        Assert.Throws<ArgumentException>(() => new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels(["Reg1"]), Token));
    }
}
