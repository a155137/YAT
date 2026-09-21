namespace YAT.app.Analyses;

// How a result column's cells are read: names and labels from the left, numbers from the right.
public enum AnalysisResultAlignment
{
    Left,
    Right
}

public sealed record AnalysisResultColumn(string Name, AnalysisResultAlignment Alignment);

// One row of a result table: its cells follow the table's columns. A cell that has no value is an empty string, never
// a placeholder like "0" or "NaN".
public sealed record AnalysisResultRow(IReadOnlyList<string> Cells);

// What an analysis produces and a result window shows: a title and a table of preformatted text.
//
// It is deliberately this small. Every analysis can describe its result as a table, the window needs nothing else to
// show one, and neither side has to know what the other computes - so the next analysis reuses the window instead of
// bringing its own. It is not a report or document model: there are no sections, styles, charts or exports here, and
// numbers are already formatted (AnalysisNumberFormat) because rendering them is not the window's decision.
public sealed record AnalysisResultTable
{
    public AnalysisResultTable(string title, IReadOnlyList<AnalysisResultColumn> columns, IReadOnlyList<AnalysisResultRow> rows)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);

        if (columns.Count == 0)
        {
            throw new ArgumentException("A result table needs at least one column.", nameof(columns));
        }

        if (rows.Any(row => row.Cells.Count != columns.Count))
        {
            throw new ArgumentException("Every result row must have one cell per column.", nameof(rows));
        }

        Title = title;
        Columns = columns;
        Rows = rows;
    }

    public string Title { get; }

    public IReadOnlyList<AnalysisResultColumn> Columns { get; }

    public IReadOnlyList<AnalysisResultRow> Rows { get; }
}
