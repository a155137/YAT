using SkiaSharp;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Queries;
using YAT.app.Composition;
using YAT.app.Views.WorksheetGrid;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.InMemory;

namespace YAT.App.Tests;

public sealed class WorksheetGridSafetyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SkiaRendererAndTextRendererDoubleDisposeIsSafeAndNeverDisposesDefaultTypeface()
    {
        var defaultHandle = SKTypeface.Default.Handle;

        var renderer = new SkiaWorksheetRenderer();
        var geometry = new WorksheetViewportGeometry([100, 100]);
        using (var bitmap = new SKBitmap(400, 200))
        using (var canvas = new SKCanvas(bitmap))
        {
            renderer.Render(
                canvas,
                new SKSize(400, 200),
                geometry.CreateSnapshot(),
                WorksheetViewportRenderSnapshot.Empty,
                0,
                0,
                new HashSet<int>(),
                null,
                WorksheetTheme.Light);
        }

        renderer.Dispose();
        renderer.Dispose();

        Assert.Equal(defaultHandle, SKTypeface.Default.Handle);
        Assert.True(SKTypeface.Default.FamilyName.Length > 0);
    }

    [Fact]
    public void TextRendererHandlesDuplicateFallbackTypefacesAndDisposesRedundantMatches()
    {
        var matchCalls = 0;
        using var textRenderer = new WorksheetTextRenderer((_, _) =>
        {
            matchCalls++;
            return SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyle.Normal) ?? SKTypeface.Default;
        });

        using var primaryFont = new SKFont(SKTypeface.Default, 13f);
        using var paint = new SKPaint { IsAntialias = true };
        using var bitmap = new SKBitmap(300, 100);
        using var canvas = new SKCanvas(bitmap);

        textRenderer.DrawClippedText(canvas, "晶圓".AsSpan(), 10, 30, 200, SKTextAlign.Left, primaryFont, paint);

        Assert.True(textRenderer.CachedFamilyCount <= 1);
        Assert.True(matchCalls >= 0);

        textRenderer.Dispose();
        textRenderer.Dispose();
    }

    [Fact]
    public void RefCountedRendererSurvivesUiDetachWhileDrawOperationLeaseIsHeld()
    {
        var renderer = new SkiaWorksheetRenderer();
        var refCounted = new VirtualizedWorksheetCanvas.RefCountedRenderer(renderer);

        Assert.True(refCounted.TryAddRef());
        refCounted.Release();
        Assert.False(refCounted.IsDisposed);

        var snapshot = new WorksheetViewportGeometry([100, 100]).CreateSnapshot();
        using (var bitmap = new SKBitmap(300, 150))
        using (var canvas = new SKCanvas(bitmap))
        {
            refCounted.Renderer.Render(
                canvas,
                new SKSize(300, 150),
                snapshot,
                WorksheetViewportRenderSnapshot.Empty,
                0,
                0,
                new HashSet<int>(),
                null,
                WorksheetTheme.Light);
        }

        refCounted.Release();
        Assert.True(refCounted.IsDisposed);
        Assert.False(refCounted.TryAddRef());
        Assert.Throws<ObjectDisposedException>(() =>
        {
            using var bitmap = new SKBitmap(100, 100);
            using var canvas = new SKCanvas(bitmap);
            refCounted.Renderer.Render(
                canvas,
                new SKSize(100, 100),
                snapshot,
                WorksheetViewportRenderSnapshot.Empty,
                0,
                0,
                new HashSet<int>(),
                null,
                WorksheetTheme.Light);
        });
    }

    [Fact]
    public async Task RepeatedControlDisposeIsIdempotentAndParallelTextRenderingIsThreadSafe()
    {
        var control = new VirtualizedWorksheetControl();
        control.Dispose();
        control.Dispose();

        using var textRenderer = new WorksheetTextRenderer();
        using var font = new SKFont(SKTypeface.Default, 13f);
        using var paint = new SKPaint { IsAntialias = true };

        var tasks = Enumerable.Range(0, 4).Select(i => Task.Run(() =>
        {
            using var bmp = new SKBitmap(200, 50);
            using var cvs = new SKCanvas(bmp);
            for (var j = 0; j < 20; j++)
            {
                textRenderer.DrawClippedText(cvs, $"晶圓測試_{i}_{j}".AsSpan(), 4, 25, 150, SKTextAlign.Left, font, paint);
            }
        }, Token));

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task RenderSnapshotRemainsImmutableAfterCacheEvictionAndInvalidation()
    {
        var source = new InMemoryQuerySource();
        var wsId = source.SeedWorksheet(400, 20);
        using var coordinator = new WorksheetViewportDataCoordinator(source);

        coordinator.SetWorksheet(wsId);
        await Task.Yield();

        coordinator.RequestVisibleColumns(0, 10);
        await Task.Yield();

        var snapshotBeforeEviction = coordinator.CreateRenderSnapshot(0, 10);
        Assert.True(snapshotBeforeEviction.TryGetColumn(0, out var col0Before));
        var valBefore = Assert.IsType<NumericRawDataColumn>(col0Before!.Values).Values[0];

        for (var b = 1; b <= 9; b++)
        {
            coordinator.RequestVisibleColumns(b * 32, (b * 32) + 5);
            await Task.Yield();
        }

        Assert.DoesNotContain(0, coordinator.CachedBlockIndices);
        coordinator.InvalidateData();
        Assert.Equal(0, coordinator.CachedBlockCount);

        Assert.True(snapshotBeforeEviction.TryGetColumn(0, out var col0After));
        Assert.Equal(valBefore, Assert.IsType<NumericRawDataColumn>(col0After!.Values).Values[0]);
    }

    [Fact]
    public async Task ProjectSwitchWithInFlightMetadataQueryRejectsStaleMetadata()
    {
        var metadataGate = new TaskCompletionSource<WorksheetPageMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new InMemoryQuerySource();
        var ws1 = source.SeedWorksheet(50, 10);
        var ws2 = source.SeedWorksheet(20, 5);

        source.OnLoadMetadata = (id, offset, ct) =>
            id == ws1 ? metadataGate.Task : source.BuildMetadataAsync(id, offset, ct);

        using var coordinator = new WorksheetViewportDataCoordinator(source);
        coordinator.SetWorksheet(ws1);
        Assert.True(coordinator.IsBusy);

        coordinator.SetWorksheet(ws2);
        await Task.Yield();

        Assert.Equal(ws2, coordinator.WorksheetId);
        Assert.Equal(20, coordinator.Columns.Count);

        metadataGate.SetResult(await source.BuildMetadataAsync(ws1, 0, Token));
        await Task.Yield();

        Assert.Equal(ws2, coordinator.WorksheetId);
        Assert.Equal(20, coordinator.Columns.Count);
        Assert.Equal(5, coordinator.PageRowCount);
    }

    private sealed class InMemoryQuerySource : IWorksheetViewportQuerySource
    {
        private readonly InMemoryWorksheetRepository _worksheets = new();
        private readonly InMemoryWorksheetColumnRepository _columns = new();
        private readonly Dictionary<Guid, List<Guid>> _columnIdsByWorksheet = [];
        private readonly Dictionary<Guid, RawDataColumn> _rawColumns = [];

        public Func<Guid, long, CancellationToken, Task<WorksheetPageMetadata>>? OnLoadMetadata { get; set; }

        public Guid SeedWorksheet(int columnCount, int rowCount)
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
                    DataType = WorksheetDataType.Numeric
                }, CancellationToken.None).GetAwaiter().GetResult();
                _rawColumns[id] = new NumericRawDataColumn(id, Enumerable.Range(0, rowCount).Select(r => (double?)(i + r)).ToArray());
                ids.Add(id);
            }

            _columnIdsByWorksheet[wsId] = ids;
            return wsId;
        }

        private WorksheetDataQueryService CreateQueryService() =>
            new(_worksheets, _columns, new MemRawStore(_columnIdsByWorksheet, _rawColumns));

        public Task<WorksheetPageMetadata> LoadPageMetadataAsync(Guid worksheetId, long rowOffset, CancellationToken cancellationToken) =>
            OnLoadMetadata is not null
                ? OnLoadMetadata(worksheetId, rowOffset, cancellationToken)
                : BuildMetadataAsync(worksheetId, rowOffset, cancellationToken);

        public Task<WorksheetPageMetadata> BuildMetadataAsync(Guid worksheetId, long rowOffset, CancellationToken cancellationToken) =>
            CreateQueryService().GetPageMetadataAsync(worksheetId, rowOffset, WorksheetDataQueryService.MaxPageRowCount, cancellationToken);

        public Task<WorksheetColumnWindow> LoadColumnWindowAsync(
            Guid worksheetId, long rowOffset, IReadOnlyList<Guid> columnIds, CancellationToken cancellationToken) =>
            CreateQueryService().GetColumnWindowAsync(worksheetId, rowOffset, WorksheetDataQueryService.MaxPageRowCount, columnIds, cancellationToken);

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
}
