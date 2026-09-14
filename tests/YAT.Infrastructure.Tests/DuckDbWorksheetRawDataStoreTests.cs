using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests;

// Behavior of the DuckDB raw data store against a private in-memory database.
public class DuckDbWorksheetRawDataStoreTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DuckDbWorksheetRawDataStore CreateStore() =>
        new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 256L * 1024 * 1024, threads: 1));

    private static double?[] Sequence(int count, double offset = 0) =>
        Enumerable.Range(0, count).Select(index => (double?)(index + offset)).ToArray();

    private static RawDataBlock Block(params RawDataColumn[] columns) => new(columns);

    private static async Task<RawDataBlock> ReadAll(DuckDbWorksheetRawDataStore store, params Guid[] columnIds) =>
        await store.ReadColumnsAsync(WorksheetId, columnIds, 0, int.MaxValue, Token);

    [Fact]
    public async Task WritesAndReadsNumericAndStringColumns()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        var lot = Guid.NewGuid();

        await store.WriteColumnsAsync(WorksheetId, Block(
            new NumericRawDataColumn(reg1, [5, 0.132, null, -2.5E-3]),
            new StringRawDataColumn(lot, ["N123", null, "", "N124"])), Token);

        var block = await ReadAll(store, reg1, lot);

        Assert.Equal(4, block.RowCount);
        Assert.Equal([5, 0.132, null, -2.5E-3], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal(["N123", null, "", "N124"], Assert.IsType<StringRawDataColumn>(block.Columns[1]).Values);
    }

    [Fact]
    public async Task ReturnsColumnsInRequestedOrder()
    {
        using var store = CreateStore();
        var no = Guid.NewGuid();
        var site = Guid.NewGuid();
        var lot = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(
            new NumericRawDataColumn(no, [1, 2]),
            new NumericRawDataColumn(site, [3, 4]),
            new StringRawDataColumn(lot, ["A", "B"])), Token);

        var block = await ReadAll(store, lot, no, site);

        Assert.Equal([lot, no, site], block.Columns.Select(column => column.ColumnId));
        Assert.Equal([3, 4], Assert.IsType<NumericRawDataColumn>(block.Columns[2]).Values);
    }

    [Fact]
    public async Task ReplacementLeavesExactlyTheNewValues()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(1000))), Token);

        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(350, offset: 0.5))), Token);

        var block = await ReadAll(store, reg1);
        Assert.Equal(350, block.RowCount);
        Assert.Equal(Sequence(350, offset: 0.5), Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns)).Values);
    }

    [Fact]
    public async Task ReplacementCanChangeTheDataType()
    {
        using var store = CreateStore();
        var bin = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(bin, [1, 2, 3])), Token);

        await store.WriteColumnsAsync(WorksheetId, Block(new StringRawDataColumn(bin, ["PASS", "FAIL"])), Token);

        var column = Assert.IsType<StringRawDataColumn>(Assert.Single((await ReadAll(store, bin)).Columns));
        Assert.Equal(["PASS", "FAIL"], column.Values);
    }

    [Fact]
    public async Task ReplacingOneColumnKeepsSiblingLiveValues()
    {
        using var store = CreateStore();
        var site = Guid.NewGuid();
        var reg1 = Guid.NewGuid();
        var reg2 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(
            new NumericRawDataColumn(site, [1, 2, 3]),
            new NumericRawDataColumn(reg1, [10, 20, 30]),
            new NumericRawDataColumn(reg2, [0.1, 0.2, 0.3])), Token);

        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [11, 21, 31])), Token);

        var block = await ReadAll(store, site, reg1, reg2);
        Assert.Equal([1, 2, 3], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal([11, 21, 31], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
        Assert.Equal([0.1, 0.2, 0.3], Assert.IsType<NumericRawDataColumn>(block.Columns[2]).Values);
    }

    [Fact]
    public async Task ReadsABoundedRowWindow()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        var lot = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(
            new NumericRawDataColumn(reg1, Sequence(10)),
            new StringRawDataColumn(lot, Enumerable.Range(0, 10).Select(index => (string?)$"L{index}").ToArray())), Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [reg1, lot], 2, 3, Token);

        Assert.Equal(3, block.RowCount);
        Assert.Equal([2, 3, 4], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal(["L2", "L3", "L4"], Assert.IsType<StringRawDataColumn>(block.Columns[1]).Values);
    }

    [Fact]
    public async Task WindowIsClippedAtTheLongestRequestedColumn()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(10))), Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [reg1], 7, 100, Token);

        Assert.Equal([7, 8, 9], Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns)).Values);
    }

    [Fact]
    public async Task ZeroRowCountReturnsATypedZeroRowBlock()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        var lot = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(
            new NumericRawDataColumn(reg1, [1, 2]),
            new StringRawDataColumn(lot, ["A", "B"])), Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [lot, reg1], 0, 0, Token);

        Assert.Equal(0, block.RowCount);
        Assert.IsType<StringRawDataColumn>(block.Columns[0]);
        Assert.IsType<NumericRawDataColumn>(block.Columns[1]);
    }

    [Theory]
    [InlineData(5L)]
    [InlineData(6L)]
    [InlineData(long.MaxValue)]
    public async Task OffsetAtOrPastTheLongestColumnReturnsZeroRows(long rowOffset)
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(5))), Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [reg1], rowOffset, 10, Token);

        Assert.Equal(0, block.RowCount);
    }

    [Fact]
    public async Task ZeroRowBlockCanBeWrittenAndRead()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(20))), Token);

        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [])), Token);

        var block = await ReadAll(store, reg1);
        Assert.Equal(0, block.RowCount);
        Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns));
    }

    [Fact]
    public async Task MixedLengthColumnsArePaddedWithNull()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        var reg2 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(1000))), Token);
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg2, Sequence(350, offset: 0.25))), Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [reg1, reg2], 0, 1000, Token);

        Assert.Equal(1000, block.RowCount);
        var first = Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values;
        var second = Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values;
        Assert.Equal(Sequence(1000), first);
        Assert.Equal(Sequence(350, offset: 0.25), second.Take(350));
        Assert.All(second.Skip(350), Assert.Null);
    }

    [Fact]
    public async Task WindowStraddlingTheEndOfAShorterColumnIsPadded()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        var lot = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(10))), Token);
        await store.WriteColumnsAsync(WorksheetId, Block(new StringRawDataColumn(lot, ["A", "B", "C", "D"])), Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [lot, reg1], 2, 4, Token);

        Assert.Equal(["C", "D", null, null], Assert.IsType<StringRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal([2, 3, 4, 5], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
    }

    [Fact]
    public async Task ColumnsFromDifferentBlocksAlignOnRowIndex()
    {
        using var store = CreateStore();
        var no = Guid.NewGuid();
        var site = Guid.NewGuid();
        var reg2 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(
            new NumericRawDataColumn(no, Sequence(6, offset: 1)),
            new NumericRawDataColumn(site, [1, 2, 3, 4, 1, 2])), Token);
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg2, [0.11, 0.12, 0.13, 0.14, 0.15, 0.16])), Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [reg2, site, no], 3, 3, Token);

        Assert.Equal([0.14, 0.15, 0.16], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal([4, 1, 2], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
        Assert.Equal([4, 5, 6], Assert.IsType<NumericRawDataColumn>(block.Columns[2]).Values);
    }

    [Fact]
    public async Task LargeBlockRoundTripsAcrossManyRowGroups()
    {
        using var store = CreateStore();
        var no = Guid.NewGuid();
        var reg1 = Guid.NewGuid();
        const int rows = 300_000;
        await store.WriteColumnsAsync(WorksheetId, Block(
            new NumericRawDataColumn(no, Sequence(rows)),
            new NumericRawDataColumn(reg1, Enumerable.Range(0, rows).Select(index => index % 7 == 0 ? null : (double?)(index * 0.001)).ToArray())), Token);

        var tail = await store.ReadColumnsAsync(WorksheetId, [reg1, no], rows - 3, 10, Token);

        Assert.Equal(3, tail.RowCount);
        Assert.Equal([rows - 3, rows - 2, rows - 1], Assert.IsType<NumericRawDataColumn>(tail.Columns[1]).Values);
        Assert.Equal(
            Enumerable.Range(rows - 3, 3).Select(index => index % 7 == 0 ? null : (double?)(index * 0.001)),
            Assert.IsType<NumericRawDataColumn>(tail.Columns[0]).Values);
    }

    [Fact]
    public async Task ReadRejectsInvalidColumnIdLists()
    {
        using var store = CreateStore();
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.ReadColumnsAsync(WorksheetId, null!, 0, 1, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadColumnsAsync(WorksheetId, [], 0, 1, Token));
        await Assert.ThrowsAsync<ArgumentException>(() => store.ReadColumnsAsync(WorksheetId, [id, id], 0, 1, Token));
    }

    [Fact]
    public async Task ReadRejectsNegativeOffsetOrCount()
    {
        using var store = CreateStore();
        var id = Guid.NewGuid();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadColumnsAsync(WorksheetId, [id], -1, 1, Token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.ReadColumnsAsync(WorksheetId, [id], 0, -1, Token));
    }

    [Fact]
    public async Task UnknownColumnIsNotFound()
    {
        using var store = CreateStore();
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(known, [1])), Token);

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(
            () => store.ReadColumnsAsync(WorksheetId, [known, unknown], 0, 1, Token));

        Assert.Equal(unknown, exception.Id);
    }

    [Fact]
    public async Task ColumnOfAnotherWorksheetIsNotFound()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [1])), Token);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => store.ReadColumnsAsync(Guid.NewGuid(), [reg1], 0, 1, Token));
    }

    [Fact]
    public async Task WriteRejectsAColumnOwnedByAnotherWorksheet()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [1, 2])), Token);

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.WriteColumnsAsync(Guid.NewGuid(), Block(new NumericRawDataColumn(reg1, [9])), Token));

        Assert.Equal([1, 2], Assert.IsType<NumericRawDataColumn>(Assert.Single((await ReadAll(store, reg1)).Columns)).Values);
    }

    [Fact]
    public async Task WriteRejectsANullBlock()
    {
        using var store = CreateStore();

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.WriteColumnsAsync(WorksheetId, null!, Token));
    }

    [Fact]
    public async Task AlreadyCancelledOperationsDoNothing()
    {
        using var store = CreateStore();
        var reg1 = Guid.NewGuid();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [1])), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.ReadColumnsAsync(WorksheetId, [reg1], 0, 1, cancellation.Token));

        await Assert.ThrowsAsync<EntityNotFoundException>(() => ReadAll(store, reg1));
    }

    [Fact]
    public async Task OperationsAfterDisposeAreRejected()
    {
        var store = CreateStore();
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(Guid.NewGuid(), [1])), Token));
    }

    [Fact]
    public void ConfiguredResourceLimitsAreApplied()
    {
        using var store = new DuckDbWorksheetRawDataStore(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 256L * 1024 * 1024, threads: 2));

        Assert.Equal("256.0 MiB", store.GetEffectiveSetting("memory_limit"));
        Assert.Equal("2", store.GetEffectiveSetting("threads"));
    }

    [Fact]
    public async Task WorksheetRowCountIsZeroWhenNothingIsStored()
    {
        using var store = CreateStore();

        Assert.Equal(0, await store.GetWorksheetRowCountAsync(WorksheetId, Token));
        Assert.Empty(await store.GetStoredColumnIdsAsync(WorksheetId, Token));
    }

    [Fact]
    public async Task WorksheetRowCountIsTheLongestLiveColumnAcrossBlocks()
    {
        using var store = CreateStore();
        var site = Guid.NewGuid();
        var reg1 = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(site, Sequence(3))), Token);
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, Sequence(7))), Token);
        await store.WriteColumnsAsync(Guid.NewGuid(), Block(new NumericRawDataColumn(Guid.NewGuid(), Sequence(50))), Token);

        Assert.Equal(7, await store.GetWorksheetRowCountAsync(WorksheetId, Token));

        // Replacing the longest column with a shorter one lowers the count: retired storage no longer counts.
        await store.WriteColumnsAsync(WorksheetId, Block(new StringRawDataColumn(reg1, ["a", "b"])), Token);

        Assert.Equal(3, await store.GetWorksheetRowCountAsync(WorksheetId, Token));
    }

    [Fact]
    public async Task StoredColumnIdsAreTheWorksheetsLiveColumns()
    {
        using var store = CreateStore();
        var no = Guid.NewGuid();
        var bin = Guid.NewGuid();
        var lot = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(no, [1]), new NumericRawDataColumn(bin, [1])), Token);
        await store.WriteColumnsAsync(WorksheetId, Block(new StringRawDataColumn(lot, ["N1", "N2"]), new NumericRawDataColumn(bin, [2, 3])), Token);
        await store.WriteColumnsAsync(Guid.NewGuid(), Block(new NumericRawDataColumn(Guid.NewGuid(), [1])), Token);

        var ids = await store.GetStoredColumnIdsAsync(WorksheetId, Token);

        Assert.Equal(new HashSet<Guid> { no, bin, lot }, ids.ToHashSet());
    }

    [Fact]
    public async Task RowCountAndStoredColumnIdsAfterDisposeAreRejected()
    {
        var store = CreateStore();
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetWorksheetRowCountAsync(WorksheetId, Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetStoredColumnIdsAsync(WorksheetId, Token));
    }

    [Fact]
    public async Task RowCountAndStoredColumnIdsHonorCancellation()
    {
        using var store = CreateStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetWorksheetRowCountAsync(WorksheetId, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.GetStoredColumnIdsAsync(WorksheetId, cancellation.Token));
    }

    [Fact]
    public void DefaultResourceLimitsAreConservative()
    {
        using var store = new DuckDbWorksheetRawDataStore(new DuckDbRawDataStoreSettings(":memory:"));

        var expectedThreads = DuckDbRawDataStoreSettings.DefaultThreads(Environment.ProcessorCount);
        Assert.Equal(expectedThreads.ToString(System.Globalization.CultureInfo.InvariantCulture), store.GetEffectiveSetting("threads"));
        Assert.InRange(expectedThreads, 1, 4);
    }
}
