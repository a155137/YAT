using System.Globalization;
using SkiaSharp;
using YAT.Application.Abstractions.Persistence;
using YAT.app.Composition;
using YAT.Domain.Enums;

namespace YAT.app.Views.WorksheetGrid;

internal readonly record struct WorksheetGridRenderStats(
    int RenderedCellCount,
    int RenderedHeaderCount,
    int RenderedRowNumberCount,
    int MissingBlockCellCount);

// Stateless-per-frame SkiaSharp renderer consuming immutable WorksheetViewportGeometrySnapshot
// and immutable WorksheetViewportRenderSnapshot.
internal sealed class SkiaWorksheetRenderer : IDisposable
{
    private const float FontSize = 13f;
    private const float CellHorizontalPadding = 6f;

    private readonly HashSet<SKTypeface> _ownedTypefaces = new(ReferenceEqualityComparer.Instance);
    private readonly SKTypeface _regularTypeface;
    private readonly SKTypeface _semiBoldTypeface;
    private readonly SKFont _cellFont;
    private readonly SKFont _headerFont;
    private readonly SKFont _selectedHeaderFont;
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _strokePaint;
    private readonly SKPaint _textPaint;
    private readonly WorksheetTextRenderer _textRenderer = new();
    private int _disposed;
    private int _lastRenderedCells;
    private int _lastRenderedHeaders;
    private int _lastRenderedRowNumbers;
    private int _lastMissingCells;

    public SkiaWorksheetRenderer()
    {
        var interRegular = SKTypeface.FromFamilyName("Inter", SKFontStyle.Normal);
        _regularTypeface = TrackOwned(interRegular) ?? SKTypeface.Default;

        var interSemiBold = SKTypeface.FromFamilyName("Inter", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        _semiBoldTypeface = TrackOwned(interSemiBold) ?? _regularTypeface;

        _cellFont = new SKFont(_regularTypeface, FontSize) { Subpixel = true };
        _headerFont = new SKFont(_regularTypeface, FontSize) { Subpixel = true };
        _selectedHeaderFont = new SKFont(_semiBoldTypeface, FontSize) { Subpixel = true };

        _fillPaint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        _strokePaint = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1f };
        _textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
    }

    public WorksheetGridRenderStats LastStats => new(
        Volatile.Read(ref _lastRenderedCells),
        Volatile.Read(ref _lastRenderedHeaders),
        Volatile.Read(ref _lastRenderedRowNumbers),
        Volatile.Read(ref _lastMissingCells));

    private SKTypeface? TrackOwned(SKTypeface? typeface)
    {
        if (typeface is not null && !ReferenceEquals(typeface, SKTypeface.Default))
        {
            _ownedTypefaces.Add(typeface);
        }

        return typeface;
    }

    public void Render(
        SKCanvas canvas,
        SKSize viewportSize,
        WorksheetViewportGeometrySnapshot geometry,
        WorksheetViewportRenderSnapshot snapshot,
        double scrollX,
        double scrollY,
        IReadOnlySet<int> selectedColumns,
        int? activeColumn,
        WorksheetTheme theme,
        int overscanRows = WorksheetViewportGeometry.DefaultOverscanRows,
        int overscanColumns = WorksheetViewportGeometry.DefaultOverscanColumns)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(geometry);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selectedColumns);
        ArgumentNullException.ThrowIfNull(theme);

        if (viewportSize.Width <= 0 || viewportSize.Height <= 0)
        {
            WriteStats(0, 0, 0, 0);
            return;
        }

        const float rowNumWidth = (float)WorksheetViewportGeometry.RowNumberColumnWidth;
        const float headerHeight = (float)WorksheetViewportGeometry.HeaderHeight;
        const float rowHeight = (float)WorksheetViewportGeometry.RowHeight;

        _fillPaint.Color = theme.Background;
        canvas.DrawRect(0, 0, viewportSize.Width, viewportSize.Height, _fillPaint);

        var bodyWidth = Math.Max(0, viewportSize.Width - rowNumWidth);
        var bodyHeight = Math.Max(0, viewportSize.Height - headerHeight);

        var rowCount = snapshot.PageRowCount;
        var visibleRows = geometry.GetVisibleRowRange(scrollY, bodyHeight, rowCount, overscanRows);
        var visibleCols = geometry.GetVisibleColumnRange(scrollX, bodyWidth, overscanColumns);
        var effectiveColEnd = Math.Min(visibleCols.EndExclusive, snapshot.Columns.Count);
        var hasVisibleCols = visibleCols.Start < effectiveColEnd;

        Span<char> numericScratch = stackalloc char[64];
        var cellMetrics = _cellFont.Metrics;
        var cellTextOffsetY = ((rowHeight - (cellMetrics.Descent - cellMetrics.Ascent)) / 2f) - cellMetrics.Ascent;
        var headerTextOffsetY = ((headerHeight - (cellMetrics.Descent - cellMetrics.Ascent)) / 2f) - cellMetrics.Ascent;

        var renderedCells = 0;
        var missingCells = 0;

        // 1. Body cells
        if (bodyWidth > 0 && bodyHeight > 0 && visibleRows.Count > 0 && hasVisibleCols)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(rowNumWidth, headerHeight, viewportSize.Width, viewportSize.Height));

            _strokePaint.Color = theme.GridLine;
            _textPaint.Color = theme.Text;

            for (var col = visibleCols.Start; col < effectiveColEnd; col++)
            {
                var colLeft = rowNumWidth + (float)(geometry.GetColumnLeftOffset(col) - scrollX);
                var colWidth = (float)geometry.GetColumnWidth(col);
                var colRight = colLeft + colWidth;

                if (selectedColumns.Contains(col) || activeColumn == col)
                {
                    _fillPaint.Color = theme.SelectedColumnTint;
                    var rowsBottom = Math.Min(
                        viewportSize.Height,
                        headerHeight + (float)((visibleRows.EndExclusive * rowHeight) - scrollY));
                    var rowsTop = Math.Max(
                        headerHeight,
                        headerHeight + (float)((visibleRows.Start * rowHeight) - scrollY));
                    if (rowsBottom > rowsTop)
                    {
                        canvas.DrawRect(colLeft, rowsTop, colWidth, rowsBottom - rowsTop, _fillPaint);
                    }
                }

                var hasColumnData = snapshot.TryGetColumn(col, out var pageColumn) && pageColumn is not null;
                var isNumeric = snapshot.Columns[col].DataType == WorksheetDataType.Numeric;
                var maxTextWidth = colWidth - (CellHorizontalPadding * 2f);

                for (var row = visibleRows.Start; row < visibleRows.EndExclusive; row++)
                {
                    var cellTop = headerHeight + (float)((row * rowHeight) - scrollY);
                    var cellBottom = cellTop + rowHeight;

                    canvas.DrawLine(colRight - 0.5f, cellTop, colRight - 0.5f, cellBottom, _strokePaint);
                    canvas.DrawLine(colLeft, cellBottom - 0.5f, colRight, cellBottom - 0.5f, _strokePaint);

                    if (!hasColumnData)
                    {
                        missingCells++;
                    }
                    else
                    {
                        var baselineY = cellTop + cellTextOffsetY;
                        switch (pageColumn!.Values)
                        {
                            case NumericRawDataColumn numeric when numeric.Values[row] is { } number:
                                if (number.TryFormat(numericScratch, out var written, default, CultureInfo.InvariantCulture) && written > 0)
                                {
                                    _textRenderer.DrawClippedText(
                                        canvas,
                                        numericScratch[..written],
                                        colRight - CellHorizontalPadding,
                                        baselineY,
                                        maxTextWidth,
                                        SKTextAlign.Right,
                                        _cellFont,
                                        _textPaint);
                                }

                                break;

                            case StringRawDataColumn text when text.Values[row] is { Length: > 0 } str:
                                _textRenderer.DrawClippedText(
                                    canvas,
                                    str.AsSpan(),
                                    colLeft + CellHorizontalPadding,
                                    baselineY,
                                    maxTextWidth,
                                    SKTextAlign.Left,
                                    _cellFont,
                                    _textPaint);
                                break;
                        }
                    }

                    renderedCells++;
                }
            }

            canvas.Restore();
        }

        // 2. Fixed row-number column (displays 1-based RowOffset + row + 1).
        var renderedRowNumbers = 0;
        if (bodyHeight > 0 && visibleRows.Count > 0)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(0, headerHeight, rowNumWidth, viewportSize.Height));

            _fillPaint.Color = theme.RowNumberBackground;
            canvas.DrawRect(0, headerHeight, rowNumWidth, bodyHeight, _fillPaint);

            _strokePaint.Color = theme.GridLine;
            _textPaint.Color = theme.RowNumberText;

            for (var row = visibleRows.Start; row < visibleRows.EndExclusive; row++)
            {
                var cellTop = headerHeight + (float)((row * rowHeight) - scrollY);
                var cellBottom = cellTop + rowHeight;

                canvas.DrawLine(rowNumWidth - 0.5f, cellTop, rowNumWidth - 0.5f, cellBottom, _strokePaint);
                canvas.DrawLine(0, cellBottom - 0.5f, rowNumWidth, cellBottom - 0.5f, _strokePaint);

                var rowNumber = snapshot.RowOffset + row + 1;
                if (rowNumber.TryFormat(numericScratch, out var written))
                {
                    _textRenderer.DrawClippedText(
                        canvas,
                        numericScratch[..written],
                        rowNumWidth - CellHorizontalPadding,
                        cellTop + cellTextOffsetY,
                        rowNumWidth - (CellHorizontalPadding * 2f),
                        SKTextAlign.Right,
                        _cellFont,
                        _textPaint);
                }

                renderedRowNumbers++;
            }

            canvas.Restore();
        }

        // 3. Fixed column header strip.
        var renderedHeaders = 0;
        if (bodyWidth > 0 && hasVisibleCols)
        {
            canvas.Save();
            canvas.ClipRect(new SKRect(rowNumWidth, 0, viewportSize.Width, headerHeight));

            _fillPaint.Color = theme.HeaderBackground;
            canvas.DrawRect(rowNumWidth, 0, bodyWidth, headerHeight, _fillPaint);

            _strokePaint.Color = theme.GridLine;
            _textPaint.Color = theme.HeaderText;

            for (var col = visibleCols.Start; col < effectiveColEnd; col++)
            {
                var colLeft = rowNumWidth + (float)(geometry.GetColumnLeftOffset(col) - scrollX);
                var colWidth = (float)geometry.GetColumnWidth(col);
                var colRight = colLeft + colWidth;

                var isActive = activeColumn == col;
                var isSelected = isActive || selectedColumns.Contains(col);

                if (isSelected)
                {
                    _fillPaint.Color = isActive ? theme.ActiveColumnTint : theme.SelectedColumnTint;
                    canvas.DrawRect(colLeft, 0, colWidth, headerHeight, _fillPaint);
                }

                if (isActive)
                {
                    _fillPaint.Color = theme.ActiveAccentBar;
                    canvas.DrawRect(colLeft, headerHeight - 2f, colWidth, 2f, _fillPaint);
                }

                canvas.DrawLine(colRight - 0.5f, 0, colRight - 0.5f, headerHeight, _strokePaint);
                canvas.DrawLine(colLeft, headerHeight - 0.5f, colRight, headerHeight - 0.5f, _strokePaint);

                var name = snapshot.Columns[col].Name;
                _textRenderer.DrawClippedText(
                    canvas,
                    name.AsSpan(),
                    colLeft + (colWidth / 2f),
                    headerTextOffsetY,
                    colWidth - (CellHorizontalPadding * 2f),
                    SKTextAlign.Center,
                    isSelected ? _selectedHeaderFont : _headerFont,
                    _textPaint);

                renderedHeaders++;
            }

            canvas.Restore();
        }

        // 4. Top-left fixed corner cell.
        _fillPaint.Color = theme.HeaderBackground;
        canvas.DrawRect(0, 0, rowNumWidth, headerHeight, _fillPaint);
        _strokePaint.Color = theme.GridLine;
        canvas.DrawLine(rowNumWidth - 0.5f, 0, rowNumWidth - 0.5f, headerHeight, _strokePaint);
        canvas.DrawLine(0, headerHeight - 0.5f, rowNumWidth, headerHeight - 0.5f, _strokePaint);

        WriteStats(renderedCells, renderedHeaders, renderedRowNumbers, missingCells);
    }

    private void WriteStats(int cells, int headers, int rowNumbers, int missing)
    {
        Volatile.Write(ref _lastRenderedCells, cells);
        Volatile.Write(ref _lastRenderedHeaders, headers);
        Volatile.Write(ref _lastRenderedRowNumbers, rowNumbers);
        Volatile.Write(ref _lastMissingCells, missing);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _textRenderer.Dispose();
        _fillPaint.Dispose();
        _strokePaint.Dispose();
        _textPaint.Dispose();
        _cellFont.Dispose();
        _headerFont.Dispose();
        _selectedHeaderFont.Dispose();

        foreach (var typeface in _ownedTypefaces)
        {
            typeface.Dispose();
        }

        _ownedTypefaces.Clear();
    }
}
