using YAT.Application.Exceptions;
using YAT.Domain.Enums;

namespace YAT.Application.Ingestion;

// Zero-allocation, row-major Span scanner for spreadsheet-style tab-separated text.
// Matches TabularTextParser + ColumnDataTypeDetector semantics verbatim:
//   - Line terminators: \r\n, \n, and \r; a single trailing terminator is ignored (same as StringReader.ReadLine).
//   - Cells are separated by '\t' and kept verbatim (no trimming, no quoting).
//   - An empty data line is 1 empty cell.
internal static class TabularTextScanner
{
    private const char CellDelimiter = '\t';
    private const int CancellationCheckInterval = 16_384;

    private const byte StateEmpty = 0;
    private const byte StateNumeric = 1;
    private const byte StateStringLocked = 2;

    public static TabularTextInspection Inspect(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ValidationException("Tabular text must not be empty.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        var lines = new TsvLineEnumerator(text.AsSpan());
        if (!lines.MoveNext())
        {
            throw new ValidationException("Tabular text must not be empty.");
        }

        var headers = ReadHeaders(lines.Current);
        var columnCount = headers.Length;
        var states = new byte[columnCount];
        var lineNumber = 1;
        var dataRowCount = 0;

        while (lines.MoveNext())
        {
            if (dataRowCount % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            lineNumber++;
            dataRowCount++;

            var cells = new TsvCellEnumerator(lines.Current);
            for (var col = 0; col < columnCount; col++)
            {
                if (!cells.MoveNext())
                {
                    throw new ValidationException(
                        $"Line {lineNumber} has {Count(col, "cell")}, but the header has {Count(columnCount, "column")}.");
                }

                if (states[col] == StateStringLocked)
                {
                    continue;
                }

                var cell = cells.Current;
                if (cell.IsWhiteSpace())
                {
                    continue;
                }

                states[col] = NumericCellParser.TryParse(cell, out _) ? StateNumeric : StateStringLocked;
            }

            if (cells.MoveNext())
            {
                var cellCount = columnCount + 1;
                while (cells.MoveNext())
                {
                    cellCount++;
                }

                throw new ValidationException(
                    $"Line {lineNumber} has {Count(cellCount, "cell")}, but the header has {Count(columnCount, "column")}.");
            }
        }

        var dataTypes = new WorksheetDataType[columnCount];
        for (var col = 0; col < columnCount; col++)
        {
            dataTypes[col] = states[col] == StateNumeric ? WorksheetDataType.Numeric : WorksheetDataType.String;
        }

        return new TabularTextInspection(headers, dataTypes, dataRowCount);
    }

    public static string[] ReadHeaderLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ValidationException("Tabular text must not be empty.");
        }

        var lines = new TsvLineEnumerator(text.AsSpan());
        if (!lines.MoveNext())
        {
            throw new ValidationException("Tabular text must not be empty.");
        }

        return ReadHeaders(lines.Current);
    }

    private static string[] ReadHeaders(ReadOnlySpan<char> headerLine)
    {
        var cellCount = 1 + headerLine.Count(CellDelimiter);
        var headers = new string[cellCount];
        var cells = new TsvCellEnumerator(headerLine);
        var index = 0;
        while (cells.MoveNext())
        {
            headers[index++] = cells.Current.ToString();
        }

        return headers;
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    internal readonly record struct TabularTextInspection(
        string[] Headers,
        WorksheetDataType[] DataTypes,
        int DataRowCount);

    internal ref struct TsvLineEnumerator
    {
        private ReadOnlySpan<char> _remaining;
        private bool _ended;

        public TsvLineEnumerator(ReadOnlySpan<char> text)
        {
            _remaining = text;
            _ended = text.IsEmpty;
            Current = default;
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public bool MoveNext()
        {
            if (_ended)
            {
                return false;
            }

            var newlineIndex = _remaining.IndexOfAny('\r', '\n');
            if (newlineIndex < 0)
            {
                Current = _remaining;
                _remaining = default;
                _ended = true;
                return true;
            }

            Current = _remaining[..newlineIndex];
            var terminator = _remaining[newlineIndex];
            var nextIndex = newlineIndex + 1;
            if (terminator == '\r' && nextIndex < _remaining.Length && _remaining[nextIndex] == '\n')
            {
                nextIndex++;
            }

            _remaining = _remaining[nextIndex..];
            if (_remaining.IsEmpty)
            {
                _ended = true;
            }

            return true;
        }
    }

    internal ref struct TsvCellEnumerator
    {
        private ReadOnlySpan<char> _remaining;
        private bool _hasMore;

        public TsvCellEnumerator(ReadOnlySpan<char> line)
        {
            _remaining = line;
            _hasMore = true;
            Current = default;
        }

        public ReadOnlySpan<char> Current { get; private set; }

        public bool MoveNext()
        {
            if (!_hasMore)
            {
                return false;
            }

            var tabIndex = _remaining.IndexOf(CellDelimiter);
            if (tabIndex < 0)
            {
                Current = _remaining;
                _remaining = default;
                _hasMore = false;
                return true;
            }

            Current = _remaining[..tabIndex];
            _remaining = _remaining[(tabIndex + 1)..];
            return true;
        }
    }
}
