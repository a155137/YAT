using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests;

// The distinct values of a raw column, as the DuckDB store lists them (Task #049): typed, in first-occurrence order,
// Missing apart from the values, at most the limit with "exactly the limit" and "more" told apart.
public class DuckDbDistinctValuesTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DuckDbWorksheetRawDataStore CreateStore() =>
        new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 256L * 1024 * 1024, threads: 1));

    private static async Task<Guid> WriteNumericAsync(DuckDbWorksheetRawDataStore store, params double?[] values)
    {
        var id = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, new RawDataBlock([new NumericRawDataColumn(id, values)]), Token);
        return id;
    }

    private static async Task<Guid> WriteTextAsync(DuckDbWorksheetRawDataStore store, params string?[] values)
    {
        var id = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, new RawDataBlock([new StringRawDataColumn(id, values)]), Token);
        return id;
    }

    private static async Task<NumericRawDistinctValues> NumbersAsync(DuckDbWorksheetRawDataStore store, Guid column, int limit = GraphValueFilter.MaximumDistinctValues) =>
        Assert.IsType<NumericRawDistinctValues>(await store.GetDistinctValuesAsync(WorksheetId, column, limit, Token));

    private static async Task<StringRawDistinctValues> TextsAsync(DuckDbWorksheetRawDataStore store, Guid column, int limit = GraphValueFilter.MaximumDistinctValues) =>
        Assert.IsType<StringRawDistinctValues>(await store.GetDistinctValuesAsync(WorksheetId, column, limit, Token));

    [Fact]
    public async Task NumbersAreListedInTheOrderTheyFirstOccur()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 3, 1, 3, 8, 1, 2, 8, 2.5);

        var values = await NumbersAsync(store, site);

        Assert.Equal(site, values.ColumnId);
        Assert.Equal([3, 1, 8, 2, 2.5], values.Values);
        Assert.False(values.HasMissing);
        Assert.False(values.HasMore);
    }

    [Fact]
    public async Task TextIsListedInTheOrderItFirstOccursAndComparedOrdinally()
    {
        using var store = CreateStore();
        var lot = await WriteTextAsync(store, "b", "a", "A", "b", "01", "1", "", "a ", "A");

        var values = await TextsAsync(store, lot);

        Assert.Equal(["b", "a", "A", "01", "1", "", "a "], values.Values);
        Assert.False(values.HasMissing);
        Assert.False(values.HasMore);
    }

    [Fact]
    public async Task NumbersCompareExactly()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 1.0, 1.0000000000000002, 0.30000000000000004, 0.3, 1.0);

        Assert.Equal([1.0, 1.0000000000000002, 0.30000000000000004, 0.3], (await NumbersAsync(store, site)).Values);
    }

    [Fact]
    public async Task MinusZeroAndZeroAreOneValue()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 5, -0.0, 0.0, 5);

        var values = await NumbersAsync(store, site);

        Assert.Equal(2, values.Count);
        Assert.Equal(5, values.Values[0]);
        Assert.Equal(0.0, values.Values[1]);
    }

    [Fact]
    public async Task AnEmptyCellIsMissingAndNeverAValue()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, null, 2, null, 1);
        var lot = await WriteTextAsync(store, "A", null, "B", "A");

        var numbers = await NumbersAsync(store, site);
        var texts = await TextsAsync(store, lot);

        Assert.Equal([2, 1], numbers.Values);
        Assert.True(numbers.HasMissing);
        Assert.Equal(["A", "B"], texts.Values);
        Assert.True(texts.HasMissing);
    }

    [Fact]
    public async Task EmptyTextIsAValueNotMissing()
    {
        using var store = CreateStore();
        var lot = await WriteTextAsync(store, "", "A");

        var values = await TextsAsync(store, lot);

        Assert.Equal(["", "A"], values.Values);
        Assert.False(values.HasMissing);
    }

    [Fact]
    public async Task RowsBeyondAShorterColumnAreMissing()
    {
        using var store = CreateStore();
        await WriteNumericAsync(store, 1, 2, 3, 4, 5);
        var site = await WriteNumericAsync(store, 1, 2);

        var values = await NumbersAsync(store, site);

        Assert.Equal([1, 2], values.Values);
        Assert.True(values.HasMissing);
    }

    [Fact]
    public async Task AColumnOfOnlyEmptyCellsHasNoValues()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, null, null);

        var values = await NumbersAsync(store, site);

        Assert.Empty(values.Values);
        Assert.True(values.HasMissing);
        Assert.False(values.HasMore);
    }

    [Fact]
    public async Task ExactlyTheLimitIsNotMore()
    {
        using var store = CreateStore();
        var values = Enumerable.Range(0, GraphValueFilter.MaximumDistinctValues).Select(index => (double?)index).ToArray();
        var site = await WriteNumericAsync(store, [.. values, .. values]);

        var listed = await NumbersAsync(store, site);

        Assert.Equal(GraphValueFilter.MaximumDistinctValues, listed.Count);
        Assert.False(listed.HasMore);
        Assert.Equal(values.Select(value => value!.Value), listed.Values);
    }

    [Fact]
    public async Task OneMoreThanTheLimitIsMoreAndNeverListedAsComplete()
    {
        using var store = CreateStore();
        var values = Enumerable.Range(0, GraphValueFilter.MaximumDistinctValues + 1).Select(index => $"V{index:D4}").ToArray();
        var lot = await WriteTextAsync(store, [.. values, null]);

        var listed = await TextsAsync(store, lot);

        Assert.Equal(GraphValueFilter.MaximumDistinctValues, listed.Count);
        Assert.True(listed.HasMore);
        Assert.Equal(values.Take(GraphValueFilter.MaximumDistinctValues), listed.Values);
        Assert.DoesNotContain(values[^1], listed.Values);

        // Missing is reported apart from the values, whatever the limit.
        Assert.True(listed.HasMissing);
    }

    [Fact]
    public async Task TheLimitIsTheCallers()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 4, 4, 3, 2, 1);

        var one = await NumbersAsync(store, site, limit: 1);
        var four = await NumbersAsync(store, site, limit: 4);

        Assert.Equal([4], one.Values);
        Assert.True(one.HasMore);
        Assert.Equal([4, 3, 2, 1], four.Values);
        Assert.False(four.HasMore);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task ALimitBelowOneIsRefused(int limit)
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetDistinctValuesAsync(WorksheetId, site, limit, Token));
    }

    [Fact]
    public async Task FirstOccurrenceOrderHoldsOverManyRows()
    {
        using var store = CreateStore();
        const int rowCount = 120_007;
        var sites = new double?[rowCount];
        for (var row = 0; row < rowCount; row++)
        {
            sites[row] = row < 100_000 ? 8 - row % 4 : row % 8 + 1;
        }

        var site = await WriteNumericAsync(store, sites);

        Assert.Equal([8, 7, 6, 5, 1, 2, 3, 4], (await NumbersAsync(store, site)).Values);
    }

    [Fact]
    public async Task AReplacedColumnListsItsNewValues()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 1, 2, 3);
        await store.WriteColumnsAsync(WorksheetId, new RawDataBlock([new NumericRawDataColumn(site, [9, 8])]), Token);

        var values = await NumbersAsync(store, site);

        Assert.Equal([9, 8], values.Values);
        Assert.False(values.HasMissing);
    }

    [Fact]
    public async Task AColumnThatIsNotStoredIsNotFound()
    {
        using var store = CreateStore();
        await WriteNumericAsync(store, 1);

        await Assert.ThrowsAsync<EntityNotFoundException>(
            () => store.GetDistinctValuesAsync(WorksheetId, Guid.NewGuid(), 10, Token));
    }

    [Fact]
    public async Task AColumnOfAnotherWorksheetIsNotFound()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 1);

        await Assert.ThrowsAsync<EntityNotFoundException>(
            () => store.GetDistinctValuesAsync(Guid.NewGuid(), site, 10, Token));
    }

    [Fact]
    public async Task ADeletedColumnIsNotFound()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 1);
        await store.DeleteColumnsAsync(WorksheetId, [site], Token);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => store.GetDistinctValuesAsync(WorksheetId, site, 10, Token));
    }

    [Fact]
    public async Task AnAlreadyCancelledRequestReadsNothing()
    {
        using var store = CreateStore();
        var site = await WriteNumericAsync(store, 1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var task = store.GetDistinctValuesAsync(WorksheetId, site, 10, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(task.IsCanceled);
    }

    [Fact]
    public async Task ADisposedStoreRefuses()
    {
        var store = CreateStore();
        var site = await WriteNumericAsync(store, 1);
        store.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.GetDistinctValuesAsync(WorksheetId, site, 10, Token));
    }
}
