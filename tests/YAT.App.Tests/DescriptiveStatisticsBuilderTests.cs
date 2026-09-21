using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Worksheet rows to a descriptive statistics table: which rows are summarised together, what is counted as missing,
// how groups are ordered and named, and how the numbers are written.
public class DescriptiveStatisticsBuilderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly DescriptiveStatisticsBuilder _builder = new();

    private static AnalysisColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    private static AnalysisVariableData Variable(string name, params double?[] values) => new(Column(name), values);

    private static AnalysisData Ungrouped(params AnalysisVariableData[] variables) =>
        new(Guid.NewGuid(), variables[0].Values.Length, variables, null);

    private static AnalysisData GroupedByText(string?[] groups, params AnalysisVariableData[] variables) =>
        new(Guid.NewGuid(), groups.Length, variables, new StringAnalysisGroupData(Column("SITE", WorksheetDataType.String), groups));

    private static AnalysisData GroupedByNumber(double?[] groups, params AnalysisVariableData[] variables) =>
        new(Guid.NewGuid(), groups.Length, variables, new NumericAnalysisGroupData(Column("SITE"), groups));

    private static string[] ColumnNames(AnalysisResultTable table) => [.. table.Columns.Select(column => column.Name)];

    private static string Cell(AnalysisResultTable table, int row, string columnName)
    {
        var column = table.Columns.Select((candidate, index) => (candidate.Name, index)).Single(candidate => candidate.Name == columnName);
        return table.Rows[row].Cells[column.index];
    }

    private static string[] Column(AnalysisResultTable table, string columnName) =>
        [.. Enumerable.Range(0, table.Rows.Count).Select(row => Cell(table, row, columnName))];

    // 0
    [Fact]
    public void AnUngroupedAnalysisHasOneRowPerVariableAndNoGroupColumn()
    {
        var data = Ungrouped(Variable("Reg1", 1, 2, 3, 4), Variable("Reg2", 10, 20, 30, 40));

        var table = _builder.Build(data, Token);

        Assert.Equal(["Variable", "N", "Missing", "Mean", "StDev", "Min", "Q1", "Median", "Q3", "Max"], ColumnNames(table));
        Assert.Equal(["Reg1", "Reg2"], Column(table, "Variable"));
        Assert.Equal("Descriptive Statistics: Reg1, Reg2", table.Title);
    }

    // 1
    [Fact]
    public void TheStatisticsAreTheOnesYatAlreadyDefines()
    {
        var table = _builder.Build(Ungrouped(Variable("Reg1", 1, 2, 3, 4)), Token);

        Assert.Equal("4", Cell(table, 0, "N"));
        Assert.Equal("0", Cell(table, 0, "Missing"));
        Assert.Equal("2.5", Cell(table, 0, "Mean"));

        // Sample standard deviation: sqrt(5 / 3), not the population one (sqrt(1.25)).
        Assert.Equal("1.2909944", Cell(table, 0, "StDev"));
        Assert.Equal("1", Cell(table, 0, "Min"));

        // R-7 quartiles, as everywhere else in YAT.
        Assert.Equal("1.75", Cell(table, 0, "Q1"));
        Assert.Equal("2.5", Cell(table, 0, "Median"));
        Assert.Equal("3.25", Cell(table, 0, "Q3"));
        Assert.Equal("4", Cell(table, 0, "Max"));
    }

    // 2
    [Fact]
    public void EmptyRowsAreCountedAsMissingAndLeftOutOfTheStatistics()
    {
        var table = _builder.Build(Ungrouped(Variable("Reg1", 1, null, 3, null, null)), Token);

        Assert.Equal("2", Cell(table, 0, "N"));
        Assert.Equal("3", Cell(table, 0, "Missing"));
        Assert.Equal("2", Cell(table, 0, "Mean"));
        Assert.Equal("1", Cell(table, 0, "Min"));
        Assert.Equal("3", Cell(table, 0, "Max"));
    }

    // 3
    [Fact]
    public void AVariableWithoutAnyValueIsSafeAndShowsBlankStatistics()
    {
        var table = _builder.Build(Ungrouped(Variable("Reg1", null, null, null)), Token);

        Assert.Equal("0", Cell(table, 0, "N"));
        Assert.Equal("3", Cell(table, 0, "Missing"));
        foreach (var statistic in (string[])["Mean", "StDev", "Min", "Q1", "Median", "Q3", "Max"])
        {
            Assert.Equal(string.Empty, Cell(table, 0, statistic));
        }
    }

    // 4
    [Fact]
    public void ASingleObservationHasNoSampleStandardDeviation()
    {
        var table = _builder.Build(Ungrouped(Variable("Reg1", 7, null)), Token);

        Assert.Equal("1", Cell(table, 0, "N"));
        Assert.Equal("1", Cell(table, 0, "Missing"));
        Assert.Equal("7", Cell(table, 0, "Mean"));
        Assert.Equal("7", Cell(table, 0, "Median"));
        Assert.Equal(string.Empty, Cell(table, 0, "StDev"));
    }

    // 5
    [Fact]
    public void GroupsAreSummarisedIndependentlyAndRowsStayWithTheirGroup()
    {
        // Value[i] and Group[i] are the same worksheet row: A holds 1 and 3, B holds 2.
        var data = GroupedByText(["A", "B", "A"], Variable("Reg1", 1, 2, 3));

        var table = _builder.Build(data, Token);

        Assert.Equal(["Variable", "Group", "N", "Missing", "Mean", "StDev", "Min", "Q1", "Median", "Q3", "Max"], ColumnNames(table));
        Assert.Equal(["A", "B"], Column(table, "Group"));
        Assert.Equal(["2", "1"], Column(table, "N"));
        Assert.Equal(["2", "2"], Column(table, "Mean"));
        Assert.Equal("3", Cell(table, 0, "Max"));
    }

    // 6
    [Fact]
    public void GroupsKeepTheOrderInWhichTheWorksheetFirstShowsThem()
    {
        var data = GroupedByText(["C", "A", "C", "B", "A"], Variable("Reg1", 1, 2, 3, 4, 5));

        var table = _builder.Build(data, Token);

        // First observed, not sorted: C, A, B.
        Assert.Equal(["C", "A", "B"], Column(table, "Group"));
    }

    // 7
    [Fact]
    public void EveryVariableListsTheGroupsInTheSameOrder()
    {
        var data = GroupedByText(["B", "A"], Variable("Reg1", 1, 2), Variable("Reg2", 10, 20));

        var table = _builder.Build(data, Token);

        Assert.Equal(["Reg1", "Reg1", "Reg2", "Reg2"], Column(table, "Variable"));
        Assert.Equal(["B", "A", "B", "A"], Column(table, "Group"));
        Assert.Equal(["1", "2", "10", "20"], Column(table, "Mean"));
    }

    // 8
    [Fact]
    public void RowsWithoutAGroupValueAreKeptAsTheirOwnGroup()
    {
        var data = GroupedByText(["A", null, "B", null], Variable("Reg1", 1, 2, 3, 4));

        var table = _builder.Build(data, Token);

        // "(Missing)" takes its place in the order like any other group, and its observations are summarised.
        Assert.Equal(["A", DescriptiveStatisticsBuilder.MissingGroupLabel, "B"], Column(table, "Group"));
        Assert.Equal("(Missing)", DescriptiveStatisticsBuilder.MissingGroupLabel);
        Assert.Equal("2", Cell(table, 1, "N"));
        Assert.Equal("3", Cell(table, 1, "Mean"));
    }

    // 9
    [Fact]
    public void AnObservedGroupStaysInTheTableEvenWhenAVariableHasNoValueInIt()
    {
        // SITE 3 appears in worksheet rows; Reg1 is empty in all of them.
        var data = GroupedByNumber([1, 3, 3, 1], Variable("Reg1", 5, null, null, 7));

        var table = _builder.Build(data, Token);

        Assert.Equal(["1", "3"], Column(table, "Group"));
        Assert.Equal(["2", "0"], Column(table, "N"));
        Assert.Equal(["0", "2"], Column(table, "Missing"));
        Assert.Equal(string.Empty, Cell(table, 1, "Mean"));
        Assert.Equal("6", Cell(table, 0, "Mean"));
    }

    // 10
    [Fact]
    public void EachGroupsMissingCountIsItsOwnRowsMinusItsObservations()
    {
        var data = GroupedByText(["A", "A", "A", "B"], Variable("Reg1", 1, null, 3, null));

        var table = _builder.Build(data, Token);

        // A holds three worksheet rows and two values; B holds one row and none. N + Missing is the group's own rows.
        Assert.Equal(["2", "0"], Column(table, "N"));
        Assert.Equal(["1", "1"], Column(table, "Missing"));
    }

    // 11
    [Fact]
    public void NumericGroupValuesAreLabelledTheWayTheGraphsLabelThem()
    {
        var data = GroupedByNumber([1, 2.5, 1234.56789], Variable("Reg1", 1, 2, 3));

        var table = _builder.Build(data, Token);

        // The graph grouping rule: 0.#### in the invariant culture.
        Assert.Equal(["1", "2.5", "1234.5679"], Column(table, "Group"));
    }

    // 12
    [Fact]
    public void StatisticsAreWrittenWithEightSignificantDigits()
    {
        var table = _builder.Build(
            Ungrouped(
                Variable("Small", 0.00000157, 0.00000157, 0.00000157),
                Variable("Large", 14983.247, 14983.247, 14983.247),
                Variable("Repeating", 1, 1, 2)),
            Token);

        Assert.Equal("1.57E-06", Cell(table, 0, "Mean"));
        Assert.Equal("14983.247", Cell(table, 1, "Mean"));
        Assert.Equal("1.3333333", Cell(table, 2, "Mean"));
    }

    // 13
    [Fact]
    public void AWorksheetWithoutRowsProducesNoStatisticsRatherThanAFailure()
    {
        var ungrouped = _builder.Build(Ungrouped(Variable("Reg1")), Token);
        var grouped = _builder.Build(GroupedByText([], Variable("Reg1")), Token);

        Assert.Equal("0", Cell(ungrouped, 0, "N"));
        Assert.Equal("0", Cell(ungrouped, 0, "Missing"));
        Assert.Equal(string.Empty, Cell(ungrouped, 0, "Mean"));

        // Grouped: no worksheet row means no observed group, so there is nothing to report.
        Assert.Empty(grouped.Rows);
    }

    // 14
    [Fact]
    public void BuildingIsCancellable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => _builder.Build(Ungrouped(Variable("Reg1", 1, 2)), cancellation.Token));
    }
}
