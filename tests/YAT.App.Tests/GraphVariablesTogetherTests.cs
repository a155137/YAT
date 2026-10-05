using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Several variables drawn together (Task #041): one variable of series - "Reg1", or "Reg1 / Lot A" with groups - in
// variable order and, within a variable, in the order the groups were first seen anywhere; the graph types' own
// builders and the statistics panel then draw them as they draw groups.
public class GraphVariablesTogetherTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.NewGuid(), name, type);

    private static readonly GraphColumnInfo Lot = Column("Lot", WorksheetDataType.String);

    private static UnivariateGraphData Variable(GraphType type, string name, double[] values, string?[]? groups = null) =>
        new(type, Guid.Empty, Column(name), values, groups is null ? null : new StringGroupData(Lot, groups));

    private static MultiVariableGraphData Several(GraphType type, params UnivariateGraphData[] variables) => new(type, Guid.Empty, variables);

    private static (string? Label, double Value)[] Rows(UnivariateGraphData data) =>
        [.. Enumerable.Range(0, data.Count).Select(row => (((StringGroupData)data.Group!).Values.Span[row], data.Values.Span[row]))];

    // ---- The series ----

    [Fact]
    public void UngroupedVariablesAreOneSeriesEachInSelectionOrder()
    {
        var combined = GraphVariablesTogether.Combine(Several(GraphType.Histogram,
            Variable(GraphType.Histogram, "Reg2", [3, 1]),
            Variable(GraphType.Histogram, "Reg1", [2])), Token);

        Assert.Equal([("Reg2", 3d), ("Reg2", 1d), ("Reg1", 2d)], Rows(combined));
        Assert.Equal("Reg2, Reg1", combined.Variable.Name);
        Assert.Equal("Variable", combined.Group!.Column.Name);
        Assert.Equal(GraphType.Histogram, combined.GraphType);
    }

    [Fact]
    public void GroupsFollowTheirVariableInTheOrderTheyWereFirstSeenAnywhere()
    {
        // Reg1 sees B before A; Reg2 sees C first, then A. Overall: B, A, C - in every variable.
        var combined = GraphVariablesTogether.Combine(Several(GraphType.EmpiricalCdf,
            Variable(GraphType.EmpiricalCdf, "Reg1", [1, 2, 3, 4], ["B", "A", "B", null]),
            Variable(GraphType.EmpiricalCdf, "Reg2", [5, 6, 7], ["C", "A", "A"])), Token);

        Assert.Equal(
            [
                ("Reg1 / B", 1d), ("Reg1 / B", 3d), ("Reg1 / A", 2d), ("Reg1 / (Missing)", 4d),
                ("Reg2 / A", 6d), ("Reg2 / A", 7d), ("Reg2 / C", 5d)
            ],
            Rows(combined));
        Assert.Equal("Variable / Lot", combined.Group!.Column.Name);
        Assert.Equal("Reg1, Reg2", combined.Variable.Name);
    }

    [Fact]
    public void NumericGroupsAreNamedAsTheGraphsNameThem()
    {
        var site = new NumericGroupData(Column("Site"), new double?[] { 2, 1.23456, 2, null });
        var reg1 = new UnivariateGraphData(GraphType.ProbabilityPlot, Guid.Empty, Column("Reg1"), new double[] { 1, 2, 3, 4 }, site);

        var combined = GraphVariablesTogether.Combine(Several(GraphType.ProbabilityPlot, reg1), Token);

        // Numeric groups from the smallest up, "(Missing)" last (Task #059).
        Assert.Equal([("Reg1 / 1.2346", 2d), ("Reg1 / 2", 1d), ("Reg1 / 2", 3d), ("Reg1 / (Missing)", 4d)], Rows(combined));
        Assert.Equal("Variable / Site", combined.Group!.Column.Name);
    }

    [Fact]
    public void AVariableWithoutObservationsAddsNoSeries()
    {
        var combined = GraphVariablesTogether.Combine(Several(GraphType.Histogram,
            Variable(GraphType.Histogram, "Reg1", [1, 2]),
            Variable(GraphType.Histogram, "Empty", []),
            Variable(GraphType.Histogram, "Reg3", [3])), Token);

        Assert.Equal([("Reg1", 1d), ("Reg1", 2d), ("Reg3", 3d)], Rows(combined));
        Assert.Equal("Reg1, Empty, Reg3", combined.Variable.Name);
    }

    [Fact]
    public void CombiningCanBeCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() => GraphVariablesTogether.Combine(
            Several(GraphType.Histogram, Variable(GraphType.Histogram, "Reg1", [1])), cancelled.Token));
    }

    // ---- Drawn by the graph types' own builders ----

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, 300).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)))];
    private static readonly double[] Reg2 = [.. Enumerable.Range(0, 200).Select(i => 15.2 + (0.2 * Math.Cos(i * 0.23)))];
    private static readonly string?[] Lots1 = [.. Enumerable.Range(0, 300).Select(i => i % 17 == 0 ? null : $"Lot {i % 3}")];
    private static readonly string?[] Lots2 = [.. Enumerable.Range(0, 200).Select(i => $"Lot {(i + 1) % 3}")];

    private static UnivariateGraphData Together(GraphType type, bool grouped) =>
        GraphVariablesTogether.Combine(Several(type,
            Variable(type, "Reg1", Reg1, grouped ? Lots1 : null),
            Variable(type, "Reg2", Reg2, grouped ? Lots2 : null)), Token);

    private static HistogramPlotLabels HistogramLabels(UnivariateGraphData data) =>
        new(data.Variable.Name, data.Group?.Column.Name) { AxisTitle = GraphVariablesTogether.AxisTitle };

    [Theory]
    [InlineData(HistogramYScale.Frequency)]
    [InlineData(HistogramYScale.Percent)]
    [InlineData(HistogramYScale.Density)]
    public void ATogetherHistogramCountsEverySeriesIntoOneSetOfBins(HistogramYScale scale)
    {
        var data = Together(GraphType.Histogram, grouped: true);
        var model = new HistogramRenderModelBuilder().Build(data, HistogramLabels(data), new HistogramOptions(scale), Token)!;

        Assert.Equal("Histogram of Reg1, Reg2", model.Frame.Title);
        Assert.Equal("Data", model.Frame.XAxis.Title);
        Assert.Equal("Variable / Lot", model.Frame.Legend!.Title);
        Assert.Equal(
            // Row 0 of Reg1 has no lot, then Lot 1, Lot 2, Lot 0: that order holds for Reg2 too.
            ["Reg1 / (Missing)", "Reg1 / Lot 1", "Reg1 / Lot 2", "Reg1 / Lot 0", "Reg2 / Lot 1", "Reg2 / Lot 2", "Reg2 / Lot 0"],
            model.Series.Select(series => series.Label));
        Assert.Equal(Enumerable.Range(0, 7), model.Series.Select(series => series.SeriesIndex));

        // One grid over every observation of both variables.
        Assert.Equal(Math.Min(Reg1.Min(), Reg2.Min()), model.Bins[0].LowerEdge, 12);
        Assert.Equal(Math.Max(Reg1.Max(), Reg2.Max()), model.Bins[^1].UpperEdge, 12);
        Assert.Equal(500, model.SourceObservationCount);
        Assert.Equal(500, model.Series.Sum(series => series.Counts.Sum()));

        // Each series is scaled on its own observations.
        foreach (var series in model.Series)
        {
            var n = series.Counts.Sum();
            var expected = scale switch
            {
                HistogramYScale.Percent => 100d,
                HistogramYScale.Density => 1d,
                _ => n
            };
            var total = scale == HistogramYScale.Density
                ? series.Heights.Select((height, bin) => height * (model.Bins[bin].UpperEdge - model.Bins[bin].LowerEdge)).Sum()
                : series.Heights.Sum();
            Assert.Equal(expected, total, 9);
        }
    }

    [Fact]
    public void ATogetherProbabilityPlotHasPointsAndAFittedLinePerSeries()
    {
        var data = Together(GraphType.ProbabilityPlot, grouped: false);
        var model = new ProbabilityPlotRenderModelBuilder().Build(
            data, new ProbabilityPlotLabels(data.Variable.Name, data.Group?.Column.Name) { AxisTitle = GraphVariablesTogether.AxisTitle }, Token)!;

        Assert.Equal("Normal Probability Plot of Reg1, Reg2", model.Frame.Title);
        Assert.Equal("Data", model.Frame.XAxis.Title);
        Assert.Equal(["Reg1", "Reg2"], model.Series.Select(series => series.Label));
        Assert.All(model.Series, series => Assert.NotNull(series.FittedLine));
        Assert.Equal(300, model.Series[0].ObservationCount);
        Assert.Equal(200, model.Series[1].ObservationCount);
        Assert.Equal(Reg1.Average(), model.Series[0].FittedLine!.Mean, 12);
        Assert.Equal(Reg2.Average(), model.Series[1].FittedLine!.Mean, 12);
        Assert.Equal("Variable", model.Frame.Legend!.Title);
    }

    [Fact]
    public void ATogetherEmpiricalCdfHasACurvePerSeries()
    {
        var data = Together(GraphType.EmpiricalCdf, grouped: true);
        var model = new EmpiricalCdfRenderModelBuilder().Build(
            data, new EmpiricalCdfLabels(data.Variable.Name, data.Group?.Column.Name) { AxisTitle = GraphVariablesTogether.AxisTitle }, Token)!;

        Assert.Equal("Empirical CDF of Reg1, Reg2", model.Frame.Title);
        Assert.Equal("Data", model.Frame.XAxis.Title);
        Assert.Equal(7, model.Series.Count);
        Assert.Equal(500, model.Series.Sum(series => series.ObservationCount));
        Assert.Equal(
            [.. Lots1.GroupBy(lot => lot).OrderBy(group => Array.IndexOf(Lots1, group.Key)).Select(group => group.Count())],
            model.Series.Take(4).Select(series => series.ObservationCount));
    }

    // More series than the palette has colours: every series keeps its own index, and the colours repeat as they do for
    // a single variable with that many groups.
    [Fact]
    public void MoreThanEightSeriesCycleThroughThePalette()
    {
        var variables = Enumerable.Range(1, 5)
            .Select(index => Variable(GraphType.Histogram, $"Reg{index}", [.. Reg1.Take(60).Select(value => value + index)], [.. Lots2.Take(60)]))
            .ToArray();
        var data = GraphVariablesTogether.Combine(Several(GraphType.Histogram, variables), Token);
        var model = new HistogramRenderModelBuilder().Build(data, HistogramLabels(data), Token)!;

        Assert.Equal(15, model.Series.Count);
        Assert.Equal(Enumerable.Range(0, 15), model.Frame.Legend!.Entries.Select(entry => entry.SeriesIndex));
        Assert.Equal(8, GraphThemes.Light.SeriesPalette.Count);
        Assert.Equal(GraphThemes.Light.SeriesColor(0), GraphThemes.Light.SeriesColor(8));
    }

    // The statistics panel lists the same series, in the same order and colours, as the graph.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void TheStatisticsPanelHasARowPerSeries(GraphType type)
    {
        var data = Together(type, grouped: true);
        var panel = GraphStatisticsPanelBuilder.Build(data, Token)!;
        var legend = new HistogramRenderModelBuilder().Build(data, HistogramLabels(data), Token)!.Frame.Legend!;

        Assert.Equal("Variable / Lot", panel.GroupHeader);
        Assert.Equal(legend.Entries.Select(entry => (entry.Label, (int?)entry.SeriesIndex)), panel.Rows.Select(row => (row.Label, row.SeriesIndex)));
    }

    // A series together holds exactly what the variable's own graph has for that group.
    [Fact]
    public void ATogetherSeriesIsTheVariablesOwnGroup()
    {
        var reg2 = Variable(GraphType.Histogram, "Reg2", Reg2, Lots2);
        var together = GraphStatisticsPanelBuilder.Build(Together(GraphType.Histogram, grouped: true), Token)!;
        var separate = GraphStatisticsPanelBuilder.Build(reg2, Token)!;

        foreach (var row in separate.Rows)
        {
            var match = Assert.Single(together.Rows, candidate => candidate.Label == $"Reg2 / {row.Label}");
            Assert.Equal(row.Count, match.Count);
            Assert.Equal(row.Mean, match.Mean);
            Assert.Equal(row.StandardDeviation, match.StandardDeviation);
        }
    }
}
