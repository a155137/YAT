using Avalonia;
using YAT.app.Views.WorksheetGrid;

namespace YAT.App.Tests;

public sealed class WorksheetGridGeometryTests
{
    [Fact]
    public void PrefixSumsAndColumnLookupWorkAcross1000Columns()
    {
        var widths = Enumerable.Range(0, 1000).Select(i => 80.0 + ((i % 5) * 20.0)).ToArray();
        var geometry = new WorksheetViewportGeometry(widths);
        var snapshot = geometry.CreateSnapshot();

        Assert.Equal(1000, geometry.ColumnCount);
        Assert.Equal(widths.Sum(), geometry.TotalDataWidth);
        Assert.Equal(-1, snapshot.FindColumnAtOffset(-1));
        Assert.Equal(-1, snapshot.FindColumnAtOffset(geometry.TotalDataWidth));

        double running = 0;
        for (var i = 0; i < 1000; i++)
        {
            Assert.Equal(running, snapshot.GetColumnLeftOffset(i));
            Assert.Equal(i, snapshot.FindColumnAtOffset(running));
            Assert.Equal(i, snapshot.FindColumnAtOffset(running + (widths[i] * 0.5)));
            Assert.Equal(i, snapshot.FindColumnAtOffset(running + widths[i] - 0.01));
            running += widths[i];
        }
    }

    [Fact]
    public void VisibleRowRangeAppliesOverscanAndClampsToBounds()
    {
        var snapshot = new WorksheetViewportGeometry([100, 100]).CreateSnapshot();
        var bodyHeight = WorksheetViewportGeometry.RowHeight * 10;

        var atTop = snapshot.GetVisibleRowRange(0, bodyHeight, 500, overscanRows: 2);
        Assert.Equal(0, atTop.Start);
        Assert.Equal(12, atTop.EndExclusive);

        var inMiddle = snapshot.GetVisibleRowRange(WorksheetViewportGeometry.RowHeight * 100, bodyHeight, 500, overscanRows: 2);
        Assert.Equal(98, inMiddle.Start);
        Assert.Equal(112, inMiddle.EndExclusive);

        var atBottom = snapshot.GetVisibleRowRange(WorksheetViewportGeometry.RowHeight * 490, bodyHeight, 500, overscanRows: 2);
        Assert.Equal(488, atBottom.Start);
        Assert.Equal(500, atBottom.EndExclusive);
    }

    [Fact]
    public void VisibleColumnRangeUsesBinarySearchAndOverscan()
    {
        var widths = Enumerable.Repeat(100.0, 1000).ToArray();
        var geometry = new WorksheetViewportGeometry(widths);

        var atLeft = geometry.GetVisibleColumnRange(0, 550, overscanColumns: 1);
        Assert.Equal(0, atLeft.Start);
        Assert.Equal(7, atLeft.EndExclusive);

        var inMiddle = geometry.GetVisibleColumnRange(50_000, 550, overscanColumns: 1);
        Assert.Equal(499, inMiddle.Start);
        Assert.Equal(507, inMiddle.EndExclusive);

        var atRight = geometry.GetVisibleColumnRange(99_500, 500, overscanColumns: 1);
        Assert.Equal(994, atRight.Start);
        Assert.Equal(1000, atRight.EndExclusive);
    }

    [Fact]
    public void ResizeColumnClampsAndUpdatesPrefixSums()
    {
        var geometry = new WorksheetViewportGeometry([100, 100, 100]);

        var resized = geometry.ResizeColumn(1, 180);
        Assert.Equal(180, resized);
        var snapshot = geometry.CreateSnapshot();
        Assert.Equal(100, snapshot.GetColumnLeftOffset(1));
        Assert.Equal(280, snapshot.GetColumnLeftOffset(2));
        Assert.Equal(380, snapshot.TotalDataWidth);

        Assert.Equal(WorksheetViewportGeometry.MinColumnWidth, geometry.ResizeColumn(0, 10));
        Assert.Equal(WorksheetViewportGeometry.MaxColumnWidth, geometry.ResizeColumn(2, 999));
    }

    [Fact]
    public void HitTestDistinguishesCornerRowNumberHeaderResizeGripAndCell()
    {
        var geometry = new WorksheetViewportGeometry([100, 100, 100]);
        const double scrollX = 50;
        const double scrollY = WorksheetViewportGeometry.RowHeight * 2;

        var corner = geometry.HitTest(new Point(10, 10), scrollX, scrollY, 500);
        Assert.Equal(WorksheetHitArea.Corner, corner.Area);

        var rowNum = geometry.HitTest(new Point(20, WorksheetViewportGeometry.HeaderHeight + 5), scrollX, scrollY, 500);
        Assert.Equal(WorksheetHitArea.RowNumberCell, rowNum.Area);
        Assert.Equal(2, rowNum.RowIndex);

        var header = geometry.HitTest(new Point(140, 12), scrollX, scrollY, 500);
        Assert.Equal(WorksheetHitArea.ColumnHeader, header.Area);
        Assert.Equal(1, header.ColumnIndex);

        var gripLeftSide = geometry.HitTest(new Point(96, 12), scrollX, scrollY, 500);
        Assert.Equal(WorksheetHitArea.ColumnResizeGrip, gripLeftSide.Area);
        Assert.Equal(0, gripLeftSide.ColumnIndex);

        var gripRightSide = geometry.HitTest(new Point(100, 12), scrollX, scrollY, 500);
        Assert.Equal(WorksheetHitArea.ColumnResizeGrip, gripRightSide.Area);
        Assert.Equal(0, gripRightSide.ColumnIndex);

        var cell = geometry.HitTest(new Point(140, WorksheetViewportGeometry.HeaderHeight + WorksheetViewportGeometry.RowHeight + 4), scrollX, scrollY, 500);
        Assert.Equal(WorksheetHitArea.Cell, cell.Area);
        Assert.Equal(3, cell.RowIndex);
        Assert.Equal(1, cell.ColumnIndex);

        var empty = geometry.HitTest(new Point(140, WorksheetViewportGeometry.HeaderHeight + (WorksheetViewportGeometry.RowHeight * 600)), scrollX, scrollY, 500);
        Assert.Equal(WorksheetHitArea.None, empty.Area);
    }
}
