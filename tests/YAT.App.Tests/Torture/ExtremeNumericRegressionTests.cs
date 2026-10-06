using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Torture;

// Task #063.1: the defects the #063 poison corpus found, named so they can never be lost in it. Finite values near the
// limits of double are drawn when their statistics and range can be represented, and refused with a message the user
// can act on when they cannot - never failed, traced and reported as "could not be drawn".
public sealed class ExtremeNumericRegressionTests
{
    private const string Max = "1.7976931348623157E+308";
    private const string MinusMax = "-1.7976931348623157E+308";

    private static TortureDataset Dataset(string name, params string[] values) =>
        new(name, [new TortureColumn("X", values), new TortureColumn("Y", [.. values.Select((_, row) => (row + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))])]);

    private static async Task<TortureOutcome> DrawAsync(TortureDataset dataset, TortureRequest request)
    {
        using var session = await TortureSession.StartAsync(dataset);
        var outcome = await session.DrawAsync(request);
        Assert.NotNull(outcome);
        Assert.Empty(outcome.Traces);
        return outcome;
    }

    // D1: a sum and squares that overflow - the statistics are those of the values, and the graph draws.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task D1StatisticsOfValuesWhoseSquaresOverflowAreFinite(GraphType type)
    {
        var dataset = Dataset("d1", "1e300", "-1e300", "5");
        var request = new TortureRequest(type, ["X"]) { Statistics = TortureStatistics.All };

        var outcome = await DrawAsync(dataset, request);

        Assert.Empty(outcome.Errors);
        var row = Assert.Single(Assert.Single(outcome.Graphs).State.Frame.StatisticsPanel!.Rows);
        Assert.Equal(5d / 3, row.Mean, 12);
        Assert.Equal(1e300, row.StandardDeviation!.Value, 1e288);
        Assert.Equal((-1e300, 5d, 1e300), (row.FiveNumbers!.Minimum, row.FiveNumbers.Median, row.FiveNumbers.Maximum));
        TortureInvariants.Verify($"D1 {request}\n  {dataset.Describe()}", outcome, TortureSizes.Normal);
    }

    // D1: a sample of MaxValue only - its mean is MaxValue, not a sum past it.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task D1TheMeanOfMaxValueIsMaxValue(GraphType type)
    {
        var dataset = Dataset("d1-max", Max, Max, Max);

        var outcome = await DrawAsync(dataset, new TortureRequest(type, ["X"]) { Statistics = TortureStatistics.All });

        Assert.Empty(outcome.Errors);
        var row = Assert.Single(Assert.Single(outcome.Graphs).State.Frame.StatisticsPanel!.Rows);
        Assert.Equal((double.MaxValue, 0d), (row.Mean, row.StandardDeviation!.Value));
    }

    // D1: a probability plot whose fitted line would reach beyond a double is refused, as a range too large to draw.
    [Fact]
    public async Task D1AFittedLineBeyondADoubleIsRefused()
    {
        var dataset = Dataset("d1-line", Max, "1.2E+308", "1.5E+308", "1E+308");

        var outcome = await DrawAsync(dataset, new TortureRequest(GraphType.ProbabilityPlot, ["X"]));

        Assert.Empty(outcome.Graphs);
        Assert.Equal([GraphDataRange.TooLargeMessage], outcome.Errors);
    }

    // D2: the box of MaxValue only - its quartiles and median are MaxValue (R-7), and it draws.
    [Fact]
    public async Task D2TheQuartilesOfMaxValueAreMaxValue()
    {
        var dataset = Dataset("d2", Max, Max);
        var request = new TortureRequest(GraphType.BoxPlot, ["X"]);

        var outcome = await DrawAsync(dataset, request);

        Assert.Empty(outcome.Errors);
        var graph = Assert.Single(outcome.Graphs);
        var box = Assert.Single(((BoxPlotRenderer)graph.Plot!).Model.Boxes);
        Assert.Equal(
            (double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue),
            (box.FirstQuartile, box.Median, box.ThirdQuartile, box.Mean));
        TortureInvariants.Verify($"D2 {request}\n  {dataset.Describe()}", outcome, TortureSizes.Normal);
    }

    // D3: a histogram of values spanning more than a double holds is refused with a message, not failed.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.BoxPlot)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task D3ARangeBeyondADoubleIsRefused(GraphType type)
    {
        var outcome = await DrawAsync(Dataset("d3", Max, MinusMax), new TortureRequest(type, ["X"]));

        Assert.Empty(outcome.Graphs);
        Assert.Equal([GraphDataRange.TooLargeMessage], outcome.Errors);
    }

    // D3: two variables drawn together share their value axis, so it is their range together that counts.
    [Fact]
    public async Task D3VariablesTogetherAreRefusedForTheirRangeTogether()
    {
        var dataset = new TortureDataset("d3-together", [new TortureColumn("A", [Max, "1E+308"]), new TortureColumn("B", [MinusMax, "-1E+308"])]);

        var together = await DrawAsync(dataset, new TortureRequest(GraphType.Histogram, ["A", "B"]));
        var separate = await DrawAsync(dataset, new TortureRequest(GraphType.Histogram, ["A", "B"]) { Layout = GraphVariableLayout.Separate });

        Assert.Equal([GraphDataRange.TooLargeMessage], together.Errors);
        Assert.Empty(separate.Errors);
        Assert.Equal(2, separate.Graphs.Count);
    }

    // D4: a scatter plot whose X or Y spans more than a double holds is refused with a message, not failed.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task D4AScatterAxisBeyondADoubleIsRefused(bool onX)
    {
        var dataset = new TortureDataset(
            "d4",
            [new TortureColumn("Wide", ["-1.6179238213760842E+308", "0", "1.6179238213760842E+308"]), new TortureColumn("Plain", ["1", "2", "3"])]);
        var request = onX
            ? new TortureRequest(GraphType.ScatterPlot, ["Wide"]) { Y = "Plain" }
            : new TortureRequest(GraphType.ScatterPlot, ["Plain"]) { Y = "Wide" };

        var outcome = await DrawAsync(dataset, request);

        Assert.Empty(outcome.Graphs);
        Assert.Equal([GraphDataRange.TooLargeMessage], outcome.Errors);
    }
}
