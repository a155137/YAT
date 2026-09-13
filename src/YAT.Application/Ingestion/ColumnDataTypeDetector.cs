using YAT.Domain.Enums;

namespace YAT.Application.Ingestion;

// Detects a basic WorksheetDataType per column. WorksheetDataType has a single numeric value,
// so integer and floating-point columns both map to Numeric; anything else maps to String.
// Blank cells are ignored; a column with no non-blank cells falls back to String.
public sealed class ColumnDataTypeDetector
{
    public IReadOnlyList<WorksheetDataType> DetectColumnTypes(ParsedTabularData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var types = new WorksheetDataType[data.Headers.Count];
        for (var columnIndex = 0; columnIndex < types.Length; columnIndex++)
        {
            types[columnIndex] = Detect(ColumnCells(data, columnIndex));
        }

        return Array.AsReadOnly(types);
    }

    public WorksheetDataType Detect(IEnumerable<string> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);

        var hasValue = false;
        foreach (var cell in cells)
        {
            if (string.IsNullOrWhiteSpace(cell))
            {
                continue;
            }

            if (!IsNumeric(cell))
            {
                return WorksheetDataType.String;
            }

            hasValue = true;
        }

        return hasValue ? WorksheetDataType.Numeric : WorksheetDataType.String;
    }

    private static IEnumerable<string> ColumnCells(ParsedTabularData data, int columnIndex)
    {
        foreach (var row in data.Rows)
        {
            yield return row[columnIndex];
        }
    }

    private static bool IsNumeric(string cell) => NumericCellParser.TryParse(cell, out _);
}
