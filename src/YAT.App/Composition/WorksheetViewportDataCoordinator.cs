using YAT.Application.Exceptions;
using YAT.Application.Queries;
using YAT.Domain.Entities;

namespace YAT.app.Composition;

internal interface IWorksheetViewportQuerySource
{
    Task<WorksheetPageMetadata> LoadPageMetadataAsync(Guid worksheetId, long rowOffset, CancellationToken cancellationToken);

    Task<WorksheetColumnWindow> LoadColumnWindowAsync(
        Guid worksheetId,
        long rowOffset,
        IReadOnlyList<Guid> columnIds,
        CancellationToken cancellationToken);
}

internal sealed class MainWindowSessionViewportQuerySource : IWorksheetViewportQuerySource
{
    private readonly MainWindowSession _session;

    public MainWindowSessionViewportQuerySource(MainWindowSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<WorksheetPageMetadata> LoadPageMetadataAsync(Guid worksheetId, long rowOffset, CancellationToken cancellationToken) =>
        _session.LoadWorksheetPageMetadataAsync(worksheetId, rowOffset, cancellationToken);

    public Task<WorksheetColumnWindow> LoadColumnWindowAsync(
        Guid worksheetId,
        long rowOffset,
        IReadOnlyList<Guid> columnIds,
        CancellationToken cancellationToken) =>
        _session.LoadWorksheetColumnWindowAsync(worksheetId, rowOffset, columnIds, cancellationToken);
}

// Coordinates bounded row/column-window loading for a single active worksheet page on the UI thread.
// Enforces BlockColumnCount = 32, MaxCachedBlocks = 8 (256 columns max), single-flight async queries
// with at most 1 pending viewport request, generation-based stale rejection, and safe retirement.
public sealed class WorksheetViewportDataCoordinator : IWorksheetViewportDataProvider, IDisposable
{
    public const int BlockColumnCount = 32;
    public const int MaxCachedBlocks = 8;

    private readonly IWorksheetViewportQuerySource _querySource;
    private readonly Dictionary<int, CacheEntry> _cache = new(MaxCachedBlocks);
    private readonly LinkedList<int> _lru = new();
    private readonly HashSet<int> _failedBlocks = [];

    private long _generation;
    private Guid _worksheetId;
    private long _rowOffset;
    private WorksheetPageMetadata? _metadata;
    private bool _isMetadataLoading;
    private int? _activeBlockIndex;
    private CancellationTokenSource? _activeCts;
    private (int FirstCol, int LastCol)? _pendingViewport;
    private int _protectedFirstBlock = -1;
    private int _protectedLastBlock = -1;
    private bool _retired;
    private bool _disposed;

    public WorksheetViewportDataCoordinator(MainWindowSession session)
        : this(new MainWindowSessionViewportQuerySource(session))
    {
    }

    internal WorksheetViewportDataCoordinator(IWorksheetViewportQuerySource querySource)
    {
        _querySource = querySource ?? throw new ArgumentNullException(nameof(querySource));
    }

    public event EventHandler? ViewportDataChanged;

    public Guid WorksheetId => _worksheetId;

    public long RowOffset => _rowOffset;

    public long TotalRowCount => _metadata?.TotalRowCount ?? 0;

    public int PageRowCount => _metadata?.RowCount ?? 0;

    public IReadOnlyList<WorksheetColumn> Columns => _metadata?.Columns ?? [];

    public bool IsMetadataLoaded => _metadata is not null;

    public bool IsBusy => _isMetadataLoading || _activeBlockIndex is not null;

    public string? LastErrorMessage { get; private set; }

    // Diagnostic/test inspection properties (UI-thread only).
    internal long Generation => _generation;

    internal int CachedBlockCount => _cache.Count;

    internal int? ActiveBlockIndex => _activeBlockIndex;

    internal bool HasPendingViewport => _pendingViewport is not null;

    internal IReadOnlyCollection<int> CachedBlockIndices => _cache.Keys;

    public void SetWorksheet(Guid worksheetId, long rowOffset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);
        if (_disposed || _retired)
        {
            return;
        }

        if (_worksheetId == worksheetId && _rowOffset == rowOffset && (_metadata is not null || _isMetadataLoading))
        {
            return;
        }

        ResetAndLoadMetadata(worksheetId, rowOffset);
    }

    public void SetPage(long rowOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);
        if (_disposed || _retired || _worksheetId == Guid.Empty)
        {
            return;
        }

        if (_rowOffset == rowOffset && (_metadata is not null || _isMetadataLoading))
        {
            return;
        }

        ResetAndLoadMetadata(_worksheetId, rowOffset);
    }

    // Called after paste, column add/delete, or project/worksheet data mutation.
    public void InvalidateData()
    {
        if (_disposed || _retired)
        {
            return;
        }

        if (_worksheetId == Guid.Empty)
        {
            Clear();
            return;
        }

        ResetAndLoadMetadata(_worksheetId, _rowOffset);
    }

    // Called when column widths change via user resize: preserves all valid cached blocks.
    public void NotifyColumnsResized(int firstVisibleColumnIndex, int lastVisibleColumnIndexInclusive)
    {
        if (_disposed || _retired)
        {
            return;
        }

        RequestVisibleColumns(firstVisibleColumnIndex, lastVisibleColumnIndexInclusive);
    }

    public void Clear()
    {
        _generation++;
        CancelActiveQuery();
        _worksheetId = Guid.Empty;
        _rowOffset = 0;
        _metadata = null;
        _isMetadataLoading = false;
        _pendingViewport = null;
        _protectedFirstBlock = -1;
        _protectedLastBlock = -1;
        _cache.Clear();
        _lru.Clear();
        _failedBlocks.Clear();
        LastErrorMessage = null;
        ViewportDataChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RetryFailed()
    {
        if (_disposed || _retired)
        {
            return;
        }

        _failedBlocks.Clear();
        LastErrorMessage = null;
        if (_worksheetId != Guid.Empty && _metadata is null && !_isMetadataLoading)
        {
            _ = StartMetadataLoadAsync(_worksheetId, _rowOffset, _generation);
            return;
        }

        if (_pendingViewport is { } pending)
        {
            ScheduleFromViewport(pending.FirstCol, pending.LastCol);
        }
        else if (_protectedFirstBlock >= 0 && _protectedLastBlock >= _protectedFirstBlock && _metadata is not null)
        {
            var firstCol = _protectedFirstBlock * BlockColumnCount;
            var lastCol = Math.Min(_metadata.Columns.Count - 1, ((_protectedLastBlock + 1) * BlockColumnCount) - 1);
            if (firstCol <= lastCol)
            {
                ScheduleFromViewport(firstCol, lastCol);
            }
        }
    }

    public void RequestVisibleColumns(int firstVisibleColumnIndex, int lastVisibleColumnIndexInclusive)
    {
        if (_disposed || _retired || _worksheetId == Guid.Empty)
        {
            return;
        }

        if (lastVisibleColumnIndexInclusive < firstVisibleColumnIndex)
        {
            return;
        }

        if (_metadata is null)
        {
            _pendingViewport = (Math.Max(0, firstVisibleColumnIndex), Math.Max(0, lastVisibleColumnIndexInclusive));
            return;
        }

        if (_metadata.Columns.Count == 0 || _metadata.RowCount == 0)
        {
            _pendingViewport = null;
            return;
        }

        var clampedFirst = Math.Clamp(firstVisibleColumnIndex, 0, _metadata.Columns.Count - 1);
        var clampedLast = Math.Clamp(lastVisibleColumnIndexInclusive, clampedFirst, _metadata.Columns.Count - 1);

        ScheduleFromViewport(clampedFirst, clampedLast);
    }

    public WorksheetViewportRenderSnapshot CreateRenderSnapshot(int firstVisibleColumnIndex, int lastVisibleColumnIndexInclusive)
    {
        if (_metadata is null || _metadata.Columns.Count == 0)
        {
            return WorksheetViewportRenderSnapshot.Empty;
        }

        if (lastVisibleColumnIndexInclusive < firstVisibleColumnIndex)
        {
            return new WorksheetViewportRenderSnapshot(
                _metadata.WorksheetId,
                _metadata.RowOffset,
                _metadata.RowCount,
                _generation,
                _metadata.Columns,
                []);
        }

        var clampedFirst = Math.Clamp(firstVisibleColumnIndex, 0, _metadata.Columns.Count - 1);
        var clampedLast = Math.Clamp(lastVisibleColumnIndexInclusive, clampedFirst, _metadata.Columns.Count - 1);
        var firstBlock = clampedFirst / BlockColumnCount;
        var lastBlock = clampedLast / BlockColumnCount;

        var snapshotBlocks = new List<WorksheetCachedColumnBlock>(Math.Min(MaxCachedBlocks, (lastBlock - firstBlock) + 1));
        for (var blockIndex = firstBlock; blockIndex <= lastBlock; blockIndex++)
        {
            if (_cache.TryGetValue(blockIndex, out var entry))
            {
                snapshotBlocks.Add(entry.Block);
            }
        }

        return new WorksheetViewportRenderSnapshot(
            _metadata.WorksheetId,
            _metadata.RowOffset,
            _metadata.RowCount,
            _generation,
            _metadata.Columns,
            snapshotBlocks);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _generation++;
        CancelActiveQuery();
        _cache.Clear();
        _lru.Clear();
        _failedBlocks.Clear();
        _pendingViewport = null;
    }

    private void ResetAndLoadMetadata(Guid worksheetId, long rowOffset)
    {
        _generation++;
        CancelActiveQuery();

        _worksheetId = worksheetId;
        _rowOffset = rowOffset;
        _metadata = null;
        _isMetadataLoading = false;
        _activeBlockIndex = null;
        _pendingViewport = null;
        _protectedFirstBlock = -1;
        _protectedLastBlock = -1;
        _cache.Clear();
        _lru.Clear();
        _failedBlocks.Clear();
        LastErrorMessage = null;

        ViewportDataChanged?.Invoke(this, EventArgs.Empty);

        if (worksheetId != Guid.Empty)
        {
            _ = StartMetadataLoadAsync(worksheetId, rowOffset, _generation);
        }
    }

    private async Task StartMetadataLoadAsync(Guid worksheetId, long rowOffset, long generation)
    {
        var cts = new CancellationTokenSource();
        _activeCts = cts;
        _isMetadataLoading = true;

        try
        {
            var metadata = await _querySource.LoadPageMetadataAsync(worksheetId, rowOffset, cts.Token);
            if (_disposed || _retired || generation != _generation)
            {
                return;
            }

            _metadata = metadata;
            LastErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (ProjectSessionClosedException)
        {
            _retired = true;
            _pendingViewport = null;
            return;
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException or RawDataStorageException or ProjectStorageException)
        {
            if (generation == _generation && !_disposed && !_retired)
            {
                LastErrorMessage = ex is RawDataStorageException
                    ? "The worksheet data could not be loaded."
                    : ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_activeCts, cts))
            {
                _activeCts = null;
                _isMetadataLoading = false;
            }

            cts.Dispose();
        }

        if (_disposed || _retired || generation != _generation)
        {
            return;
        }

        var pending = _pendingViewport;
        _pendingViewport = null;
        ViewportDataChanged?.Invoke(this, EventArgs.Empty);

        if (!_disposed && !_retired && generation == _generation && !IsBusy && pending is { } p
            && _metadata is not null && _metadata.Columns.Count > 0 && _metadata.RowCount > 0)
        {
            RequestVisibleColumns(p.FirstCol, p.LastCol);
        }
    }

    private void ScheduleFromViewport(int firstCol, int lastCol)
    {
        var firstBlock = firstCol / BlockColumnCount;
        var lastBlock = lastCol / BlockColumnCount;

        // If the visible range exceeds cache capacity, protect at most MaxCachedBlocks from the start of the range
        // to prevent infinite eviction/re-fetch thrashing.
        var effectiveLastBlock = Math.Min(lastBlock, firstBlock + MaxCachedBlocks - 1);
        _protectedFirstBlock = firstBlock;
        _protectedLastBlock = effectiveLastBlock;

        for (var b = firstBlock; b <= effectiveLastBlock; b++)
        {
            if (_cache.TryGetValue(b, out var entry))
            {
                TouchLru(entry);
            }
        }

        var nextBlock = FindNextMissingBlock(firstBlock, effectiveLastBlock);
        if (nextBlock is null)
        {
            _pendingViewport = null;
            return;
        }

        if (_isMetadataLoading || _activeBlockIndex is not null)
        {
            _pendingViewport = (firstCol, lastCol);

            // Cancel stale in-flight block query if user scrolled completely past it.
            if (_activeBlockIndex is { } active && (active < firstBlock || active > effectiveLastBlock))
            {
                _activeCts?.Cancel();
            }

            return;
        }

        _pendingViewport = (firstCol, lastCol);
        _ = StartBlockLoadAsync(nextBlock.Value, _generation);
    }

    private int? FindNextMissingBlock(int firstBlock, int lastBlock)
    {
        for (var b = firstBlock; b <= lastBlock; b++)
        {
            if (!_cache.ContainsKey(b) && !_failedBlocks.Contains(b))
            {
                return b;
            }
        }

        return null;
    }

    private async Task StartBlockLoadAsync(int blockIndex, long generation)
    {
        if (_metadata is null)
        {
            return;
        }

        var startCol = blockIndex * BlockColumnCount;
        if (startCol >= _metadata.Columns.Count)
        {
            return;
        }

        var count = Math.Min(BlockColumnCount, _metadata.Columns.Count - startCol);
        var columnIds = new Guid[count];
        for (var i = 0; i < count; i++)
        {
            columnIds[i] = _metadata.Columns[startCol + i].Id;
        }

        var cts = new CancellationTokenSource();
        _activeCts = cts;
        _activeBlockIndex = blockIndex;

        try
        {
            var window = await _querySource.LoadColumnWindowAsync(
                _metadata.WorksheetId,
                _metadata.RowOffset,
                columnIds,
                cts.Token);

            if (_disposed || _retired || generation != _generation || _metadata is null)
            {
                return;
            }

            InsertBlock(new WorksheetCachedColumnBlock(blockIndex, startCol, window.Columns));
            _failedBlocks.Remove(blockIndex);
            LastErrorMessage = null;
        }
        catch (OperationCanceledException)
        {
            // Cancelled due to rapid scrolling or invalidation; fall through to process latest pending viewport.
        }
        catch (ProjectSessionClosedException)
        {
            _retired = true;
            _pendingViewport = null;
            return;
        }
        catch (Exception ex) when (ex is ValidationException or EntityNotFoundException or RawDataStorageException or ProjectStorageException)
        {
            if (generation == _generation && !_disposed && !_retired)
            {
                _failedBlocks.Add(blockIndex);
                LastErrorMessage = ex is RawDataStorageException
                    ? "The worksheet data could not be loaded."
                    : ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_activeCts, cts))
            {
                _activeCts = null;
                _activeBlockIndex = null;
            }

            cts.Dispose();
        }

        if (_disposed || _retired || generation != _generation)
        {
            return;
        }

        var pending = _pendingViewport;
        _pendingViewport = null;
        ViewportDataChanged?.Invoke(this, EventArgs.Empty);

        if (!_disposed && !_retired && generation == _generation && !IsBusy && pending is { } p)
        {
            RequestVisibleColumns(p.FirstCol, p.LastCol);
        }
    }

    private void InsertBlock(WorksheetCachedColumnBlock block)
    {
        if (_cache.TryGetValue(block.BlockIndex, out var existing))
        {
            existing.Block = block;
            TouchLru(existing);
            return;
        }

        while (_cache.Count >= MaxCachedBlocks)
        {
            EvictOneBlock();
        }

        var node = _lru.AddFirst(block.BlockIndex);
        _cache[block.BlockIndex] = new CacheEntry(block, node);
    }

    private void EvictOneBlock()
    {
        // Prefer evicting the least-recently-used block outside the currently protected visible range.
        var node = _lru.Last;
        while (node is not null)
        {
            var candidate = node.Value;
            if (candidate < _protectedFirstBlock || candidate > _protectedLastBlock)
            {
                _lru.Remove(node);
                _cache.Remove(candidate);
                return;
            }

            node = node.Previous;
        }

        // If all cached blocks fall within a very wide visible range, evict the LRU block to preserve strict bounds.
        if (_lru.Last is { } fallback)
        {
            _lru.RemoveLast();
            _cache.Remove(fallback.Value);
        }
    }

    private void TouchLru(CacheEntry entry)
    {
        if (!ReferenceEquals(entry.LruNode, _lru.First))
        {
            _lru.Remove(entry.LruNode);
            _lru.AddFirst(entry.LruNode);
        }
    }

    private void CancelActiveQuery()
    {
        if (_activeCts is not null)
        {
            try
            {
                _activeCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(WorksheetCachedColumnBlock block, LinkedListNode<int> lruNode)
        {
            Block = block;
            LruNode = lruNode;
        }

        public WorksheetCachedColumnBlock Block { get; set; }

        public LinkedListNode<int> LruNode { get; }
    }
}
