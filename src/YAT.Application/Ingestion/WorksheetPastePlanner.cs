using System.Globalization;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Ingestion;

// Plans a column-oriented paste: incoming columns are placed sequentially from startColumnIndex,
// whether those targets overlap existing columns, follow the last one, or lie beyond it.
// Header names are cleaned (trim, blank fallback) before duplicate-name resolution.
public sealed class WorksheetPastePlanner
{
    private readonly ColumnDataTypeDetector _dataTypeDetector;

    public WorksheetPastePlanner(ColumnDataTypeDetector dataTypeDetector)
    {
        _dataTypeDetector = dataTypeDetector;
    }

    public WorksheetPastePlan Plan(
        Guid worksheetId,
        IReadOnlyCollection<WorksheetColumn> existingColumns,
        int startColumnIndex,
        ParsedTabularData data)
    {
        ArgumentNullException.ThrowIfNull(existingColumns);
        ArgumentNullException.ThrowIfNull(data);

        var dataTypes = _dataTypeDetector.DetectColumnTypes(data);
        return PlanCore(worksheetId, existingColumns, startColumnIndex, data.Headers, dataTypes, data.Rows.Count);
    }

    public WorksheetPastePlan PlanFromText(
        Guid worksheetId,
        IReadOnlyCollection<WorksheetColumn> existingColumns,
        int startColumnIndex,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(existingColumns);

        var inspection = TabularTextScanner.Inspect(text, cancellationToken);
        return PlanCore(worksheetId, existingColumns, startColumnIndex, inspection.Headers, inspection.DataTypes, inspection.DataRowCount);
    }

    private static WorksheetPastePlan PlanCore(
        Guid worksheetId,
        IReadOnlyCollection<WorksheetColumn> existingColumns,
        int startColumnIndex,
        IReadOnlyList<string> headers,
        IReadOnlyList<WorksheetDataType> dataTypes,
        int dataRowCount)
    {
        if (startColumnIndex < 0)
        {
            throw new ValidationException("Start column index must not be negative.");
        }

        var columnCount = headers.Count;
        if ((long)startColumnIndex + columnCount - 1 > int.MaxValue)
        {
            throw new ValidationException("Pasted columns exceed the maximum column index.");
        }

        var lastTargetIndex = startColumnIndex + columnCount - 1;

        // Existing columns inside the target range are being replaced, so they do not reserve their names.
        var namesInScope = existingColumns
            .Where(column => column.Index < startColumnIndex || column.Index > lastTargetIndex)
            .Select(column => column.Name)
            .ToList();
        var columns = new PlannedPasteColumn[columnCount];

        for (var sourceIndex = 0; sourceIndex < columnCount; sourceIndex++)
        {
            var header = headers[sourceIndex];
            var targetIndex = startColumnIndex + sourceIndex;
            var finalHeader = ResolveUniqueName(CleanHeader(header, targetIndex), namesInScope);
            namesInScope.Add(finalHeader);

            columns[sourceIndex] = new PlannedPasteColumn(
                sourceIndex,
                targetIndex,
                header,
                finalHeader,
                dataTypes[sourceIndex]);
        }

        return new WorksheetPastePlan(worksheetId, startColumnIndex, columnCount, dataRowCount, Array.AsReadOnly(columns));
    }

    // Leading and trailing whitespace is removed; internal whitespace is kept. A blank header is named after
    // its 1-based target worksheet position, e.g. "Column4" for target index 3. The raw header stays in OriginalHeader.
    private static string CleanHeader(string header, int targetIndex)
    {
        var trimmed = header.Trim();
        return trimmed.Length > 0 ? trimmed : $"Column{(long)targetIndex + 1}";
    }

    // Case-insensitive. On a clash the result is "<header>_<max existing suffix + 1>", keeping the
    // incoming casing; suffix gaps are never reused.
    private static string ResolveUniqueName(string header, IReadOnlyList<string> namesInScope)
    {
        if (!namesInScope.Contains(header, StringComparer.OrdinalIgnoreCase))
        {
            return header;
        }

        var prefix = header + "_";
        var maxSuffix = 0;
        foreach (var name in namesInScope)
        {
            if (name.Length > prefix.Length
                && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var suffix)
                && suffix > maxSuffix)
            {
                maxSuffix = suffix;
            }
        }

        return $"{header}_{maxSuffix + 1}";
    }
}
