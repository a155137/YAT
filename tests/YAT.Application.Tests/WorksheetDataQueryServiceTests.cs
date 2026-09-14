using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Queries;
using YAT.Application.Tests.TestDoubles;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public class WorksheetDataQueryServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Fixture
    {
        public Fixture()
        {
            Worksheets.Seed(Worksheet);
        }

        public Worksheet Worksheet { get; } = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

        public FakeWorksheetRepository Worksheets { get; } = new();

        public FakeWorksheetColumnRepository Columns { get; } = new();

        public FakeWorksheetRawDataStore RawStore { get; } = new();

        public WorksheetDataQueryService Service => new(Worksheets, Columns, RawStore);

        public WorksheetColumn Column(int index, string name, WorksheetDataType dataType = WorksheetDataType.Numeric)
        {
            var column = new WorksheetColumn { Id = Guid.NewGuid(), WorksheetId = Worksheet.Id, Index = index, Name = name, DataType = dataType };
            Columns.Seed(column);
            return column;
        }

        public Task StoreAsync(params RawDataColumn[] columns) =>
            RawStore.WriteColumnsAsync(Worksheet.Id, new RawDataBlock(columns), Token);

        public Task<WorksheetDataPage> PageAsync(long rowOffset = 0, int rowCount = WorksheetDataQueryService.MaxPageRowCount) =>
            Service.GetPageAsync(Worksheet.Id, rowOffset, rowCount, Token);
    }

    private static double?[] Sequence(int count, int start = 0) =>
        Enumerable.Range(start, count).Select(value => (double?)value).ToArray();

    private static IReadOnlyList<double?> Numbers(WorksheetDataPageColumn column) =>
        Assert.IsType<NumericRawDataColumn>(column.Values).Values;

    private static IReadOnlyList<string?> Texts(WorksheetDataPageColumn column) =>
        Assert.IsType<StringRawDataColumn>(column.Values).Values;

    // 1
    [Fact]
    public async Task ReturnsColumnsOrderedByIndexWithTheirPageValues()
    {
        var fixture = new Fixture();
        var bin = fixture.Column(1, "Bin");
        var lot = fixture.Column(2, "Lot", WorksheetDataType.String);
        var no = fixture.Column(0, "No");
        await fixture.StoreAsync(
            new NumericRawDataColumn(no.Id, [1, 2, 3]),
            new NumericRawDataColumn(bin.Id, [1, 2, 1]),
            new StringRawDataColumn(lot.Id, ["N123", null, "N125"]));

        var page = await fixture.PageAsync();

        Assert.Equal(fixture.Worksheet.Id, page.WorksheetId);
        Assert.Equal(3, page.TotalRowCount);
        Assert.Equal(0, page.RowOffset);
        Assert.Equal(3, page.RowCount);
        Assert.Equal(["No", "Bin", "Lot"], page.Columns.Select(column => column.Column.Name));
        Assert.Equal([no.Id, bin.Id, lot.Id], page.Columns.Select(column => column.Values.ColumnId));
        Assert.Equal([1, 2, 3], Numbers(page.Columns[0]));
        Assert.Equal([1, 2, 1], Numbers(page.Columns[1]));
        Assert.Equal(["N123", null, "N125"], Texts(page.Columns[2]));
    }

    // 2, 6
    [Fact]
    public async Task TotalRowCountIsTheLongestLiveColumnAndShorterColumnsAreNullPadded()
    {
        var fixture = new Fixture();
        var site = fixture.Column(0, "SITE");
        var reg1 = fixture.Column(1, "Reg1");
        await fixture.StoreAsync(new NumericRawDataColumn(site.Id, [1, 2]));
        await fixture.StoreAsync(new NumericRawDataColumn(reg1.Id, [5, 7, 2, 9, 4]));

        var page = await fixture.PageAsync();

        Assert.Equal(5, page.TotalRowCount);
        Assert.Equal(5, page.RowCount);
        Assert.Equal([1, 2, null, null, null], Numbers(page.Columns[0]));
        Assert.Equal([5, 7, 2, 9, 4], Numbers(page.Columns[1]));
    }

    // 3
    [Fact]
    public async Task EmptyWorksheetReturnsZeroRowsWithoutReading()
    {
        var fixture = new Fixture();

        var page = await fixture.PageAsync();

        Assert.Equal(0, page.TotalRowCount);
        Assert.Equal(0, page.RowCount);
        Assert.Empty(page.Columns);
        Assert.Empty(fixture.RawStore.Reads);
    }

    // 4
    [Fact]
    public async Task PageHoldsAtMost500Rows()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Column(0, "Reg1");
        await fixture.StoreAsync(new NumericRawDataColumn(reg1.Id, Sequence(1200)));

        var page = await fixture.PageAsync();

        Assert.Equal(1200, page.TotalRowCount);
        Assert.Equal(500, page.RowCount);
        Assert.Equal(Sequence(500), Numbers(page.Columns[0]));
        Assert.Equal(500, Assert.Single(fixture.RawStore.Reads).RowCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.PageAsync(0, WorksheetDataQueryService.MaxPageRowCount + 1));
    }

    // 5
    [Fact]
    public async Task LaterPagesUseTheRequestedOffsetAndEndAtTheLastRow()
    {
        var fixture = new Fixture();
        var reg1 = fixture.Column(0, "Reg1");
        await fixture.StoreAsync(new NumericRawDataColumn(reg1.Id, Sequence(1200)));

        var second = await fixture.PageAsync(500);
        var last = await fixture.PageAsync(1000);

        Assert.Equal(500, second.RowOffset);
        Assert.Equal(Sequence(500, start: 500), Numbers(second.Columns[0]));
        Assert.Equal(1000, last.RowOffset);
        Assert.Equal(200, last.RowCount);
        Assert.Equal(Sequence(200, start: 1000), Numbers(last.Columns[0]));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(10)]
    public async Task OffsetAtOrPastTheEndReturnsNoRowsWithoutReading(long rowOffset)
    {
        var fixture = new Fixture();
        var reg1 = fixture.Column(0, "Reg1");
        await fixture.StoreAsync(new NumericRawDataColumn(reg1.Id, [1, 2, 3]));

        var page = await fixture.PageAsync(rowOffset);

        Assert.Equal(3, page.TotalRowCount);
        Assert.Equal(0, page.RowCount);
        Assert.Empty(Numbers(Assert.Single(page.Columns)));
        Assert.Empty(fixture.RawStore.Reads);
    }

    [Fact]
    public async Task MetadataOnlyColumnsAreNotReadAndGetEmptyValues()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");
        var vth = fixture.Column(1, "Vth");
        var comment = fixture.Column(2, "Comment", WorksheetDataType.String);
        var tested = fixture.Column(3, "TestedAt", WorksheetDataType.DateTime);
        await fixture.StoreAsync(new NumericRawDataColumn(no.Id, [1, 2]));

        var page = await fixture.PageAsync();

        Assert.Equal([no.Id], Assert.Single(fixture.RawStore.Reads).ColumnIds);
        Assert.Equal(["No", "Vth", "Comment", "TestedAt"], page.Columns.Select(column => column.Column.Name));
        Assert.Equal([1, 2], Numbers(page.Columns[0]));
        Assert.Equal([null, null], Numbers(page.Columns[1]));
        Assert.Equal([null, null], Texts(page.Columns[2]));
        Assert.Equal([null, null], Texts(page.Columns[3]));
        Assert.Equal(vth.Id, page.Columns[1].Values.ColumnId);
        Assert.Equal(tested.Id, page.Columns[3].Values.ColumnId);
    }

    [Fact]
    public async Task WorksheetWithOnlyMetadataColumnsHasZeroRows()
    {
        var fixture = new Fixture();
        fixture.Column(0, "Vth");

        var page = await fixture.PageAsync();

        Assert.Equal(0, page.TotalRowCount);
        Assert.Equal(0, page.RowCount);
        Assert.Equal("Vth", Assert.Single(page.Columns).Column.Name);
        Assert.Empty(fixture.RawStore.Reads);
    }

    [Fact]
    public async Task RawColumnsWithoutMetadataCanExtendTheRowCountAndShownColumnsArePadded()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");
        await fixture.StoreAsync(new NumericRawDataColumn(no.Id, [1, 2]));
        // Raw values whose metadata write never happened (the known non-atomic paste limitation).
        await fixture.StoreAsync(new NumericRawDataColumn(Guid.NewGuid(), [9, 9, 9, 9]));

        var page = await fixture.PageAsync();

        Assert.Equal(4, page.TotalRowCount);
        Assert.Equal(4, page.RowCount);
        Assert.Equal([1, 2, null, null], Numbers(Assert.Single(page.Columns)));
    }

    [Fact]
    public async Task ColumnsOfOtherWorksheetsAreNotIncluded()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");
        await fixture.StoreAsync(new NumericRawDataColumn(no.Id, [1]));
        await fixture.RawStore.WriteColumnsAsync(Guid.NewGuid(), new RawDataBlock([new NumericRawDataColumn(Guid.NewGuid(), [1, 2, 3, 4])]), Token);

        var page = await fixture.PageAsync();

        Assert.Equal(1, page.TotalRowCount);
        Assert.Equal([1], Numbers(Assert.Single(page.Columns)));
    }

    [Fact]
    public async Task RejectsInvalidArgumentsAndMissingWorksheet()
    {
        var fixture = new Fixture();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.PageAsync(-1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => fixture.PageAsync(0, -1));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Service.GetPageAsync(Guid.NewGuid(), 0, 10, Token));
        Assert.Empty(fixture.RawStore.Reads);
    }

    [Fact]
    public async Task ZeroRowCountReturnsColumnsWithoutValues()
    {
        var fixture = new Fixture();
        var no = fixture.Column(0, "No");
        await fixture.StoreAsync(new NumericRawDataColumn(no.Id, [1, 2]));

        var page = await fixture.PageAsync(0, 0);

        Assert.Equal(2, page.TotalRowCount);
        Assert.Equal(0, page.RowCount);
        Assert.Empty(Numbers(Assert.Single(page.Columns)));
    }
}
