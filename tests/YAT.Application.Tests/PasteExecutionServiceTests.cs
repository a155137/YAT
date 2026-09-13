using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Ingestion;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class PasteExecutionServiceTests
{
    private const string CanonicalText =
        "No\tBin\tSITE\tReg1\tReg2\tReg3\n" +
        "1\t1\t1\t5\t.132\t500\n" +
        "2\t2\t2\t7\t.157\t2350\n" +
        "3\t1\t3\t2\t.122\t450\n";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ParsedTabularData Parse(string text) => new TabularTextParser().Parse(text);

    private sealed class Fixture
    {
        public Fixture()
        {
            Worksheets.Seed(Worksheet);
        }

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "WAT_Lot_A" };

        public FakeWorksheetRepository Worksheets { get; } = new();

        public FakeWorksheetColumnRepository Columns { get; } = new();

        public FakeWorksheetRawDataStore RawStore { get; } = new();

        public PasteExecutionService Service => new(Worksheets, Columns, RawStore);

        public RawDataBlock SingleWrittenBlock
        {
            get
            {
                var write = Assert.Single(RawStore.Writes);
                Assert.Equal(Worksheet.Id, write.WorksheetId);
                return write.Block;
            }
        }

        public WorksheetColumn SeedColumn(
            int index,
            string name,
            WorksheetDataType dataType = WorksheetDataType.Numeric,
            ColumnSemanticType? semanticType = null,
            string? unit = null)
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = Worksheet.Id,
                Index = index,
                Name = name,
                DataType = dataType,
                SemanticType = semanticType,
                Unit = unit
            };
            Columns.Seed(column);
            return column;
        }

        public async Task<IReadOnlyList<WorksheetColumn>> StoredColumnsAsync() =>
            await Columns.GetByWorksheetIdAsync(Worksheet.Id, Token);

        public async Task<WorksheetPastePlan> PlanAsync(ParsedTabularData data, int startColumnIndex) =>
            new WorksheetPastePlanner(new ColumnDataTypeDetector())
                .Plan(Worksheet.Id, await StoredColumnsAsync(), startColumnIndex, data);

        public async Task<PasteExecutionResult> PasteAsync(string text, int startColumnIndex = 0)
        {
            var data = Parse(text);
            var plan = await PlanAsync(data, startColumnIndex);
            return await Service.ExecuteAsync(plan, data, Token);
        }

        public void AssertNothingPersisted()
        {
            Assert.Empty(RawStore.Writes);
            Assert.Empty(Columns.Added);
            Assert.Empty(Columns.Updated);
        }
    }

    private static RawDataColumn RawColumn(RawDataBlock block, Guid columnId) =>
        Assert.Single(block.Columns, column => column.ColumnId == columnId);

    private static IReadOnlyList<double?> NumericValues(RawDataBlock block, Guid columnId) =>
        Assert.IsType<NumericRawDataColumn>(RawColumn(block, columnId)).Values;

    private static IReadOnlyList<string?> StringValues(RawDataBlock block, Guid columnId) =>
        Assert.IsType<StringRawDataColumn>(RawColumn(block, columnId)).Values;

    // 1
    [Fact]
    public async Task PastesIntoEmptyWorksheet()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync(CanonicalText);

        Assert.Equal(fixture.Worksheet.Id, result.WorksheetId);
        Assert.Equal(0, result.StartColumnIndex);
        Assert.Equal(3, result.RowCount);
        Assert.All(result.Columns, column => Assert.True(column.IsNew));
        Assert.All(result.Columns, column => Assert.NotEqual(Guid.Empty, column.ColumnId));
        Assert.Equal(6, result.Columns.Select(column => column.ColumnId).Distinct().Count());

        var stored = await fixture.StoredColumnsAsync();
        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Reg2", "Reg3"], stored.Select(column => column.Name));
        Assert.Equal([0, 1, 2, 3, 4, 5], stored.Select(column => column.Index));
        Assert.All(stored, column => Assert.Equal(WorksheetDataType.Numeric, column.DataType));
        Assert.All(stored, column => Assert.Equal(fixture.Worksheet.Id, column.WorksheetId));
        Assert.Equal(result.Columns.Select(column => column.ColumnId), stored.Select(column => column.Id));
        Assert.Equal(6, fixture.Columns.Added.Count);
        Assert.Empty(fixture.Columns.Updated);

        var block = fixture.SingleWrittenBlock;
        Assert.Equal(3, block.RowCount);
        Assert.Equal(result.Columns.Select(column => column.ColumnId), block.Columns.Select(column => column.ColumnId));
    }

    // 2
    [Fact]
    public async Task OverwritingExistingColumnsPreservesIdsAndIndexes()
    {
        var fixture = new Fixture();
        var no = fixture.SeedColumn(0, "No");
        var bin = fixture.SeedColumn(1, "Bin");

        var result = await fixture.PasteAsync("Lot\tWafer\nN123\t7\n");

        Assert.Equal([no.Id, bin.Id], result.Columns.Select(column => column.ColumnId));
        Assert.All(result.Columns, column => Assert.False(column.IsNew));

        var stored = await fixture.StoredColumnsAsync();
        Assert.Equal([no.Id, bin.Id], stored.Select(column => column.Id));
        Assert.Equal([0, 1], stored.Select(column => column.Index));
        Assert.Equal(["Lot", "Wafer"], stored.Select(column => column.Name));
        Assert.Equal([WorksheetDataType.String, WorksheetDataType.Numeric], stored.Select(column => column.DataType));
        Assert.Equal(2, fixture.Columns.Updated.Count);
        Assert.Empty(fixture.Columns.Added);
        Assert.Equal([no.Id, bin.Id], fixture.SingleWrittenBlock.Columns.Select(column => column.ColumnId));
    }

    [Fact]
    public async Task OverwriteResetsSemanticTypeAndUnit()
    {
        var fixture = new Fixture();
        var site = fixture.SeedColumn(0, "SITE", semanticType: ColumnSemanticType.Site, unit: "#");

        await fixture.PasteAsync("Vth\n0.45\n");

        var stored = Assert.Single(await fixture.StoredColumnsAsync());
        Assert.Equal(site.Id, stored.Id);
        Assert.Equal("Vth", stored.Name);
        Assert.Null(stored.SemanticType);
        Assert.Null(stored.Unit);
    }

    [Fact]
    public async Task DoesNotMutateLoadedColumnInstances()
    {
        var fixture = new Fixture();
        var site = fixture.SeedColumn(0, "SITE", semanticType: ColumnSemanticType.Site, unit: "#");

        await fixture.PasteAsync("Lot\nN123\n");

        Assert.Equal("SITE", site.Name);
        Assert.Equal(WorksheetDataType.Numeric, site.DataType);
        Assert.Equal(ColumnSemanticType.Site, site.SemanticType);
        Assert.Equal("#", site.Unit);
        Assert.NotSame(site, Assert.Single(fixture.Columns.Updated));
    }

    // 3
    [Fact]
    public async Task PartialExtensionReusesOverlappingIdsAndCreatesOnlyMissingColumns()
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "No");
        fixture.SeedColumn(1, "Bin");
        var site = fixture.SeedColumn(2, "SITE");

        var result = await fixture.PasteAsync("SITE\tReg1\tReg2\n1\t5\t0.132\n", startColumnIndex: 2);

        Assert.Equal([2, 3, 4], result.Columns.Select(column => column.Index));
        Assert.Equal([false, true, true], result.Columns.Select(column => column.IsNew));
        Assert.Equal(site.Id, result.Columns[0].ColumnId);
        Assert.Equal(["SITE"], fixture.Columns.Updated.Select(column => column.Name));
        Assert.Equal(["Reg1", "Reg2"], fixture.Columns.Added.Select(column => column.Name));
        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Reg2"], (await fixture.StoredColumnsAsync()).Select(column => column.Name));
    }

    // 4
    [Fact]
    public async Task UntouchedColumnsRemainUnchanged()
    {
        var fixture = new Fixture();
        var no = fixture.SeedColumn(0, "No", semanticType: ColumnSemanticType.Lot, unit: "u");
        fixture.SeedColumn(1, "Bin");
        var site = fixture.SeedColumn(2, "SITE", WorksheetDataType.String);

        await fixture.PasteAsync("Reg1\n5\n", startColumnIndex: 1);

        var stored = await fixture.StoredColumnsAsync();
        Assert.Same(no, stored[0]);
        Assert.Same(site, stored[2]);
        Assert.Equal(("No", WorksheetDataType.Numeric, ColumnSemanticType.Lot, "u"), (no.Name, no.DataType, no.SemanticType, no.Unit));
        Assert.Equal(("SITE", WorksheetDataType.String), (site.Name, site.DataType));
        Assert.Equal(["Reg1"], fixture.Columns.Updated.Select(column => column.Name));
        Assert.Equal([stored[1].Id], fixture.SingleWrittenBlock.Columns.Select(column => column.ColumnId));
    }

    // 5
    [Fact]
    public async Task MultiColumnPastePerformsExactlyOneRawWrite()
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "No");
        fixture.SeedColumn(1, "Bin");

        await fixture.PasteAsync(CanonicalText);

        var block = fixture.SingleWrittenBlock;
        Assert.Equal(6, block.Columns.Count);
    }

    [Fact]
    public async Task WritesRawDataBeforeColumnMetadata()
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "No");
        var metadataWritesAtRawWrite = -1;
        fixture.RawStore.OnWrite = () => metadataWritesAtRawWrite = fixture.Columns.Added.Count + fixture.Columns.Updated.Count;

        await fixture.PasteAsync("No\tBin\n1\t1\n");

        Assert.Equal(0, metadataWritesAtRawWrite);
        Assert.Single(fixture.Columns.Updated);
        Assert.Single(fixture.Columns.Added);
    }

    // 6
    [Fact]
    public async Task PreservesCanonicalSemiconductorRowAlignment()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync(CanonicalText);

        var block = fixture.SingleWrittenBlock;
        var ids = result.Columns.Select(column => column.ColumnId).ToArray();
        Assert.Equal([1, 2, 3], NumericValues(block, ids[0]));
        Assert.Equal([1, 2, 1], NumericValues(block, ids[1]));
        Assert.Equal([1, 2, 3], NumericValues(block, ids[2]));
        Assert.Equal([5, 7, 2], NumericValues(block, ids[3]));
        Assert.Equal([0.132, 0.157, 0.122], NumericValues(block, ids[4]));
        Assert.Equal([500, 2350, 450], NumericValues(block, ids[5]));
    }

    [Fact]
    public async Task EmptyCellsKeepTheirRowPositionAcrossColumns()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync(
            "No\tLot\tReg2\n" +
            "1\tN1\t0.1\n" +
            "2\t\t\n" +
            "3\tN3\t0.3\n");

        var block = fixture.SingleWrittenBlock;
        var ids = result.Columns.Select(column => column.ColumnId).ToArray();
        Assert.Equal([1, 2, 3], NumericValues(block, ids[0]));
        Assert.Equal(["N1", null, "N3"], StringValues(block, ids[1]));
        Assert.Equal([0.1, null, 0.3], NumericValues(block, ids[2]));
    }

    // 7
    [Fact]
    public async Task EmptyNumericCellBecomesNull()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync("Reg1\tNo\n5\t1\n\t2\n7\t3\n");

        Assert.Equal(WorksheetDataType.Numeric, result.Columns[0].DataType);
        Assert.Equal([5, null, 7], NumericValues(fixture.SingleWrittenBlock, result.Columns[0].ColumnId));
    }

    // 8
    [Fact]
    public async Task EmptyStringCellBecomesNull()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync("Lot\tNo\nN1\t1\n\t2\n");

        Assert.Equal(WorksheetDataType.String, result.Columns[0].DataType);
        Assert.Equal(["N1", null], StringValues(fixture.SingleWrittenBlock, result.Columns[0].ColumnId));
    }

    [Fact]
    public async Task AllEmptyColumnIsStringOfNulls()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync("Empty\tNo\n\t1\n \t2\n");

        Assert.Equal(WorksheetDataType.String, result.Columns[0].DataType);
        Assert.Equal([null, null], StringValues(fixture.SingleWrittenBlock, result.Columns[0].ColumnId));
    }

    // 9
    [Theory]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData(" ")]
    public async Task WhitespaceOnlyCellBecomesNull(string whitespace)
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync($"Reg1\tLot\n5\tN1\n{whitespace}\t{whitespace}\n");

        var block = fixture.SingleWrittenBlock;
        Assert.Equal([5, null], NumericValues(block, result.Columns[0].ColumnId));
        Assert.Equal(["N1", null], StringValues(block, result.Columns[1].ColumnId));
    }

    // 10
    [Fact]
    public async Task NonEmptyStringValuesAreKeptVerbatim()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync("Lot\n N123 \nLot A\n\tx\n".Replace("\n\tx\n", "\n"));

        Assert.Equal([" N123 ", "Lot A"], StringValues(fixture.SingleWrittenBlock, result.Columns[0].ColumnId));
    }

    // 11
    [Fact]
    public async Task HeadersAreTrimmedButKeepInternalWhitespace()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync("  SITE \t Reg 1  \n1\t5\n");

        Assert.Equal(["SITE", "Reg 1"], result.Columns.Select(column => column.Name));
        Assert.Equal(["SITE", "Reg 1"], (await fixture.StoredColumnsAsync()).Select(column => column.Name));
    }

    // 12
    [Fact]
    public async Task BlankHeadersAreNamedAfterTargetPosition()
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "No");

        var result = await fixture.PasteAsync("\tReg1\t \n1\t5\t7\n", startColumnIndex: 1);

        Assert.Equal(["Column2", "Reg1", "Column4"], result.Columns.Select(column => column.Name));
        Assert.Equal(["No", "Column2", "Reg1", "Column4"], (await fixture.StoredColumnsAsync()).Select(column => column.Name));
    }

    // 13
    [Fact]
    public async Task DuplicateHeadersFollowPlannerResolutionRules()
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "SITE");
        fixture.SeedColumn(1, "SITE_1");
        fixture.SeedColumn(2, "SITE_3");

        var result = await fixture.PasteAsync(" site\tSITE\n1\t2\n", startColumnIndex: 3);

        Assert.Equal(["site_4", "SITE_5"], result.Columns.Select(column => column.Name));
    }

    // 14
    [Fact]
    public async Task ConvertsNumericNotationsWithInvariantCulture()
    {
        var fixture = new Fixture();

        var result = await fixture.PasteAsync("Reg\n123\n-23.5\n.132\n1.57E-6\n-2.5e3\n");

        Assert.Equal([123, -23.5, 0.132, 1.57E-6, -2500], NumericValues(fixture.SingleWrittenBlock, result.Columns[0].ColumnId));
    }

    [Theory]
    [InlineData("123", 123)]
    [InlineData("-23.5", -23.5)]
    [InlineData(".132", 0.132)]
    [InlineData("1.57E-6", 1.57E-6)]
    [InlineData(" 5 ", 5)]
    public async Task DetectorAndExecutorAgreeOnNumericCells(string cell, double expected)
    {
        var fixture = new Fixture();
        Assert.Equal(WorksheetDataType.Numeric, new ColumnDataTypeDetector().Detect([cell]));

        var result = await fixture.PasteAsync($"Reg\n{cell}\n");

        Assert.Equal([expected], NumericValues(fixture.SingleWrittenBlock, result.Columns[0].ColumnId));
    }

    // 15
    [Fact]
    public async Task NumericToStringOverwriteKeepsId()
    {
        var fixture = new Fixture();
        var column = fixture.SeedColumn(0, "Reg1");

        await fixture.PasteAsync("Lot\nN123\n");

        var stored = Assert.Single(await fixture.StoredColumnsAsync());
        Assert.Equal((column.Id, WorksheetDataType.String), (stored.Id, stored.DataType));
        Assert.Equal(["N123"], StringValues(fixture.SingleWrittenBlock, column.Id));
    }

    // 16
    [Fact]
    public async Task StringToNumericOverwriteKeepsId()
    {
        var fixture = new Fixture();
        var column = fixture.SeedColumn(0, "Lot", WorksheetDataType.String);

        await fixture.PasteAsync("Reg1\n0.132\n");

        var stored = Assert.Single(await fixture.StoredColumnsAsync());
        Assert.Equal((column.Id, WorksheetDataType.Numeric), (stored.Id, stored.DataType));
        Assert.Equal([0.132], NumericValues(fixture.SingleWrittenBlock, column.Id));
    }

    // 17
    [Theory]
    [InlineData("Bin\n9\n")]
    [InlineData("Bin\n9\n8\n7\n6\n5\n")]
    [InlineData("Bin\n")]
    public async Task AcceptsIncomingRowCountDifferentFromExistingColumns(string replacement)
    {
        var fixture = new Fixture();
        await fixture.PasteAsync(CanonicalText);
        var bin = (await fixture.StoredColumnsAsync())[1];
        fixture.RawStore.Writes.Clear();

        var result = await fixture.PasteAsync(replacement, startColumnIndex: 1);

        var expectedRows = Parse(replacement).Rows.Count;
        Assert.Equal(expectedRows, result.RowCount);
        Assert.Equal(bin.Id, Assert.Single(result.Columns).ColumnId);
        Assert.Equal(expectedRows, fixture.SingleWrittenBlock.RowCount);
    }

    // 18
    [Theory]
    [InlineData("No\tBin\n1\t1\n", "No\tBin\tSITE\n1\t1\t1\n")]
    [InlineData("No\tBin\n1\t1\n", "No\n1\n")]
    [InlineData("No\tBin\n1\t1\n", "No\tBin\n1\t1\n2\t2\n")]
    [InlineData("No\tBin\n1\t1\n", "No\tSITE\n1\t1\n")]
    public async Task PlanDataMismatchFailsBeforePersistence(string plannedText, string executedText)
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "No");
        var plan = await fixture.PlanAsync(Parse(plannedText), 0);

        await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Service.ExecuteAsync(plan, Parse(executedText), Token));

        fixture.AssertNothingPersisted();
    }

    [Fact]
    public async Task MissingWorksheetFailsBeforePersistence()
    {
        var fixture = new Fixture();
        var data = Parse("No\n1\n");
        var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).Plan(Guid.NewGuid(), [], 0, data);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Service.ExecuteAsync(plan, data, Token));

        fixture.AssertNothingPersisted();
    }

    [Fact]
    public async Task OutdatedPlanFailsBeforePersistence()
    {
        var fixture = new Fixture();
        var data = Parse("Reg1\n5\n");
        var plan = await fixture.PlanAsync(data, 0);
        fixture.SeedColumn(3, "reg1");

        await Assert.ThrowsAsync<ValidationException>(() => fixture.Service.ExecuteAsync(plan, data, Token));

        fixture.AssertNothingPersisted();
    }

    [Fact]
    public async Task DuplicateExistingIndexInTargetRangeFailsBeforePersistence()
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "No");
        fixture.SeedColumn(0, "Bin");

        await Assert.ThrowsAsync<ValidationException>(() => fixture.PasteAsync("Reg1\n5\n"));

        fixture.AssertNothingPersisted();
    }

    // 19
    [Fact]
    public async Task NumericConversionInconsistencyFailsBeforePersistenceWithoutStringFallback()
    {
        var fixture = new Fixture();
        var plan = await fixture.PlanAsync(Parse("No\tReg1\n1\t5\n2\t7\n"), 0);
        Assert.Equal(WorksheetDataType.Numeric, plan.Columns[1].DataType);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => fixture.Service.ExecuteAsync(plan, Parse("No\tReg1\n1\t5\n2\tabc\n"), Token));

        Assert.Contains("Reg1", exception.Message);
        fixture.AssertNothingPersisted();
        Assert.Empty(await fixture.StoredColumnsAsync());
    }

    // 20
    [Fact]
    public async Task CancellationBeforePersistenceWritesNothing()
    {
        var fixture = new Fixture();
        fixture.SeedColumn(0, "No");
        var data = Parse(CanonicalText);
        var plan = await fixture.PlanAsync(data, 0);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Service.ExecuteAsync(plan, data, cancellation.Token));

        fixture.AssertNothingPersisted();
    }

    [Fact]
    public async Task CancellationDuringRawWriteIsPreservedAndSkipsMetadata()
    {
        var fixture = new Fixture();
        var canceled = new OperationCanceledException();
        fixture.RawStore.WriteFailure = canceled;

        var thrown = await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.PasteAsync(CanonicalText));

        Assert.Same(canceled, thrown);
        fixture.AssertNothingPersisted();
    }

    // 21
    [Fact]
    public async Task RawWriteFailurePreventsMetadataPersistence()
    {
        var fixture = new Fixture();
        var no = fixture.SeedColumn(0, "No");
        var failure = new RawDataStorageException("Simulated storage failure.");
        fixture.RawStore.WriteFailure = failure;

        var thrown = await Assert.ThrowsAsync<RawDataStorageException>(() => fixture.PasteAsync("Lot\tReg1\nN1\t5\n"));

        Assert.Same(failure, thrown);
        fixture.AssertNothingPersisted();
        Assert.Same(no, Assert.Single(await fixture.StoredColumnsAsync()));
    }

    // 22 — documents the known limitation: metadata and raw data do not share a transaction.
    [Fact]
    public async Task MetadataFailureAfterRawWriteLeavesRawDataWrittenAndMetadataPartial()
    {
        var fixture = new Fixture();
        var no = fixture.SeedColumn(0, "No");
        var failure = new InvalidOperationException("Simulated metadata failure.");
        fixture.Columns.FailingWrite = (2, failure);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.PasteAsync("Lot\tReg1\tReg2\nN1\t5\t0.1\n"));

        Assert.Same(failure, thrown);

        // The raw write completed for all three columns and is not rolled back.
        Assert.Equal(3, fixture.SingleWrittenBlock.Columns.Count);

        // Metadata is written in plan order: the first write (update of index 0) succeeded, the rest did not happen.
        Assert.Equal([no.Id], fixture.Columns.Updated.Select(column => column.Id));
        Assert.Empty(fixture.Columns.Added);
        var stored = Assert.Single(await fixture.StoredColumnsAsync());
        Assert.Equal((no.Id, "Lot", WorksheetDataType.String), (stored.Id, stored.Name, stored.DataType));
    }

    [Fact]
    public void ResultCarriesOnlyMetadata()
    {
        Assert.Equal(
            [typeof(Guid), typeof(int), typeof(int), typeof(IReadOnlyList<PastedColumn>)],
            typeof(PasteExecutionResult).GetConstructors().Single().GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Equal(
            [typeof(Guid), typeof(int), typeof(string), typeof(WorksheetDataType), typeof(bool)],
            typeof(PastedColumn).GetConstructors().Single(constructor => constructor.GetParameters().Length > 1)
                .GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(
            typeof(PasteExecutionResult).GetProperties().Concat(typeof(PastedColumn).GetProperties()),
            property => typeof(RawDataBlock).IsAssignableFrom(property.PropertyType)
                || typeof(RawDataColumn).IsAssignableFrom(property.PropertyType));
    }
}
