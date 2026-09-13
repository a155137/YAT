using YAT.Application.Exceptions;
using YAT.Application.Ingestion;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class WorksheetPastePlannerTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static WorksheetPastePlanner CreatePlanner() => new(new ColumnDataTypeDetector());

    private static ParsedTabularData Parse(string text) => new TabularTextParser().Parse(text);

    private static ParsedTabularData HeadersOnly(params string[] headers) => Parse(string.Join('\t', headers));

    private static List<WorksheetColumn> Columns(params string[] names) =>
        names.Select((name, index) => new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = WorksheetId,
            Index = index,
            Name = name,
            DataType = WorksheetDataType.Numeric
        }).ToList();

    private static string[] FinalHeaders(WorksheetPastePlan plan) => plan.Columns.Select(column => column.FinalHeader).ToArray();

    private static int[] TargetIndexes(WorksheetPastePlan plan) => plan.Columns.Select(column => column.TargetColumnIndex).ToArray();

    [Fact]
    public void PlansPasteIntoEmptyWorksheet()
    {
        var data = Parse("No\tBin\tSITE\tReg1\tReg2\n1\t1\t1\t5\t0.132\n2\t2\t2\t7\t0.157");

        var plan = CreatePlanner().Plan(WorksheetId, Columns(), 0, data);

        Assert.Equal(WorksheetId, plan.WorksheetId);
        Assert.Equal(0, plan.StartColumnIndex);
        Assert.Equal(5, plan.ColumnCount);
        Assert.Equal(2, plan.DataRowCount);
        Assert.Equal(
            [
                new PlannedPasteColumn(0, 0, "No", "No", WorksheetDataType.Numeric),
                new PlannedPasteColumn(1, 1, "Bin", "Bin", WorksheetDataType.Numeric),
                new PlannedPasteColumn(2, 2, "SITE", "SITE", WorksheetDataType.Numeric),
                new PlannedPasteColumn(3, 3, "Reg1", "Reg1", WorksheetDataType.Numeric),
                new PlannedPasteColumn(4, 4, "Reg2", "Reg2", WorksheetDataType.Numeric)
            ],
            plan.Columns);
    }

    [Fact]
    public void PlansPasteImmediatelyAfterLastExistingColumn()
    {
        var existing = Columns("No", "Bin", "SITE", "Reg1", "Reg2");
        var data = Parse("No\tBin\tSITE\tReg3\n1\t1\t1\t500\n2\t2\t2\t2350");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 5, data);

        Assert.Equal(
            [
                new PlannedPasteColumn(0, 5, "No", "No_1", WorksheetDataType.Numeric),
                new PlannedPasteColumn(1, 6, "Bin", "Bin_1", WorksheetDataType.Numeric),
                new PlannedPasteColumn(2, 7, "SITE", "SITE_1", WorksheetDataType.Numeric),
                new PlannedPasteColumn(3, 8, "Reg3", "Reg3", WorksheetDataType.Numeric)
            ],
            plan.Columns);
    }

    [Fact]
    public void PlansPasteStartingInsideExistingColumns()
    {
        var existing = Columns("A", "B", "C", "D", "E", "F");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 3, HeadersOnly("X", "Y", "Z"));

        Assert.Equal([3, 4, 5], TargetIndexes(plan));
        Assert.Equal(["X", "Y", "Z"], FinalHeaders(plan));
    }

    [Fact]
    public void PlansPasteExtendingBeyondCurrentWorksheetWidth()
    {
        var existing = Columns("A", "B", "C");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 2, HeadersOnly("X", "Y", "Z", "W"));

        Assert.Equal([2, 3, 4, 5], TargetIndexes(plan));
    }

    [Fact]
    public void PlansPasteStartingBeyondCurrentWorksheetWidth()
    {
        var existing = Columns("A", "B");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 7, HeadersOnly("X", "Y"));

        Assert.Equal(7, plan.StartColumnIndex);
        Assert.Equal([7, 8], TargetIndexes(plan));
    }

    [Fact]
    public void AssignsSequentialTargetIndexesFromStartColumn()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns(), 3, HeadersOnly("P", "Q", "R", "S"));

        Assert.Equal([0, 1, 2, 3], plan.Columns.Select(column => column.SourceColumnIndex));
        Assert.Equal([3, 4, 5, 6], TargetIndexes(plan));
    }

    [Fact]
    public void ReportsIncomingBlockWidth()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("A"), 1, Parse("P\tQ\tR\n1\t2\t3"));

        Assert.Equal(3, plan.ColumnCount);
        Assert.Equal(3, plan.Columns.Count);
    }

    [Theory]
    [InlineData("A\tB", 0)]
    [InlineData("A\tB\n1\t2", 1)]
    [InlineData("A\tB\r\n1\t2\r\n3\t4\r\n5\t6\r\n", 3)]
    public void ReportsIncomingDataRowCount(string text, int expectedRowCount)
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns(), 0, Parse(text));

        Assert.Equal(expectedRowCount, plan.DataRowCount);
    }

    [Fact]
    public void PreservesDetectedDataTypes()
    {
        var data = Parse(
            "No\tLot\tReg2\tEmpty\tReg3\r\n" +
            "1\tN123\t0.132\t\t1E3\r\n" +
            "2\tN124\t0.157\t\t2.5E-2\r\n");
        var detected = new ColumnDataTypeDetector().DetectColumnTypes(data);

        var plan = CreatePlanner().Plan(WorksheetId, Columns(), 0, data);

        Assert.Equal(detected, plan.Columns.Select(column => column.DataType));
        Assert.Equal(
            [WorksheetDataType.Numeric, WorksheetDataType.String, WorksheetDataType.Numeric, WorksheetDataType.String, WorksheetDataType.Numeric],
            plan.Columns.Select(column => column.DataType));
    }

    [Fact]
    public void KeepsOriginalHeaderAlongsideFinalHeader()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE"), 1, HeadersOnly("SITE", "Reg1"));

        Assert.Equal(["SITE", "Reg1"], plan.Columns.Select(column => column.OriginalHeader));
        Assert.Equal(["SITE_1", "Reg1"], FinalHeaders(plan));
    }

    [Fact]
    public void RenamesHeaderThatDuplicatesExistingColumn()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("No", "SITE"), 2, HeadersOnly("SITE"));

        Assert.Equal(["SITE_1"], FinalHeaders(plan));
    }

    [Fact]
    public void RenamesDuplicateHeadersWithinSamePaste()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns(), 0, HeadersOnly("SITE", "SITE", "Reg1"));

        Assert.Equal(["SITE", "SITE_1", "Reg1"], FinalHeaders(plan));
    }

    [Fact]
    public void DetectsDuplicatesCaseInsensitively()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE"), 1, HeadersOnly("site", "Reg1", "REG1"));

        Assert.Equal(["site_1", "Reg1", "REG1_1"], FinalHeaders(plan));
    }

    [Fact]
    public void PreservesIncomingCasingWhenSuffixIsAdded()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE"), 1, HeadersOnly("Site"));

        var column = Assert.Single(plan.Columns);
        Assert.Equal("Site", column.OriginalHeader);
        Assert.Equal("Site_1", column.FinalHeader);
    }

    [Fact]
    public void UsesMaxExistingSuffixPlusOne()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE", "SITE_1", "SITE_3"), 3, HeadersOnly("SITE"));

        Assert.Equal(["SITE_4"], FinalHeaders(plan));
    }

    [Fact]
    public void DoesNotReuseSuffixGaps()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE", "SITE_2"), 2, HeadersOnly("SITE", "SITE"));

        Assert.Equal(["SITE_3", "SITE_4"], FinalHeaders(plan));
    }

    [Fact]
    public void MatchesExistingSuffixesCaseInsensitively()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE", "site_5"), 2, HeadersOnly("Site"));

        Assert.Equal(["Site_6"], FinalHeaders(plan));
    }

    [Fact]
    public void EarlierGeneratedNamesAffectLaterDuplicateResolution()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE"), 1, HeadersOnly("SITE", "SITE", "Site"));

        Assert.Equal(["SITE_1", "SITE_2", "Site_3"], FinalHeaders(plan));
    }

    [Fact]
    public void EarlierUnchangedNamesInSamePasteCountAsSuffixes()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE"), 1, HeadersOnly("SITE_7", "SITE"));

        Assert.Equal(["SITE_7", "SITE_8"], FinalHeaders(plan));
    }

    [Fact]
    public void KeepsSuffixedHeaderWhenItDoesNotClash()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE"), 1, HeadersOnly("SITE_1"));

        Assert.Equal(["SITE_1"], FinalHeaders(plan));
    }

    [Fact]
    public void ResolvesSuffixedHeaderAgainstItsOwnName()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE", "SITE_1"), 2, HeadersOnly("SITE_1"));

        Assert.Equal(["SITE_1_1"], FinalHeaders(plan));
    }

    [Fact]
    public void IgnoresNonNumericSuffixes()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE", "SITE_A", "SITE_2x", "SITE_-4", "SITE_"), 5, HeadersOnly("SITE"));

        Assert.Equal(["SITE_1"], FinalHeaders(plan));
    }

    [Fact]
    public void ReplacedColumnsDoNotReserveNamesWhenOverwritingSameColumns()
    {
        var existing = Columns("No", "Bin", "SITE", "Reg1", "Reg2");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 0, HeadersOnly("No", "Bin", "SITE", "Reg1"));

        Assert.Equal([0, 1, 2, 3], TargetIndexes(plan));
        Assert.Equal(["No", "Bin", "SITE", "Reg1"], FinalHeaders(plan));
    }

    [Fact]
    public void ColumnsBeforeAppendLikePasteStillReserveNames()
    {
        var existing = Columns("No", "Bin", "SITE", "Reg1", "Reg2");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 5, HeadersOnly("No", "Bin", "SITE", "Reg1"));

        Assert.Equal([5, 6, 7, 8], TargetIndexes(plan));
        Assert.Equal(["No_1", "Bin_1", "SITE_1", "Reg1_1"], FinalHeaders(plan));
    }

    [Fact]
    public void ReplacedColumnsDoNotReserveNamesInPartialOverwrite()
    {
        var existing = Columns("No", "Bin", "SITE", "Reg1", "Reg2", "Reg3");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 3, HeadersOnly("Reg1", "Reg2"));

        Assert.Equal([3, 4], TargetIndexes(plan));
        Assert.Equal(["Reg1", "Reg2"], FinalHeaders(plan));
    }

    [Fact]
    public void SurvivingColumnOutsideTargetRangeStillReservesName()
    {
        var existing = Columns("No", "Bin", "SITE", "Reg1", "Reg2", "Reg3");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 3, HeadersOnly("Reg3"));

        Assert.Equal([3], TargetIndexes(plan));
        Assert.Equal(["Reg3_1"], FinalHeaders(plan));
    }

    [Fact]
    public void ReplacedColumnsDoNotContributeSuffixes()
    {
        var existing = Columns("SITE", "SITE_3", "Reg1");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 1, HeadersOnly("SITE"));

        Assert.Equal(["SITE_1"], FinalHeaders(plan));
    }

    [Fact]
    public void ReplacedColumnNamesCanBeReusedWithinSamePasteOnlyOnce()
    {
        var existing = Columns("SITE", "Reg1");

        var plan = CreatePlanner().Plan(WorksheetId, existing, 0, HeadersOnly("SITE", "SITE"));

        Assert.Equal(["SITE", "SITE_1"], FinalHeaders(plan));
    }

    [Fact]
    public void TrimsHeadersButKeepsInternalWhitespace()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns(), 0, HeadersOnly("  SITE ", " Reg 1  ", "Vth"));

        Assert.Equal(["SITE", "Reg 1", "Vth"], FinalHeaders(plan));
        Assert.Equal(["  SITE ", " Reg 1  ", "Vth"], plan.Columns.Select(column => column.OriginalHeader));
    }

    [Fact]
    public void NamesBlankHeadersAfterOneBasedTargetPosition()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("No"), 1, HeadersOnly("", "Reg1", "   "));

        Assert.Equal(["Column2", "Reg1", "Column4"], FinalHeaders(plan));
        Assert.Equal(["", "Reg1", "   "], plan.Columns.Select(column => column.OriginalHeader));
    }

    [Fact]
    public void NamesBlankHeaderAtMaximumColumnIndexWithoutOverflow()
    {
        // A whitespace-only header needs a data row: the parser rejects whitespace-only text.
        var plan = CreatePlanner().Plan(WorksheetId, Columns(), int.MaxValue, Parse(" \n1"));

        Assert.Equal(["Column2147483648"], FinalHeaders(plan));
    }

    [Fact]
    public void ResolvesDuplicatesAfterTrimming()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("SITE"), 1, HeadersOnly(" site ", "SITE  "));

        Assert.Equal(["site_1", "SITE_2"], FinalHeaders(plan));
    }

    [Fact]
    public void ResolvesDuplicatesOfBlankHeaderFallbackNames()
    {
        var plan = CreatePlanner().Plan(WorksheetId, Columns("No", "Column3"), 2, HeadersOnly("", "column3"));

        Assert.Equal(["Column3_1", "column3_2"], FinalHeaders(plan));
    }

    [Fact]
    public void DoesNotModifyExistingColumns()
    {
        var existing = Columns("No", "SITE");

        CreatePlanner().Plan(WorksheetId, existing, 1, HeadersOnly("SITE", "Reg1"));

        Assert.Equal(2, existing.Count);
        Assert.Equal(["No", "SITE"], existing.Select(column => column.Name));
        Assert.Equal([0, 1], existing.Select(column => column.Index));
    }

    private static string[] PropertyTypeNames(params Type[] types) =>
        types.Select(type => type.FullName!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void PlanExposesOnlyMetadata()
    {
        Assert.Equal(
            PropertyTypeNames(typeof(Guid), typeof(int), typeof(int), typeof(int), typeof(IReadOnlyList<PlannedPasteColumn>)),
            PropertyTypeNames(typeof(WorksheetPastePlan).GetProperties().Select(property => property.PropertyType).ToArray()));
        Assert.Equal(
            PropertyTypeNames(typeof(int), typeof(int), typeof(string), typeof(string), typeof(WorksheetDataType)),
            PropertyTypeNames(typeof(PlannedPasteColumn).GetProperties().Select(property => property.PropertyType).ToArray()));
        Assert.Equal(
            [typeof(ColumnDataTypeDetector)],
            Assert.Single(typeof(WorksheetPastePlanner).GetConstructors()).GetParameters().Select(parameter => parameter.ParameterType));
    }

    [Fact]
    public void RejectsNegativeStartColumnIndex()
    {
        Assert.Throws<ValidationException>(
            () => CreatePlanner().Plan(WorksheetId, Columns(), -1, HeadersOnly("A")));
    }

    [Fact]
    public void RejectsTargetIndexesBeyondMaximumColumnIndex()
    {
        var planner = CreatePlanner();

        var lastColumn = planner.Plan(WorksheetId, Columns(), int.MaxValue, HeadersOnly("A"));
        Assert.Equal(int.MaxValue, Assert.Single(lastColumn.Columns).TargetColumnIndex);

        Assert.Throws<ValidationException>(
            () => planner.Plan(WorksheetId, Columns(), int.MaxValue, HeadersOnly("A", "B")));
    }
}
