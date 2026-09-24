using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// How a legend's entries flow (Task #044): down columns beside the plot (a legend on the right or the left), or along
// rows above or below it.
public enum GraphLegendFlow
{
    Columns,
    Rows
}

// One cell of an arranged legend, in the legend box's own coordinates (its top left corner is 0, 0): an entry - its
// swatch at the cell's left, its label after it - or, with EntryIndex -1, the "… N more" that stands for the entries
// left out. LabelWidth is the most room the label's text may take; a longer label is cut short with an ellipsis where
// it is drawn.
public sealed record GraphLegendCell(int EntryIndex, SKRect Bounds, float LabelWidth)
{
    public bool IsMore => EntryIndex < 0;
}

// A legend laid out in the room it was given: its box's size, where its title goes (TitleWidth 0 for none), its cells,
// and how many entries did not fit (and are counted by the "… N more" cell). An empty arrangement has no room at all:
// nothing of the legend is drawn, and every entry is left out.
public sealed record GraphLegendArrangement(
    SKSize Size,
    float TitleWidth,
    IReadOnlyList<GraphLegendCell> Cells,
    int HiddenCount)
{
    public bool IsEmpty => Size.Width <= 0 || Size.Height <= 0;

    public static GraphLegendArrangement Empty(int entries) => new(SKSize.Empty, 0f, [], entries);
}

// Arranges a legend's entries in the room it may take (Task #044). Pure arithmetic over sizes measured elsewhere: it
// reads no data, no configuration and no font, and the same sizes always give the same arrangement.
//
// Down columns, entries fill a column as tall as the room allows and then start the next one to its right, as long as
// the columns stay within the room's width; along rows, entries fill a row as wide as the room allows and then start
// the next row below it, as long as the rows stay within the room's height. A column, or a cell of a row, is never
// wider than MaximumColumnWidth with its padding; a label wider than its cell is cut short where it is drawn. When the
// room cannot hold every entry, the last cell that fits becomes "… N more", N counting every entry left out. The
// title, when there is one, heads the columns across their width, or leads the first row.
public static class GraphLegendLayout
{
    // The legend box's own spacing and its swatches: the sizes the legend has always been drawn with.
    public const float Padding = 8f;

    public const float SwatchSize = 11f;

    public const float EntrySpacing = 5f;

    // The widest a column of a legend beside the plot is, padding included, and the widest a cell of a legend above or
    // below it is: the widest a legend has always been.
    public const float MaximumColumnWidth = 220f;

    // Between two columns, and between two cells of a row.
    public const float ColumnGap = 12f;

    public const float CellGap = 16f;

    // labelWidths: each entry's label as measured, in entry order. titleWidth: the title as measured, 0 for none.
    // moreWidth: the widest "… N more" can be for this legend. The room is maximumWidth by maximumHeight, padding
    // included.
    public static GraphLegendArrangement Arrange(
        IReadOnlyList<float> labelWidths,
        float titleWidth,
        float rowHeight,
        float moreWidth,
        GraphLegendFlow flow,
        float maximumWidth,
        float maximumHeight)
    {
        ArgumentNullException.ThrowIfNull(labelWidths);
        Require(titleWidth, nameof(titleWidth));
        Require(rowHeight, nameof(rowHeight));
        Require(moreWidth, nameof(moreWidth));
        Require(maximumWidth, nameof(maximumWidth));
        Require(maximumHeight, nameof(maximumHeight));
        foreach (var width in labelWidths)
        {
            Require(width, nameof(labelWidths));
        }

        var count = labelWidths.Count;
        var innerWidth = maximumWidth - (2 * Padding);
        var innerHeight = maximumHeight - (2 * Padding);
        var cellCap = Math.Min(MaximumColumnWidth - (2 * Padding), innerWidth);

        // Not even one entry's swatch and a little of its label fits: there is no legend to draw.
        if (count == 0 || rowHeight <= 0 || cellCap < SwatchSize + EntrySpacing + 1 || innerHeight < rowHeight)
        {
            return GraphLegendArrangement.Empty(count);
        }

        return flow == GraphLegendFlow.Columns
            ? Columns(labelWidths, titleWidth, rowHeight, moreWidth, innerWidth, innerHeight, cellCap)
            : Rows(labelWidths, titleWidth, rowHeight, moreWidth, innerWidth, innerHeight, cellCap);
    }

    // The width an entry's cell asks for: its swatch, the gap, its label - never more than the cap.
    private static float CellWidth(float labelWidth, float cellCap) =>
        Math.Min(SwatchSize + EntrySpacing + labelWidth, cellCap);

    private static float Top(int row, float rowHeight) => Padding + (row * (rowHeight + EntrySpacing));

    private static GraphLegendArrangement Columns(
        IReadOnlyList<float> labelWidths,
        float titleWidth,
        float rowHeight,
        float moreWidth,
        float innerWidth,
        float innerHeight,
        float cellCap)
    {
        var count = labelWidths.Count;
        var titleRows = titleWidth > 0 ? 1 : 0;
        var perColumn = (int)Math.Floor((innerHeight + EntrySpacing) / (rowHeight + EntrySpacing)) - titleRows;
        if (perColumn < 1)
        {
            return GraphLegendArrangement.Empty(count);
        }

        // Every column the entries need, as wide as its widest cell; then as many of them as the room's width holds.
        var needed = (count + perColumn - 1) / perColumn;
        var widths = new List<float>(needed);
        var used = 0f;
        for (var column = 0; column < needed; column++)
        {
            var first = column * perColumn;
            var width = 0f;
            for (var index = first; index < Math.Min(first + perColumn, count); index++)
            {
                width = Math.Max(width, CellWidth(labelWidths[index], cellCap));
            }

            var next = used + (column > 0 ? ColumnGap : 0) + width;
            if (column > 0 && next > innerWidth)
            {
                break;
            }

            widths.Add(width);
            used = next;
        }

        // Entries left over: the last cell of the last column says how many, itself included. A last column too narrow
        // to widen for it whole gives way, and the count moves to the end of the column before it.
        var moreCell = Math.Min(moreWidth, cellCap);
        int shown;
        int hidden;
        while (true)
        {
            shown = Math.Min(count, widths.Count * perColumn);
            hidden = count - shown;
            if (hidden == 0)
            {
                break;
            }

            var last = widths.Count - 1;
            var room = innerWidth - (used - widths[last]);
            var asked = Math.Max(widths[last], moreCell);
            if (asked <= room || widths.Count == 1)
            {
                shown--;
                hidden++;
                var widened = Math.Min(asked, room);
                used += widened - widths[last];
                widths[last] = widened;
                break;
            }

            used -= widths[last] + ColumnGap;
            widths.RemoveAt(last);
        }

        var cells = new List<GraphLegendCell>(shown + (hidden > 0 ? 1 : 0));
        var left = Padding;
        for (var column = 0; column < widths.Count; column++)
        {
            for (var row = 0; row < perColumn; row++)
            {
                var index = (column * perColumn) + row;
                var isMore = hidden > 0 && index == shown;
                if (index > shown || (index == shown && !isMore))
                {
                    break;
                }

                var top = Top(titleRows + row, rowHeight);
                var bounds = new SKRect(left, top, left + widths[column], top + rowHeight);
                cells.Add(isMore
                    ? new GraphLegendCell(-1, bounds, widths[column])
                    : new GraphLegendCell(index, bounds, Math.Max(0, widths[column] - SwatchSize - EntrySpacing)));
            }

            left += widths[column] + ColumnGap;
        }

        var contentWidth = Math.Max(used, titleRows > 0 ? Math.Min(titleWidth, Math.Max(used, cellCap)) : 0);
        var rows = titleRows + Math.Min(perColumn, shown + (hidden > 0 ? 1 : 0));
        var size = new SKSize(
            contentWidth + (2 * Padding), (2 * Padding) + (rows * rowHeight) + ((rows - 1) * EntrySpacing));
        return new GraphLegendArrangement(size, titleRows > 0 ? contentWidth : 0f, cells, hidden);
    }

    private static GraphLegendArrangement Rows(
        IReadOnlyList<float> labelWidths,
        float titleWidth,
        float rowHeight,
        float moreWidth,
        float innerWidth,
        float innerHeight,
        float cellCap)
    {
        var count = labelWidths.Count;
        var maximumRows = (int)Math.Floor((innerHeight + EntrySpacing) / (rowHeight + EntrySpacing));
        if (maximumRows < 1)
        {
            return GraphLegendArrangement.Empty(count);
        }

        // The title leads the first row; the entries follow it, a new row whenever the next one would not fit.
        var title = titleWidth > 0 ? Math.Min(titleWidth, cellCap) : 0f;
        var placed = new List<(int Index, int Row, float Left, float Width)>(count);
        var row = 0;
        var x = title > 0 ? title + CellGap : 0f;
        var widest = title;
        var overflow = false;
        for (var index = 0; index < count; index++)
        {
            var width = CellWidth(labelWidths[index], cellCap);
            if (x > 0 && x + width > innerWidth)
            {
                if (row + 1 >= maximumRows)
                {
                    overflow = true;
                    break;
                }

                row++;
                x = 0;
            }

            placed.Add((index, row, x, width));
            widest = Math.Max(widest, x + width);
            x += width + CellGap;
        }

        var hidden = 0;
        GraphLegendCell? more = null;
        if (overflow)
        {
            // Entries at the end of the last row give way until "… N more" fits after the ones that are left.
            var moreCell = Math.Min(moreWidth, cellCap);
            static float End((int Index, int Row, float Left, float Width) cell) => cell.Left + cell.Width;
            while (placed.Count > 0 && placed[^1].Row == row && End(placed[^1]) + CellGap + moreCell > innerWidth)
            {
                placed.RemoveAt(placed.Count - 1);
            }

            var moreLeft = placed.Count > 0 && placed[^1].Row == row
                ? End(placed[^1]) + CellGap
                : row == 0 && title > 0 ? title + CellGap : 0f;
            var shownWidth = Math.Max(0, Math.Min(moreCell, innerWidth - moreLeft));
            hidden = count - placed.Count;
            var top = Top(row, rowHeight);
            var moreBounds = new SKRect(Padding + moreLeft, top, Padding + moreLeft + shownWidth, top + rowHeight);
            more = new GraphLegendCell(-1, moreBounds, shownWidth);
            widest = Math.Max(title, placed.Count == 0 ? 0 : placed.Max(End));
            widest = Math.Max(widest, moreLeft + shownWidth);
        }

        var cells = new List<GraphLegendCell>(placed.Count + 1);
        foreach (var (index, cellRow, left, width) in placed)
        {
            var top = Top(cellRow, rowHeight);
            var bounds = new SKRect(Padding + left, top, Padding + left + width, top + rowHeight);
            cells.Add(new GraphLegendCell(index, bounds, Math.Max(0, width - SwatchSize - EntrySpacing)));
        }

        if (more is not null)
        {
            cells.Add(more);
        }

        var rows = row + 1;
        var size = new SKSize(
            widest + (2 * Padding), (2 * Padding) + (rows * rowHeight) + ((rows - 1) * EntrySpacing));
        return new GraphLegendArrangement(size, title, cells, hidden);
    }

    private static void Require(float value, string name)
    {
        if (!float.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "A legend size must be a finite, non-negative number.");
        }
    }
}
