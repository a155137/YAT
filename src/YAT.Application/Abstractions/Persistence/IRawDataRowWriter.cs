namespace YAT.Application.Abstractions.Persistence;

// Row-major writer handed to the callback of a streaming raw write (IWorksheetRawDataStore.WriteColumnsAsync).
//
// Protocol, enforced by every implementation (InvalidOperationException otherwise):
//   BeginRow, then exactly one value per schema column in schema order, then EndRow; repeat per row.
//   WriteDouble only for Numeric columns, WriteString only for String columns; WriteNull for either (an empty cell).
//   No row may be left open when the callback returns.
// Rows are numbered 0, 1, 2, ... in the order they are begun: that number is the worksheet row.
// WriteDouble rejects non-finite values (ArgumentException), as NumericRawDataColumn does. WriteString stores the text
// verbatim (no trimming, no null for empty text).
// The writer belongs to the callback invocation only: using it after the callback has returned (or thrown) throws
// ObjectDisposedException. Implementations may check cancellation while values are written.
public interface IRawDataRowWriter
{
    void BeginRow();

    void WriteNull();

    void WriteDouble(double value);

    void WriteString(ReadOnlySpan<char> value);

    void EndRow();
}
