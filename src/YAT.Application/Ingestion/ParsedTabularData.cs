namespace YAT.Application.Ingestion;

// Temporary ingestion result; not a Domain entity. Every row has exactly Headers.Count cells.
public sealed class ParsedTabularData
{
    internal ParsedTabularData(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    public IReadOnlyList<string> Headers { get; }

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }
}
