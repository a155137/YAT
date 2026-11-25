using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Queries;
using YAT.app.Composition;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.App.Tests;

public sealed class WorksheetViewportDataCoordinatorTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class StubQuerySource : IWorksheetViewportQuerySource
    {
        private readonly InMemoryWorksheetRepository _worksheets = new();
        private readonly InMemoryWorksheetColumnRepository _columns = new();
        private readonly Dictionary<Guid, List<Guid>> _columnIdsByWorksheet = [];
        private readonly Dictionary<Guid, RawDataColumn> _rawColumns = [];

        public Func<Guid, long, CancellationToken, Task<WorksheetPageMetadata>>? OnLoadMetadata { get; set; }

        public Func<Guid, long, IReadOnlyList<Guid>, CancellationToken, Task<WorksheetColumnWindow>>? OnLoadWindow { get; set; }

        public List<(Guid WorksheetId, long RowOffset, IReadOnlyList<Guid> ColumnIds)> WindowCalls { get; } = [];

        public int MaxConcurrentCalls { get; private set; }

        private int _concurrentCalls;

        public Guid SeedWorksheet(int columnCount, int rowCount, WorksheetDataType dataType = WorksheetDataType.Numeric)
        {
            var wsId = Guid.NewGuid();
            _worksheets.AddAsync(new Worksheet { Id = wsId, Name = "Sheet1" }, CancellationToken.None).GetAwaiter().GetResult();
            var ids = new List<Guid>(columnCount);
            for (var i = 0; i < columnCount; i++)
            {
                var id = Guid.NewGuid();
                _columns.AddAsync(new WorksheetColumn
                {
                    Id = id,
                    WorksheetId = wsId,
                    Index = i,
                    Name = $"C{i}",
                    DataType = dataType
                }, CancellationToken.None).GetAwaiter().GetResult();
                _rawColumns[id] = dataType == WorksheetDataType.Numeric
                    ? new NumericRawDataColumn(id, Enumerable.Range(0, rowCount).Select(r => (double?)(i + (r * 0.01))).ToArray())
                    : new StringRawDataColumn(id, Enumerable.Range(0, rowCount).Select(r => (string?)$"Val_{i}_{r}_晶圓測試").ToArray());
                ids.Add(id);
            }

            _columnIdsByWorksheet[wsId] = ids;
            return wsId;
        }

        public IReadOnlyList<Guid> GetColumnIds(Guid worksheetId) => _columnIdsByWorksheet[worksheetId];

        private WorksheetDataQueryService CreateQueryService() =>
            new(_worksheets, _columns, new MemRawStore(_columnIdsByWorksheet, _rawColumns));

        public async Task<WorksheetPageMetadata> LoadPageMetadataAsync(Guid worksheetId, long rowOffset, CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _concurrentCalls);
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, current);
            try
            {
                if (OnLoadMetadata is not null)
                {
                    return await OnLoadMetadata(worksheetId, rowOffset, cancellationToken);
                }

                return await CreateQueryService().GetPageMetadataAsync(
                    worksheetId, rowOffset, WorksheetDataQueryService.MaxPageRowCount, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCalls);
            }
        }

        public async Task<WorksheetColumnWindow> LoadColumnWindowAsync(
            Guid worksheetId,
            long rowOffset,
            IReadOnlyList<Guid> columnIds,
            CancellationToken cancellationToken)
        {
            WindowCalls.Add((worksheetId, rowOffset, columnIds.ToArray()));
            var current = Interlocked.Increment(ref _concurrentCalls);
            MaxConcurrentCalls = Math.Max(MaxConcurrentCalls, current);
            try
            {
                if (OnLoadWindow is not null)
                {
                    return await OnLoadWindow(worksheetId, rowOffset, columnIds, cancellationToken);
                }

                return await CreateQueryService().GetColumnWindowAsync(
                    worksheetId, rowOffset, WorksheetDataQueryService.MaxPageRowCount, columnIds, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentCalls);
            }
        }

        public Task<WorksheetColumnWindow> BuildWindowAsync(
            Guid worksheetId, long rowOffset, IReadOnlyList<Guid> columnIds, CancellationToken cancellationToken) =>
            CreateQueryService().GetColumnWindowAsync(
                worksheetId, rowOffset, WorksheetDataQueryService.MaxPageRowCount, columnIds, cancellationToken);

        private sealed class MemRawStore(
            Dictionary<Guid, List<Guid>> columnIdsByWorksheet,
            Dictionary<Guid, RawDataColumn> rawColumns) : IWorksheetRawDataStore
        {
            public Task WriteColumnsAsync(Guid worksheetId, RawDataBlock block, CancellationToken ct) => Task.CompletedTask;
            public Task WriteColumnsAsync(Guid worksheetId, IReadOnlyList<RawColumnSchema> cols, Action<IRawDataRowWriter> writeRows, CancellationToken ct) => Task.CompletedTask;
            public Task<long> GetWorksheetRowCountAsync(Guid worksheetId, CancellationToken ct) =>
                Task.FromResult(columnIdsByWorksheet.TryGetValue(worksheetId, out var list) && list.Count > 0
                    ? (long)rawColumns[list[0]].RowCount
                    : 0L);
            public Task<IReadOnlySet<Guid>> GetStoredColumnIdsAsync(Guid worksheetId, CancellationToken ct) =>
                Task.FromResult<IReadOnlySet<Guid>>(columnIdsByWorksheet.TryGetValue(worksheetId, out var list)
                    ? list.ToHashSet()
                    : []);
            public Task<RawDistinctValues> GetDistinctValuesAsync(Guid worksheetId, Guid columnId, int limit, CancellationToken ct) =>
                throw new NotSupportedException();
            public Task DeleteColumnsAsync(Guid worksheetId, IReadOnlyList<Guid> columnIds, CancellationToken ct) => Task.CompletedTask;
            public Task<RawDataBlock> ReadColumnsAsync(Guid worksheetId, IReadOnlyList<Guid> columnIds, long rowOffset, int rowCount, CancellationToken ct) =>
                Task.FromResult(new RawDataBlock(columnIds.Select(id => rawColumns[id]).ToArray()));
        }
    }

    [Fact]
    public async Task LruEvictionRespectsMaxCachedBlocksAndProtectsVisibleRange()
    {
        var source = new StubQuerySource();
        var wsId = source.SeedWorksheet(1000, 500);
        using var coordinator = new WorksheetViewportDataCoordinator(source);

        coordinator.SetWorksheet(wsId);
        await Task.Yield();
        Assert.True(coordinator.IsMetadataLoaded);

        for (var b = 0; b < WorksheetViewportDataCoordinator.MaxCachedBlocks; b++)
        {
            coordinator.RequestVisibleColumns(b * 32, (b * 32) + 5);
            await Task.Yield();
        }

        Assert.Equal(WorksheetViewportDataCoordinator.MaxCachedBlocks, coordinator.CachedBlockCount);

        // Touch block 0 so block 1 becomes LRU.
        coordinator.RequestVisibleColumns(0, 5);
        await Task.Yield();

        // Load block 8 -> evicts block 1, keeps block 0.
        coordinator.RequestVisibleColumns(8 * 32, (8 * 32) + 5);
        await Task.Yield();

        Assert.Equal(WorksheetViewportDataCoordinator.MaxCachedBlocks, coordinator.CachedBlockCount);
        Assert.Contains(0, coordinator.CachedBlockIndices);
        Assert.DoesNotContain(1, coordinator.CachedBlockIndices);
        Assert.Contains(8, coordinator.CachedBlockIndices);
    }

    [Fact]
    public async Task RapidHorizontalScrollingCancelsStaleBlockAndLoadsLatestPendingOnly()
    {
        var firstBlockStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstBlockCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubQuerySource();
        var wsId = source.SeedWorksheet(1000, 500);

        var firstColId = source.GetColumnIds(wsId)[0];
        source.OnLoadWindow = async (id, offset, ids, ct) =>
        {
            if (ids[0] == firstColId)
            {
                firstBlockStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    firstBlockCancelled.TrySetResult();
                    throw;
                }
            }

            return await source.BuildWindowAsync(id, offset, ids, ct);
        };

        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(wsId);
        await Task.Yield();

        coordinator.RequestVisibleColumns(0, 10);
        await firstBlockStarted.Task;

        coordinator.RequestVisibleColumns(5 * 32, (5 * 32) + 10);
        coordinator.RequestVisibleColumns(10 * 32, (10 * 32) + 10);
        coordinator.RequestVisibleColumns(20 * 32, (20 * 32) + 10);

        await firstBlockCancelled.Task;
        await Task.Delay(20, Token);

        Assert.Equal(1, source.MaxConcurrentCalls);
        Assert.Equal(2, source.WindowCalls.Count);
        Assert.Contains(20, coordinator.CachedBlockIndices);
        Assert.DoesNotContain(5, coordinator.CachedBlockIndices);
        Assert.DoesNotContain(10, coordinator.CachedBlockIndices);
    }

    [Fact]
    public async Task StaleResponsesAreRejectedAfterInvalidateAndWorksheetSwitch()
    {
        var windowGate = new TaskCompletionSource<WorksheetColumnWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new StubQuerySource();
        var ws1 = source.SeedWorksheet(100, 500);
        var ws2 = source.SeedWorksheet(100, 500);

        source.OnLoadWindow = (id, offset, ids, ct) =>
            id == ws1 ? windowGate.Task : source.BuildWindowAsync(id, offset, ids, ct);

        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(ws1);
        await Task.Yield();

        var gen1 = coordinator.Generation;
        coordinator.RequestVisibleColumns(0, 5);
        Assert.True(coordinator.IsBusy);

        coordinator.InvalidateData();
        Assert.True(coordinator.Generation > gen1);
        Assert.Equal(0, coordinator.CachedBlockCount);

        coordinator.SetWorksheet(ws2);
        await Task.Yield();

        var staleWindow = await source.BuildWindowAsync(ws1, 0, [source.GetColumnIds(ws1)[0]], Token);
        windowGate.SetResult(staleWindow);
        await Task.Yield();

        Assert.Equal(ws2, coordinator.WorksheetId);
        Assert.Equal(0, coordinator.CachedBlockCount);

        var snapshot = coordinator.CreateRenderSnapshot(0, 5);
        Assert.Equal(ws2, snapshot.WorksheetId);
        Assert.False(snapshot.TryGetColumn(0, out _));
    }

    [Fact]
    public async Task QueryFailuresRecoverWithoutPermanentBusyStateAndAllowRetry()
    {
        var shouldFail = true;
        var source = new StubQuerySource();
        var wsId = source.SeedWorksheet(100, 500);
        source.OnLoadWindow = (id, offset, ids, ct) =>
        {
            if (shouldFail)
            {
                throw new RawDataStorageException("Simulated DuckDB read error");
            }

            return source.BuildWindowAsync(id, offset, ids, ct);
        };

        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(wsId);
        await Task.Yield();

        coordinator.RequestVisibleColumns(0, 5);
        await Task.Yield();

        Assert.False(coordinator.IsBusy);
        Assert.NotNull(coordinator.LastErrorMessage);
        Assert.Equal(0, coordinator.CachedBlockCount);

        var callsBefore = source.WindowCalls.Count;
        coordinator.RequestVisibleColumns(0, 5);
        await Task.Yield();
        Assert.Equal(callsBefore, source.WindowCalls.Count);

        shouldFail = false;
        coordinator.RetryFailed();
        await Task.Yield();

        Assert.False(coordinator.IsBusy);
        Assert.Null(coordinator.LastErrorMessage);
        Assert.Contains(0, coordinator.CachedBlockIndices);
    }

    [Fact]
    public async Task ProjectSessionClosedRetiresCoordinatorSafelyWithInFlightQuery()
    {
        var source = new StubQuerySource();
        var wsId = source.SeedWorksheet(100, 500);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.OnLoadWindow = async (_, _, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(5, ct);
            throw new ProjectSessionClosedException();
        };

        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(wsId);
        await Task.Yield();

        coordinator.RequestVisibleColumns(0, 5);
        await started.Task;
        await Task.Delay(25, Token);

        Assert.False(coordinator.IsBusy);
        Assert.Null(coordinator.LastErrorMessage);

        var callsAfterRetire = source.WindowCalls.Count;
        coordinator.RequestVisibleColumns(32, 40);
        await Task.Yield();
        Assert.Equal(callsAfterRetire, source.WindowCalls.Count);
    }

    [Fact]
    public async Task WideViewportExceedingCacheCapacityKeepsStrictBoundsWithoutBlocking()
    {
        var source = new StubQuerySource();
        var wsId = source.SeedWorksheet(1000, 500);
        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(wsId);
        await Task.Yield();

        for (var step = 0; step < 12; step++)
        {
            coordinator.RequestVisibleColumns(0, 399);
            await Task.Yield();
        }

        Assert.Equal(WorksheetViewportDataCoordinator.MaxCachedBlocks, coordinator.CachedBlockCount);
        Assert.False(coordinator.IsBusy);

        var snapshot = coordinator.CreateRenderSnapshot(0, 399);
        Assert.Equal(WorksheetViewportDataCoordinator.MaxCachedBlocks, snapshot.LoadedBlockCount);
        Assert.True(snapshot.TryGetColumn(0, out _));
        Assert.False(snapshot.TryGetColumn(350, out _));
    }

    [Fact]
    public async Task ResizePreservesValidCachedBlocksAndLoadsNewlyExposedColumns()
    {
        var source = new StubQuerySource();
        var wsId = source.SeedWorksheet(200, 500);
        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(wsId);
        await Task.Yield();

        coordinator.RequestVisibleColumns(0, 15);
        await Task.Yield();
        var callsAfterInitial = source.WindowCalls.Count;
        Assert.Contains(0, coordinator.CachedBlockIndices);

        coordinator.NotifyColumnsResized(0, 40);
        await Task.Yield();

        Assert.Equal(callsAfterInitial + 1, source.WindowCalls.Count);
        Assert.Contains(0, coordinator.CachedBlockIndices);
        Assert.Contains(1, coordinator.CachedBlockIndices);
    }

    [Theory]
    [InlineData(WorksheetDataType.Numeric)]
    [InlineData(WorksheetDataType.String)]
    public async Task BoundedCacheRetainsLowMemoryFootprintOn1000Columns(WorksheetDataType dataType)
    {
        var source = new StubQuerySource();
        var wsId = source.SeedWorksheet(1000, 500, dataType);
        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(wsId);
        await Task.Yield();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);

        for (var b = 0; b < 12; b++)
        {
            coordinator.RequestVisibleColumns(b * 32, (b * 32) + 15);
            await Task.Yield();
        }

        Assert.Equal(WorksheetViewportDataCoordinator.MaxCachedBlocks, coordinator.CachedBlockCount);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var retainedMb = (GC.GetTotalMemory(true) - heapBefore) / (1024.0 * 1024.0);

        Assert.True(retainedMb < 15.0, $"Retained heap {retainedMb:F2} MB exceeded 15 MB bound for {dataType}.");
    }
}
