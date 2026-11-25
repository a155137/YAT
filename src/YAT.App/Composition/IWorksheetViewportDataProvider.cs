using YAT.Application.Queries;
using YAT.Domain.Entities;

namespace YAT.app.Composition;

// Immutable cached block of up to BlockColumnCount (32) typed columns for a single worksheet page.
public sealed class WorksheetCachedColumnBlock
{
    internal WorksheetCachedColumnBlock(
        int blockIndex,
        int startColumnIndex,
        IReadOnlyList<WorksheetDataPageColumn> columns)
    {
        BlockIndex = blockIndex;
        StartColumnIndex = startColumnIndex;
        Columns = columns;
    }

    public int BlockIndex { get; }

    public int StartColumnIndex { get; }

    public IReadOnlyList<WorksheetDataPageColumn> Columns { get; }
}

// Immutable point-in-time snapshot handed to the renderer on the UI thread.
// Contains only currently valid, cached blocks; missing blocks are left blank without blocking.
public sealed class WorksheetViewportRenderSnapshot
{
    public static WorksheetViewportRenderSnapshot Empty { get; } =
        new(Guid.Empty, 0, 0, 0, [], []);

    private readonly IReadOnlyList<WorksheetCachedColumnBlock> _blocks;

    internal WorksheetViewportRenderSnapshot(
        Guid worksheetId,
        long rowOffset,
        int pageRowCount,
        long generation,
        IReadOnlyList<WorksheetColumn> columns,
        IReadOnlyList<WorksheetCachedColumnBlock> blocks)
    {
        WorksheetId = worksheetId;
        RowOffset = rowOffset;
        PageRowCount = pageRowCount;
        Generation = generation;
        Columns = columns;
        _blocks = blocks;
    }

    public Guid WorksheetId { get; }

    public long RowOffset { get; }

    public int PageRowCount { get; }

    public long Generation { get; }

    public IReadOnlyList<WorksheetColumn> Columns { get; }

    public int LoadedBlockCount => _blocks.Count;

    public bool TryGetColumn(int columnIndex, out WorksheetDataPageColumn? column)
    {
        if ((uint)columnIndex >= (uint)Columns.Count)
        {
            column = null;
            return false;
        }

        var blockIndex = columnIndex / WorksheetViewportDataCoordinator.BlockColumnCount;
        for (var i = 0; i < _blocks.Count; i++)
        {
            var block = _blocks[i];
            if (block.BlockIndex == blockIndex)
            {
                var localIndex = columnIndex - block.StartColumnIndex;
                if ((uint)localIndex < (uint)block.Columns.Count)
                {
                    column = block.Columns[localIndex];
                    return true;
                }

                break;
            }
        }

        column = null;
        return false;
    }
}

// Read-only viewport data contract consumed by the worksheet view/renderer.
public interface IWorksheetViewportDataProvider
{
    Guid WorksheetId { get; }

    long RowOffset { get; }

    long TotalRowCount { get; }

    int PageRowCount { get; }

    IReadOnlyList<WorksheetColumn> Columns { get; }

    bool IsMetadataLoaded { get; }

    bool IsBusy { get; }

    string? LastErrorMessage { get; }

    event EventHandler? ViewportDataChanged;

    void RequestVisibleColumns(int firstVisibleColumnIndex, int lastVisibleColumnIndexInclusive);

    WorksheetViewportRenderSnapshot CreateRenderSnapshot(int firstVisibleColumnIndex, int lastVisibleColumnIndexInclusive);
}
