using YAT.Application.Exceptions;

namespace YAT.Application.Ingestion;

// Parses spreadsheet-style tab-separated text. The first line is always the header row.
// Cells are kept verbatim: no trimming, no quoting rules, no renaming of empty or duplicate headers.
public sealed class TabularTextParser
{
    private const char CellDelimiter = '\t';

    public ParsedTabularData Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ValidationException("Tabular text must not be empty.");
        }

        // StringReader treats \r\n, \n and \r as line terminators and ignores a single terminator
        // at the end of the text, which spreadsheet clipboard output always includes.
        using var reader = new StringReader(text);

        var headers = reader.ReadLine()!.Split(CellDelimiter);
        var rows = new List<IReadOnlyList<string>>();
        var lineNumber = 1;

        while (reader.ReadLine() is { } line)
        {
            lineNumber++;

            var cells = line.Split(CellDelimiter);
            if (cells.Length != headers.Length)
            {
                throw new ValidationException(
                    $"Line {lineNumber} has {Count(cells.Length, "cell")}, but the header has {Count(headers.Length, "column")}.");
            }

            rows.Add(Array.AsReadOnly(cells));
        }

        return new ParsedTabularData(Array.AsReadOnly(headers), rows.AsReadOnly());
    }

    private static string Count(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
