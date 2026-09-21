using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Worksheet rows to a capability table: test order is worksheet row order, a gap breaks the moving range that order
// is made of, groups are independent sequences, and what the table shows never changes what is calculated.
public class CapabilityAnalysisBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly CapabilityAnalysisBuilder _builder = new();

    private static AnalysisColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static AnalysisVariableData Variable(string name, params double?[] values) => new(Column(name), values);

    private static AnalysisData Ungrouped(params AnalysisVariableData[] variables) =>
        new(Guid.NewGuid(), variables[0].Values.Length, variables, null);

    private static AnalysisData GroupedByText(string?[] groups, params AnalysisVariableData[] variables) =>
        new(Guid.NewGuid(), groups.Length, variables, new StringAnalysisGroupData(Column("SITE", WorksheetDataType.String), groups));

    private static AnalysisData GroupedByNumber(double?[] groups, params AnalysisVariableData[] variables) =>
        new(Guid.NewGuid(), groups.Length, variables, new NumericAnalysisGroupData(Column("SITE"), groups));

    // A configuration that gives every variable of the data the same specification and shows the given statistics
    // (the defaults when none are named).
    private static CapabilityAnalysisConfiguration Configuration(
        AnalysisData data,
        double? lower = null,
        double? upper = null,
        params CapabilityStatistic[] statistics) =>
        new(
            data.WorksheetId,
            [.. data.Variables.Select(variable => new CapabilityVariable(variable.Column.ColumnId, lower, upper))],
            data.Group?.Column.ColumnId,
            statistics.Length == 0 ? CapabilityAnalysisConfiguration.DefaultDisplayStatistics : statistics);

    private static string[] ColumnNames(AnalysisResultTable table) => [.. table.Columns.Select(column => column.Name)];

    private static string Cell(AnalysisResultTable table, int row, string columnName)
    {
        var column = table.Columns.Select((candidate, index) => (candidate.Name, index)).Single(candidate => candidate.Name == columnName);
        return table.Rows[row].Cells[column.index];
    }

    private static string[] Column(AnalysisResultTable table, string columnName) =>
        [.. Enumerable.Range(0, table.Rows.Count).Select(row => Cell(table, row, columnName))];

    // ---- Moving range in worksheet order ----

    // 0
    [Fact]
    public void TheWithinSpreadComesFromTheMovingRangesOfConsecutiveRows()
    {
        // Ranges 1, 3, 2 -> MRbar 2 -> 2 / 1.128. Mean is the mean of all four values.
        var data = Ungrouped(Variable("Reg1", 100, 101, 104, 102));

        var table = _builder.Build(data, Configuration(data), Token);

        Assert.Equal(["Variable", "N", "Mean", "Within StDev"], ColumnNames(table));
        Assert.Equal("4", Cell(table, 0, "N"));
        Assert.Equal("101.75", Cell(table, 0, "Mean"));
        Assert.Equal("1.7730496", Cell(table, 0, "Within StDev"));
        Assert.Equal("Capability Analysis: Reg1", table.Title);
    }

    // 1
    [Fact]
    public void TheRowsAreNeverSortedBeforeTheMovingRangeIsTaken()
    {
        // The same four values in measured order and in sorted order give different within spreads; the builder must
        // report the measured one.
        var measured = Ungrouped(Variable("Reg1", 100, 104, 101, 102));
        var sorted = Ungrouped(Variable("Reg1", 100, 101, 102, 104));

        var measuredTable = _builder.Build(measured, Configuration(measured), Token);
        var sortedTable = _builder.Build(sorted, Configuration(sorted), Token);

        // Measured: ranges 4, 3, 1 -> MRbar 8/3. Sorted: ranges 1, 1, 2 -> MRbar 4/3.
        Assert.Equal("2.3640662", Cell(measuredTable, 0, "Within StDev"));
        Assert.Equal("1.1820331", Cell(sortedTable, 0, "Within StDev"));

        // The mean does not depend on the order, which is exactly why the spread has to.
        Assert.Equal(Cell(measuredTable, 0, "Mean"), Cell(sortedTable, 0, "Mean"));
    }

    // 2
    [Fact]
    public void AMissingRowBreaksTheAdjacencyOnBothSides()
    {
        // 100, 101, null, 103, 102: only |101-100| and |102-103| are consecutive measurements -> MRbar 1.
        var data = Ungrouped(Variable("Reg1", 100, 101, null, 103, 102));

        var table = _builder.Build(data, Configuration(data), Token);

        Assert.Equal("4", Cell(table, 0, "N"));
        Assert.Equal("101.5", Cell(table, 0, "Mean"));
        Assert.Equal("0.88652482", Cell(table, 0, "Within StDev"));
    }

    // 3
    [Fact]
    public void ValuesEitherSideOfAGapNeverFormAMovingRangeEvenWithSeveralObservations()
    {
        // 100, null, 101: two observations, no consecutive pair, so there is no within spread and no capability.
        var data = Ungrouped(Variable("Reg1", 100, null, 101));

        var table = _builder.Build(data, Configuration(data, 90, 110, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        Assert.Equal("2", Cell(table, 0, "N"));
        Assert.Equal("1", Cell(table, 0, "Missing"));
        Assert.Equal("100.5", Cell(table, 0, "Mean"));
        Assert.Equal(string.Empty, Cell(table, 0, "Within StDev"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cp"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cpk"));
    }

    // 4
    [Fact]
    public void TheMeanUsesEveryValidObservationNotOnlyTheOnesInAMovingRange()
    {
        // 103 and 102 are in no moving range with 100 and 101, but they are still measurements of this variable.
        var data = Ungrouped(Variable("Reg1", 100, 101, null, 103, 102));

        var table = _builder.Build(data, Configuration(data), Token);

        Assert.Equal("101.5", Cell(table, 0, "Mean"));
    }

    // ---- Counts and edge cases ----

    // 5
    [Fact]
    public void ASingleObservationHasAMeanButNoSpreadAndNoIndices()
    {
        var data = Ungrouped(Variable("Reg1", 15000, null, null));

        var table = _builder.Build(data, Configuration(data, 14500, 15500, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        Assert.Equal("1", Cell(table, 0, "N"));
        Assert.Equal("2", Cell(table, 0, "Missing"));
        Assert.Equal("15000", Cell(table, 0, "Mean"));
        Assert.Equal(string.Empty, Cell(table, 0, "Within StDev"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cp"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cpu"));
    }

    // 6
    [Fact]
    public void AVariableWithoutAnyValueIsSafeAndShowsBlanks()
    {
        var data = Ungrouped(Variable("Reg1", null, null, null));

        var table = _builder.Build(data, Configuration(data, 1, 2, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        Assert.Equal("0", Cell(table, 0, "N"));
        Assert.Equal("3", Cell(table, 0, "Missing"));
        Assert.Equal(string.Empty, Cell(table, 0, "Mean"));
        Assert.Equal(string.Empty, Cell(table, 0, "Within StDev"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cpk"));

        // The specification it was measured against is still worth showing.
        Assert.Equal("1", Cell(table, 0, "LSL"));
        Assert.Equal("2", Cell(table, 0, "USL"));
    }

    // 7
    [Fact]
    public void AProcessThatNeverMovedReportsAZeroSpreadAndNoIndicesRatherThanInfinity()
    {
        var data = Ungrouped(Variable("Reg1", 15000, 15000, 15000));

        var table = _builder.Build(data, Configuration(data, 14500, 15500, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        Assert.Equal("0", Cell(table, 0, "Within StDev"));
        foreach (var index in (string[])["Cp", "Cpl", "Cpu", "Cpk"])
        {
            Assert.Equal(string.Empty, Cell(table, 0, index));
        }

        Assert.DoesNotContain(table.Rows[0].Cells, cell => cell.Contains('∞') || cell.Contains("Inf") || cell.Contains("NaN"));
    }

    // ---- Specifications ----

    // 8
    [Fact]
    public void ATwoSidedSpecificationReportsAllFourIndices()
    {
        // Mean 15000, MRbar 100 -> sigma = 100 / 1.128 = 88.65..., spec 14500..15500.
        var data = Ungrouped(Variable("Reg1", 14950, 15050, 14950, 15050));

        var table = _builder.Build(data, Configuration(data, 14500, 15500, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        var sigma = 100 / 1.128;
        Assert.Equal("15000", Cell(table, 0, "Mean"));
        Assert.Equal(Format(1000 / (6 * sigma)), Cell(table, 0, "Cp"));
        Assert.Equal(Format(500 / (3 * sigma)), Cell(table, 0, "Cpl"));
        Assert.Equal(Format(500 / (3 * sigma)), Cell(table, 0, "Cpu"));
        Assert.Equal(Format(500 / (3 * sigma)), Cell(table, 0, "Cpk"));
        Assert.Equal("14500", Cell(table, 0, "LSL"));
        Assert.Equal("15500", Cell(table, 0, "USL"));
    }

    // 9
    [Fact]
    public void AnUpperOnlySpecificationHasNoCpAndNoCpl()
    {
        var data = Ungrouped(Variable("Reg1", 450, 490, 450, 490));

        var table = _builder.Build(data, Configuration(data, null, 500, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        var sigma = 40 / 1.128;
        Assert.Equal(string.Empty, Cell(table, 0, "Cp"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cpl"));
        Assert.Equal(string.Empty, Cell(table, 0, "LSL"));
        Assert.Equal(Format(30 / (3 * sigma)), Cell(table, 0, "Cpu"));
        Assert.Equal(Cell(table, 0, "Cpu"), Cell(table, 0, "Cpk"));
    }

    // 10
    [Fact]
    public void ALowerOnlySpecificationHasNoCpAndNoCpu()
    {
        var data = Ungrouped(Variable("Reg1", 22, 30, 22, 30));

        var table = _builder.Build(data, Configuration(data, 20, null, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        var sigma = 8 / 1.128;
        Assert.Equal(string.Empty, Cell(table, 0, "Cp"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cpu"));
        Assert.Equal(string.Empty, Cell(table, 0, "USL"));
        Assert.Equal(Format(6 / (3 * sigma)), Cell(table, 0, "Cpl"));
        Assert.Equal(Cell(table, 0, "Cpl"), Cell(table, 0, "Cpk"));
    }

    // 11
    [Fact]
    public void AProcessOutsideItsSpecificationReportsANegativeCpk()
    {
        // Mean 15600 is above the upper limit of 15500.
        var data = Ungrouped(Variable("Reg1", 15550, 15650, 15550, 15650));

        var table = _builder.Build(data, Configuration(data, 14500, 15500, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        var sigma = 100 / 1.128;
        Assert.Equal(Format(-100 / (3 * sigma)), Cell(table, 0, "Cpk"));
        Assert.StartsWith("-", Cell(table, 0, "Cpk"));
    }

    // 12
    [Fact]
    public void EveryVariableIsMeasuredAgainstItsOwnSpecification()
    {
        var data = Ungrouped(Variable("Reg1", 14950, 15050), Variable("Reg2", 0.145, 0.155), Variable("Reg3", 450, 490));

        var configuration = new CapabilityAnalysisConfiguration(
            data.WorksheetId,
            [
                new CapabilityVariable(data.Variables[0].Column.ColumnId, 14500, 15500),
                new CapabilityVariable(data.Variables[1].Column.ColumnId, 0.1, 0.2),
                new CapabilityVariable(data.Variables[2].Column.ColumnId, null, 500)
            ],
            null,
            CapabilityAnalysisConfiguration.SelectableStatistics);

        var table = _builder.Build(data, configuration, Token);

        Assert.Equal(["Reg1", "Reg2", "Reg3"], Column(table, "Variable"));
        Assert.Equal(["14500", "0.1", string.Empty], Column(table, "LSL"));
        Assert.Equal(["15500", "0.2", "500"], Column(table, "USL"));
        Assert.Equal(string.Empty, Cell(table, 2, "Cp"));
        Assert.NotEqual(string.Empty, Cell(table, 0, "Cp"));
    }

    // ---- Grouping ----

    // 13
    [Fact]
    public void RowsOfAnotherGroupDoNotInterruptThisGroupsSequence()
    {
        // SITE1 100, SITE2 500, SITE1 101: the two SITE1 measurements are consecutive SITE1 measurements.
        var data = GroupedByText(["SITE1", "SITE2", "SITE1"], Variable("Reg1", 100, 500, 101));

        var table = _builder.Build(data, Configuration(data), Token);

        Assert.Equal(["SITE1", "SITE2"], Column(table, "Group"));
        Assert.Equal(Format(1 / 1.128), Cell(table, 0, "Within StDev"));
        Assert.Equal("2", Cell(table, 0, "N"));

        // SITE2 has a single measurement, so it has no moving range of its own.
        Assert.Equal(string.Empty, Cell(table, 1, "Within StDev"));
    }

    // 14
    [Fact]
    public void AMissingValueInsideTheSameGroupDoesBreakThatGroupsSequence()
    {
        // SITE1 100, SITE1 null, SITE2 500, SITE1 101: SITE1's own sequence has a gap in it.
        var data = GroupedByText(["SITE1", "SITE1", "SITE2", "SITE1"], Variable("Reg1", 100, null, 500, 101));

        var table = _builder.Build(data, Configuration(data, statistics: [CapabilityStatistic.Count, CapabilityStatistic.Missing, CapabilityStatistic.Mean, CapabilityStatistic.WithinStandardDeviation]), Token);

        Assert.Equal(["SITE1", "SITE2"], Column(table, "Group"));
        Assert.Equal("2", Cell(table, 0, "N"));
        Assert.Equal("1", Cell(table, 0, "Missing"));
        Assert.Equal("100.5", Cell(table, 0, "Mean"));

        // |101 - 100| must not be taken: the two measurements were not made one after the other.
        Assert.Equal(string.Empty, Cell(table, 0, "Within StDev"));
    }

    // 15
    [Fact]
    public void EachGroupIsItsOwnAnalysisWithItsOwnMeanSpreadAndIndices()
    {
        var data = GroupedByNumber(
            [1, 2, 1, 2],
            Variable("Reg1", 14950, 15450, 15050, 15550));

        var table = _builder.Build(data, Configuration(data, 14500, 15500, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);

        // SITE 1: 14950, 15050 -> mean 15000, MRbar 100. SITE 2: 15450, 15550 -> mean 15500, MRbar 100.
        var sigma = 100 / 1.128;
        Assert.Equal(["1", "2"], Column(table, "Group"));
        Assert.Equal(["15000", "15500"], Column(table, "Mean"));
        Assert.Equal([Format(sigma), Format(sigma)], Column(table, "Within StDev"));
        Assert.Equal(Format(500 / (3 * sigma)), Cell(table, 0, "Cpk"));

        // The second group sits exactly on its upper limit.
        Assert.Equal(Format(0), Cell(table, 1, "Cpk"));
    }

    // 16
    [Fact]
    public void GroupsKeepTheOrderInWhichTheWorksheetFirstShowsThemAndMissingGroupsAreKept()
    {
        var data = GroupedByText(["C", null, "A", "C"], Variable("Reg1", 1, 2, 3, 4));

        var table = _builder.Build(data, Configuration(data, 0, 10), Token);

        Assert.Equal(["C", AnalysisGroups.MissingGroupLabel, "A"], Column(table, "Group"));
        Assert.Equal("(Missing)", AnalysisGroups.MissingGroupLabel);
        Assert.Equal(["2", "1", "1"], Column(table, "N"));
    }

    // 17
    [Fact]
    public void AnObservedGroupStaysInTheTableEvenWhenAVariableHasNoValueInIt()
    {
        var data = GroupedByNumber([1, 3, 3, 1], Variable("Reg1", 5, null, null, 7));

        var table = _builder.Build(data, Configuration(data, 0, 10, CapabilityStatistic.Count, CapabilityStatistic.Missing, CapabilityStatistic.Mean, CapabilityStatistic.Cpk), Token);

        Assert.Equal(["1", "3"], Column(table, "Group"));
        Assert.Equal(["2", "0"], Column(table, "N"));
        Assert.Equal(["0", "2"], Column(table, "Missing"));
        Assert.Equal(string.Empty, Cell(table, 1, "Mean"));
        Assert.Equal(string.Empty, Cell(table, 1, "Cpk"));
    }

    // 18
    [Fact]
    public void EveryVariableListsTheGroupsInTheSameOrder()
    {
        var data = GroupedByText(["B", "A"], Variable("Reg1", 1, 2), Variable("Reg2", 10, 20));

        var table = _builder.Build(data, Configuration(data, 0, 100), Token);

        Assert.Equal(["Reg1", "Reg1", "Reg2", "Reg2"], Column(table, "Variable"));
        Assert.Equal(["B", "A", "B", "A"], Column(table, "Group"));
    }

    // ---- Display options ----

    // 19
    [Fact]
    public void TheDefaultTableShowsTheDataAndNotTheIndices()
    {
        var ungrouped = Ungrouped(Variable("Reg1", 1, 2));
        var grouped = GroupedByText(["A", "A"], Variable("Reg1", 1, 2));

        Assert.Equal(
            ["Variable", "N", "Mean", "Within StDev"],
            ColumnNames(_builder.Build(ungrouped, Configuration(ungrouped, 0, 10), Token)));

        Assert.Equal(
            ["Variable", "Group", "N", "Mean", "Within StDev"],
            ColumnNames(_builder.Build(grouped, Configuration(grouped, 0, 10), Token)));
    }

    // 20
    [Fact]
    public void ChosenStatisticsBecomeColumnsInTheOrderAResultShowsThem()
    {
        var data = Ungrouped(Variable("Reg1", 1, 2));

        // Chosen in a deliberately jumbled order; the table shows them in its own.
        var table = _builder.Build(
            data,
            Configuration(data, 0, 10, CapabilityStatistic.Cpk, CapabilityStatistic.Missing, CapabilityStatistic.Cp, CapabilityStatistic.Count),
            Token);

        Assert.Equal(["Variable", "N", "Missing", "Cp", "Cpk"], ColumnNames(table));
    }

    // 21
    [Fact]
    public void WhatTheTableShowsNeverChangesWhatIsCalculated()
    {
        var data = Ungrouped(Variable("Reg1", 14950, 15050, 14950, 15050));

        var everything = _builder.Build(data, Configuration(data, 14500, 15500, CapabilityAnalysisConfiguration.SelectableStatistics.ToArray()), Token);
        var onlyCpu = _builder.Build(data, Configuration(data, 14500, 15500, CapabilityStatistic.Cpu), Token);

        Assert.Equal(["Variable", "Cpu"], ColumnNames(onlyCpu));
        Assert.Equal(Cell(everything, 0, "Cpu"), Cell(onlyCpu, 0, "Cpu"));
    }

    // 22
    [Fact]
    public void TheStructuralColumnsAreAlwaysThere()
    {
        var grouped = GroupedByText(["A"], Variable("Reg1", 1));

        var table = _builder.Build(grouped, Configuration(grouped, 0, 10, CapabilityStatistic.Cpk), Token);

        Assert.Equal(["Variable", "Group", "Cpk"], ColumnNames(table));
        Assert.Equal("Reg1", Cell(table, 0, "Variable"));
        Assert.Equal("A", Cell(table, 0, "Group"));
    }

    // 23
    [Fact]
    public void BuildingIsCancellable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var data = Ungrouped(Variable("Reg1", 1, 2));

        Assert.Throws<OperationCanceledException>(() => _builder.Build(data, Configuration(data, 0, 10), cancellation.Token));
    }

    // The G8 invariant form a capability table shows a statistic in.
    private static string Format(double value) => AnalysisNumberFormat.Statistic(value);
}
