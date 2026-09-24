using SkiaSharp;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// Arranging a legend in the room it may take (Task #044): pure arithmetic over measured sizes. Down columns beside the
// plot, along rows above or below it; a column or cell never wider than 220 with its padding; "… N more" when the room
// cannot hold every entry, N counting every entry left out; every cell inside the box; the same sizes, the same result.
public class GraphLegendLayoutTests
{
    private const float Row = 15f;
    private const float More = 60f;

    private static float[] Widths(int count, float width = 40f) => [.. Enumerable.Repeat(width, count)];

    private static GraphLegendArrangement Columns(float[] widths, float maximumWidth, float maximumHeight, float title = 0f) =>
        GraphLegendLayout.Arrange(widths, title, Row, More, GraphLegendFlow.Columns, maximumWidth, maximumHeight);

    private static GraphLegendArrangement Rows(float[] widths, float maximumWidth, float maximumHeight, float title = 0f) =>
        GraphLegendLayout.Arrange(widths, title, Row, More, GraphLegendFlow.Rows, maximumWidth, maximumHeight);

    // Every cell inside the box; the entries shown and the ones left out add up to all of them.
    private static void AssertWhole(GraphLegendArrangement arrangement, int count)
    {
        var box = new SKRect(0, 0, arrangement.Size.Width, arrangement.Size.Height);
        foreach (var cell in arrangement.Cells)
        {
            Assert.True(float.IsFinite(cell.Bounds.Left) && float.IsFinite(cell.Bounds.Right) && float.IsFinite(cell.Bounds.Top) && float.IsFinite(cell.Bounds.Bottom));
            Assert.True(cell.Bounds.Left >= 0 && cell.Bounds.Top >= 0 && cell.Bounds.Right <= box.Right + 0.001f && cell.Bounds.Bottom <= box.Bottom + 0.001f,
                $"{cell.Bounds} lies outside {box}");
            Assert.True(cell.LabelWidth >= 0 && cell.LabelWidth <= cell.Bounds.Width + 0.001f);
        }

        var shown = arrangement.Cells.Count(cell => !cell.IsMore);
        Assert.Equal(count, shown + arrangement.HiddenCount);
        Assert.Equal(arrangement.HiddenCount > 0, arrangement.Cells.Any(cell => cell.IsMore));
        Assert.Equal(arrangement.Cells.Where(cell => !cell.IsMore).Select(cell => cell.EntryIndex), Enumerable.Range(0, shown));
    }

    [Fact]
    public void AFewEntriesStandInOneColumnAsTheyAlwaysDid()
    {
        var arrangement = Columns(Widths(3), 300, 400, title: 30);

        AssertWhole(arrangement, 3);
        Assert.Equal(0, arrangement.HiddenCount);

        // Padding, the title row and three rows: the legend's familiar box.
        Assert.Equal(new SKSize(8 + 11 + 5 + 40 + 8, 8 + (4 * Row) + (3 * 5) + 8), arrangement.Size);
        // The title may take the whole width across the columns.
        Assert.Equal(56, arrangement.TitleWidth);
        Assert.All(arrangement.Cells, cell => Assert.Equal(8, cell.Bounds.Left));
        Assert.Equal(8 + Row + 5, arrangement.Cells[0].Bounds.Top);
    }

    [Fact]
    public void ColumnsTooTallContinueInTheNextColumn()
    {
        // Room for 5 rows: 12 entries take three columns.
        var height = (2 * 8) + (5 * Row) + (4 * 5);
        var arrangement = Columns(Widths(12), 400, height);

        AssertWhole(arrangement, 12);
        Assert.Equal(0, arrangement.HiddenCount);
        var lefts = arrangement.Cells.Select(cell => cell.Bounds.Left).Distinct().ToList();
        Assert.Equal(3, lefts.Count);
        Assert.Equal(8 + 56 + GraphLegendLayout.ColumnGap, lefts[1]);
        Assert.Equal(5, arrangement.Cells.Count(cell => cell.Bounds.Left == lefts[0]));
    }

    [Fact]
    public void WhatTheWidthCannotHoldIsCountedInTheLastCell()
    {
        // Room for 5 rows and two 56-wide columns: 9 entries shown and "… 11 more" in the tenth place.
        var height = (2 * 8) + (5 * Row) + (4 * 5);
        var arrangement = Columns(Widths(20), 16 + 56 + GraphLegendLayout.ColumnGap + 60, height);

        AssertWhole(arrangement, 20);
        Assert.Equal(11, arrangement.HiddenCount);
        var more = Assert.Single(arrangement.Cells, cell => cell.IsMore);
        Assert.Same(more, arrangement.Cells[^1]);
        Assert.Equal(9, arrangement.Cells.Count(cell => !cell.IsMore));
        Assert.True(arrangement.Size.Width <= 16 + 56 + GraphLegendLayout.ColumnGap + 60);
    }

    [Fact]
    public void ALastColumnTooNarrowForTheCountGivesWayToIt()
    {
        // Room for two 56-wide columns, but not for the second to widen to the count's 60: the count ends the first.
        var height = (2 * 8) + (5 * Row) + (4 * 5);
        var arrangement = Columns(Widths(20), 16 + 56 + GraphLegendLayout.ColumnGap + 58, height);

        AssertWhole(arrangement, 20);
        Assert.Equal(16, arrangement.HiddenCount);
        var more = Assert.Single(arrangement.Cells, cell => cell.IsMore);
        Assert.Equal(More, more.Bounds.Width);
        Assert.Equal(More, more.LabelWidth);
        Assert.Single(arrangement.Cells.Select(cell => cell.Bounds.Left).Distinct());
    }

    [Fact]
    public void AColumnIsNeverWiderThanTheLegendAlwaysWas()
    {
        var arrangement = Columns([30, 900, 45], 1000, 400);

        AssertWhole(arrangement, 3);
        Assert.Equal(GraphLegendLayout.MaximumColumnWidth, arrangement.Size.Width);
        Assert.Equal(GraphLegendLayout.MaximumColumnWidth - 16 - 16, arrangement.Cells[1].LabelWidth);
    }

    [Fact]
    public void RowsWrapAcrossTheWidthTheTitleLeading()
    {
        // 56-wide cells 16 apart in 272: the title (40) and three cells, then four a row.
        var arrangement = Rows(Widths(10), 272 + 16, 400, title: 40);

        AssertWhole(arrangement, 10);
        Assert.Equal(0, arrangement.HiddenCount);
        var tops = arrangement.Cells.Select(cell => cell.Bounds.Top).Distinct().ToList();
        Assert.Equal(3, tops.Count);
        Assert.Equal(8 + 40 + GraphLegendLayout.CellGap, arrangement.Cells[0].Bounds.Left);
        Assert.Equal(3, arrangement.Cells.Count(cell => cell.Bounds.Top == tops[0]));
        Assert.Equal(8 + (3 * Row) + (2 * 5) + 8, arrangement.Size.Height);
    }

    [Fact]
    public void RowsTheHeightCannotHoldEndWithTheCount()
    {
        // Two rows of four 56-wide cells, and "… N more" 60 wide: the last two entries of the second row give way to it.
        var arrangement = Rows(Widths(20), 16 + (4 * 56) + (3 * GraphLegendLayout.CellGap), (2 * 8) + (2 * Row) + 5);

        AssertWhole(arrangement, 20);
        Assert.Equal(14, arrangement.HiddenCount);
        var more = arrangement.Cells[^1];
        Assert.True(more.IsMore);
        Assert.Equal(arrangement.Cells[^2].Bounds.Top, more.Bounds.Top);
        Assert.Equal(More, more.Bounds.Width);
        Assert.Equal(6, arrangement.Cells.Count(cell => !cell.IsMore));
    }

    [Fact]
    public void AHundredEntriesFitTheirRoomAndCountTheRest()
    {
        var widths = Enumerable.Range(0, 100).Select(index => 30f + (index % 7 * 9)).ToArray();
        foreach (var flow in new[] { GraphLegendFlow.Columns, GraphLegendFlow.Rows })
        {
            foreach (var (width, height) in new[] { (280f, 380f), (700f, 120f), (120f, 90f), (1000f, 2000f) })
            {
                var arrangement = GraphLegendLayout.Arrange(widths, 25, Row, More, flow, width, height);
                AssertWhole(arrangement, 100);
                Assert.True(arrangement.Size.Width <= width + 0.001f && arrangement.Size.Height <= height + 0.001f,
                    $"{flow} {arrangement.Size} in {width}x{height}");
                var again = GraphLegendLayout.Arrange(widths, 25, Row, More, flow, width, height);
                Assert.Equal((arrangement.Size, arrangement.TitleWidth, arrangement.HiddenCount), (again.Size, again.TitleWidth, again.HiddenCount));
                Assert.Equal(arrangement.Cells, again.Cells);
            }
        }
    }

    [Theory]
    [InlineData(10f, 400f)]
    [InlineData(400f, 20f)]
    [InlineData(0f, 0f)]
    public void NoRoomForARowMeansNoLegend(float width, float height)
    {
        var arrangement = Columns(Widths(4), width, height);

        Assert.True(arrangement.IsEmpty);
        Assert.Empty(arrangement.Cells);
        Assert.Equal(4, arrangement.HiddenCount);
    }

    [Fact]
    public void SizesThatAreNotSizesAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Columns([float.NaN], 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Columns([-1f], 100, 100));
        Assert.Throws<ArgumentOutOfRangeException>(() => Columns([1f], float.PositiveInfinity, 100));
        Assert.Throws<ArgumentNullException>(() => GraphLegendLayout.Arrange(null!, 0, Row, More, GraphLegendFlow.Rows, 100, 100));
    }
}
