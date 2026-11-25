using Avalonia;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.app.Views.WorksheetGrid;

internal readonly record struct VisibleRange(int Start, int EndExclusive)
{
    public int Count => Math.Max(0, EndExclusive - Start);
}

internal enum WorksheetHitArea
{
    None,
    Corner,
    RowNumberCell,
    ColumnHeader,
    ColumnResizeGrip,
    Cell
}

internal readonly record struct WorksheetHitResult(
    WorksheetHitArea Area,
    int RowIndex,
    int ColumnIndex);

// Immutable point-in-time geometry snapshot for the compositor/render thread.
internal sealed class WorksheetViewportGeometrySnapshot
{
    private readonly double[] _columnWidths;
    private readonly double[] _columnOffsets; // length = ColumnCount + 1

    internal WorksheetViewportGeometrySnapshot(double[] columnWidths, double[] columnOffsets)
    {
        _columnWidths = columnWidths;
        _columnOffsets = columnOffsets;
        ColumnCount = columnWidths.Length;
    }

    public int ColumnCount { get; }

    public double TotalDataWidth => _columnOffsets[ColumnCount];

    public double GetColumnWidth(int columnIndex) => _columnWidths[columnIndex];

    public double GetColumnLeftOffset(int columnIndex) => _columnOffsets[columnIndex];

    public double GetColumnRightOffset(int columnIndex) => _columnOffsets[columnIndex + 1];

    public VisibleRange GetVisibleRowRange(
        double scrollY,
        double viewportBodyHeight,
        int totalRows,
        int overscanRows = WorksheetViewportGeometry.DefaultOverscanRows)
    {
        if (totalRows <= 0 || viewportBodyHeight <= 0)
        {
            return new VisibleRange(0, 0);
        }

        var clampedScrollY = Math.Max(0, scrollY);
        var firstVisible = (int)Math.Floor(clampedScrollY / WorksheetViewportGeometry.RowHeight);
        var lastVisible = (int)Math.Floor((clampedScrollY + viewportBodyHeight - 0.001) / WorksheetViewportGeometry.RowHeight);

        var start = Math.Clamp(firstVisible - overscanRows, 0, totalRows);
        var end = Math.Clamp(lastVisible + 1 + overscanRows, 0, totalRows);
        return new VisibleRange(start, Math.Max(start, end));
    }

    public VisibleRange GetVisibleColumnRange(
        double scrollX,
        double viewportBodyWidth,
        int overscanColumns = WorksheetViewportGeometry.DefaultOverscanColumns)
    {
        if (ColumnCount == 0 || viewportBodyWidth <= 0)
        {
            return new VisibleRange(0, 0);
        }

        var clampedScrollX = Math.Max(0, scrollX);
        if (clampedScrollX >= TotalDataWidth)
        {
            return new VisibleRange(0, 0);
        }

        var firstVisible = FindColumnAtOffset(clampedScrollX);
        var rightEdge = Math.Min(TotalDataWidth - 0.001, clampedScrollX + viewportBodyWidth - 0.001);
        var lastVisible = rightEdge < clampedScrollX ? firstVisible : FindColumnAtOffset(rightEdge);

        var start = Math.Clamp(firstVisible - overscanColumns, 0, ColumnCount);
        var end = Math.Clamp(lastVisible + 1 + overscanColumns, 0, ColumnCount);
        return new VisibleRange(start, Math.Max(start, end));
    }

    public int FindColumnAtOffset(double contentX)
    {
        if (ColumnCount == 0 || contentX < 0 || contentX >= TotalDataWidth)
        {
            return -1;
        }

        var index = Array.BinarySearch(_columnOffsets, 0, ColumnCount, contentX);
        if (index >= 0)
        {
            return Math.Min(index, ColumnCount - 1);
        }

        var insertion = ~index;
        return Math.Clamp(insertion - 1, 0, ColumnCount - 1);
    }
}

// UI-thread-owned mutable layout geometry for the virtualized worksheet grid.
internal sealed class WorksheetViewportGeometry
{
    public const double RowNumberColumnWidth = 48.0;
    public const double HeaderHeight = 26.0;
    public const double RowHeight = 22.0;
    public const double MinColumnWidth = 48.0;
    public const double MaxColumnWidth = 400.0;
    public const int DefaultOverscanRows = 2;
    public const int DefaultOverscanColumns = 1;

    private const double CharacterWidth = 7.5;
    private const double HeaderChrome = 20.0;
    private const double NumericMinWidth = 88.0;
    private const double NumericMaxWidth = 160.0;
    private const double TextMinWidth = 96.0;
    private const double TextMaxWidth = 240.0;

    private readonly double[] _columnWidths;
    private readonly double[] _columnOffsets;
    private WorksheetViewportGeometrySnapshot? _cachedSnapshot;

    public WorksheetViewportGeometry(IReadOnlyList<double> initialColumnWidths)
    {
        ArgumentNullException.ThrowIfNull(initialColumnWidths);

        ColumnCount = initialColumnWidths.Count;
        _columnWidths = new double[ColumnCount];
        _columnOffsets = new double[ColumnCount + 1];

        for (var i = 0; i < ColumnCount; i++)
        {
            _columnWidths[i] = Math.Clamp(initialColumnWidths[i], MinColumnWidth, MaxColumnWidth);
            _columnOffsets[i + 1] = _columnOffsets[i] + _columnWidths[i];
        }
    }

    public static WorksheetViewportGeometry Empty { get; } = new([]);

    public static WorksheetViewportGeometry FromColumns(
        IReadOnlyList<WorksheetColumn> columns,
        IReadOnlyDictionary<Guid, double>? preservedWidths = null)
    {
        ArgumentNullException.ThrowIfNull(columns);

        var widths = new double[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var col = columns[i];
            widths[i] = preservedWidths is not null && preservedWidths.TryGetValue(col.Id, out var custom)
                ? custom
                : GetDefaultColumnWidth(col.Name, col.DataType);
        }

        return new WorksheetViewportGeometry(widths);
    }

    public static double GetDefaultColumnWidth(string name, WorksheetDataType dataType)
    {
        var (minimum, maximum) = dataType == WorksheetDataType.Numeric
            ? (NumericMinWidth, NumericMaxWidth)
            : (TextMinWidth, TextMaxWidth);

        return Math.Clamp(Math.Ceiling(name.Length * CharacterWidth + HeaderChrome), minimum, maximum);
    }

    public int ColumnCount { get; }

    public double TotalDataWidth => _columnOffsets[ColumnCount];

    public double GetTotalDataHeight(int rowCount) => Math.Max(0, rowCount) * RowHeight;

    public double GetColumnWidth(int columnIndex) => _columnWidths[columnIndex];

    public WorksheetViewportGeometrySnapshot CreateSnapshot()
    {
        if (_cachedSnapshot is { } cached)
        {
            return cached;
        }

        var snapshot = new WorksheetViewportGeometrySnapshot((double[])_columnWidths.Clone(), (double[])_columnOffsets.Clone());
        _cachedSnapshot = snapshot;
        return snapshot;
    }

    public double ResizeColumn(int columnIndex, double newWidth)
    {
        if ((uint)columnIndex >= (uint)ColumnCount)
        {
            throw new ArgumentOutOfRangeException(nameof(columnIndex));
        }

        var clamped = Math.Clamp(newWidth, MinColumnWidth, MaxColumnWidth);
        var delta = clamped - _columnWidths[columnIndex];
        if (delta == 0)
        {
            return clamped;
        }

        _columnWidths[columnIndex] = clamped;
        for (var i = columnIndex + 1; i <= ColumnCount; i++)
        {
            _columnOffsets[i] += delta;
        }

        _cachedSnapshot = null;
        return clamped;
    }

    public VisibleRange GetVisibleColumnRange(double scrollX, double viewportBodyWidth, int overscanColumns = DefaultOverscanColumns) =>
        CreateSnapshot().GetVisibleColumnRange(scrollX, viewportBodyWidth, overscanColumns);

    public WorksheetHitResult HitTest(
        Point point,
        double scrollX,
        double scrollY,
        int totalRows,
        double resizeGripHalfWidth = 4.0)
    {
        if (point.X < 0 || point.Y < 0)
        {
            return new WorksheetHitResult(WorksheetHitArea.None, -1, -1);
        }

        var inHeader = point.Y < HeaderHeight;
        var inRowNumber = point.X < RowNumberColumnWidth;

        if (inRowNumber)
        {
            if (inHeader)
            {
                return new WorksheetHitResult(WorksheetHitArea.Corner, -1, -1);
            }

            var row = (int)Math.Floor((point.Y - HeaderHeight + Math.Max(0, scrollY)) / RowHeight);
            return row >= 0 && row < totalRows
                ? new WorksheetHitResult(WorksheetHitArea.RowNumberCell, row, -1)
                : new WorksheetHitResult(WorksheetHitArea.None, -1, -1);
        }

        var snapshot = CreateSnapshot();
        var contentX = (point.X - RowNumberColumnWidth) + Math.Max(0, scrollX);

        if (inHeader)
        {
            var candidate = snapshot.FindColumnAtOffset(contentX);
            if (candidate >= 0)
            {
                var rightEdge = _columnOffsets[candidate + 1];
                if (Math.Abs(contentX - rightEdge) <= resizeGripHalfWidth)
                {
                    return new WorksheetHitResult(WorksheetHitArea.ColumnResizeGrip, -1, candidate);
                }
            }

            if (candidate > 0)
            {
                var prevRightEdge = _columnOffsets[candidate];
                if (Math.Abs(contentX - prevRightEdge) <= resizeGripHalfWidth)
                {
                    return new WorksheetHitResult(WorksheetHitArea.ColumnResizeGrip, -1, candidate - 1);
                }
            }
            else if (candidate == -1 && ColumnCount > 0 && Math.Abs(contentX - TotalDataWidth) <= resizeGripHalfWidth)
            {
                return new WorksheetHitResult(WorksheetHitArea.ColumnResizeGrip, -1, ColumnCount - 1);
            }

            return candidate >= 0
                ? new WorksheetHitResult(WorksheetHitArea.ColumnHeader, -1, candidate)
                : new WorksheetHitResult(WorksheetHitArea.None, -1, -1);
        }

        var cellCol = snapshot.FindColumnAtOffset(contentX);
        var cellRow = (int)Math.Floor((point.Y - HeaderHeight + Math.Max(0, scrollY)) / RowHeight);
        if (cellCol >= 0 && cellRow >= 0 && cellRow < totalRows)
        {
            return new WorksheetHitResult(WorksheetHitArea.Cell, cellRow, cellCol);
        }

        return new WorksheetHitResult(WorksheetHitArea.None, -1, -1);
    }
}
