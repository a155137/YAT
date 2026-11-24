using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Application.Ingestion;

// Executes a planned column-oriented paste. Everything is prepared in memory first (column identities, metadata,
// one RawDataBlock); only then is the block written with a single raw store call, followed by the column metadata.
//
// Known limitation: column metadata and raw data are not persisted in one transaction. If a metadata write fails
// after the raw write has completed, the raw values stay replaced while some or all metadata changes are missing.
public sealed class PasteExecutionService
{
    private readonly IWorksheetRepository _worksheets;
    private readonly IWorksheetColumnRepository _columns;
    private readonly IWorksheetRawDataStore _rawDataStore;

    public PasteExecutionService(
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository columns,
        IWorksheetRawDataStore rawDataStore)
    {
        _worksheets = worksheets;
        _columns = columns;
        _rawDataStore = rawDataStore;
    }

    public async Task<PasteExecutionResult> ExecuteAsync(
        WorksheetPastePlan plan,
        ParsedTabularData data,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(data);

        // Phase A: prepare. No persistence side effects until the whole paste has been prepared.
        EnsurePlanDescribesData(plan, data);
        cancellationToken.ThrowIfCancellationRequested();

        var preparedColumns = await PrepareColumnsAsync(plan, cancellationToken);
        var rawColumns = new RawDataColumn[plan.ColumnCount];
        for (var i = 0; i < plan.ColumnCount; i++)
        {
            rawColumns[i] = ConvertColumn(preparedColumns[i].Column, plan.Columns[i], data);
        }

        var block = new RawDataBlock(rawColumns);
        cancellationToken.ThrowIfCancellationRequested();

        // Phase B: persist. The raw write is the single cancellable persistence step; once it has completed,
        // the metadata is written without cancellation so a late cancel cannot separate the two on purpose.
        await _rawDataStore.WriteColumnsAsync(plan.WorksheetId, block, cancellationToken);
        await PersistMetadataAsync(preparedColumns);

        // Phase C: metadata-only result.
        return BuildResult(plan, block.RowCount, preparedColumns);
    }

    // Streaming paste execution directly from tabular text without materializing ParsedTabularData or RawDataBlock.
    //
    // Plan/text pairing contract: plan and text must come from the same PlanFromText call. Phase A verifies header
    // alignment and column metadata; Phase B verifies cell counts per row, numeric parseability for Numeric columns,
    // and exact DataRowCount at end-of-stream inside the raw write transaction (so any mismatch rolls back). Header and
    // row-count checks do not establish full content identity between distinct texts with identical structure and types;
    // the caller is responsible for passing the same text that was planned.
    public async Task<PasteExecutionResult> ExecuteFromTextAsync(
        WorksheetPastePlan plan,
        string text,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(text);

        // Phase A: prepare. No persistence side effects until headers, plan, worksheet and column state are verified.
        EnsurePlanDescribesTextHeader(plan, text);
        cancellationToken.ThrowIfCancellationRequested();

        var preparedColumns = await PrepareColumnsAsync(plan, cancellationToken);
        var schema = preparedColumns
            .Select(prepared => new RawColumnSchema(prepared.Column.Id, prepared.Column.DataType))
            .ToArray();

        cancellationToken.ThrowIfCancellationRequested();

        // Phase B: persist via single-transaction row-major streaming; metadata follows without cancellation.
        await _rawDataStore.WriteColumnsAsync(
            plan.WorksheetId,
            schema,
            writer => StreamRows(plan, text, writer),
            cancellationToken);

        await PersistMetadataAsync(preparedColumns);

        // Phase C: metadata-only result.
        return BuildResult(plan, plan.DataRowCount, preparedColumns);
    }

    private async Task<PreparedColumn[]> PrepareColumnsAsync(WorksheetPastePlan plan, CancellationToken cancellationToken)
    {
        var worksheet = await _worksheets.GetByIdAsync(plan.WorksheetId, cancellationToken);
        if (worksheet is null)
        {
            throw new EntityNotFoundException(nameof(Worksheet), plan.WorksheetId);
        }

        var existingColumns = await _columns.GetByWorksheetIdAsync(plan.WorksheetId, cancellationToken);
        var replacedColumns = FindReplacedColumns(plan, existingColumns);
        EnsurePlanIsCurrent(plan, existingColumns);

        var preparedColumns = new PreparedColumn[plan.ColumnCount];
        for (var i = 0; i < plan.ColumnCount; i++)
        {
            var planned = plan.Columns[i];
            var replaced = replacedColumns.GetValueOrDefault(planned.TargetColumnIndex);

            // New instances only: repositories may hand out their stored objects, so loaded columns are never mutated.
            var column = new WorksheetColumn
            {
                Id = replaced?.Id ?? Guid.NewGuid(),
                WorksheetId = plan.WorksheetId,
                Index = planned.TargetColumnIndex,
                Name = planned.FinalHeader,
                DataType = planned.DataType,
                SemanticType = null,
                Unit = null
            };

            preparedColumns[i] = new PreparedColumn(column, IsNew: replaced is null);
        }

        return preparedColumns;
    }

    private async Task PersistMetadataAsync(PreparedColumn[] preparedColumns)
    {
        foreach (var prepared in preparedColumns)
        {
            if (prepared.IsNew)
            {
                await _columns.AddAsync(prepared.Column, CancellationToken.None);
            }
            else
            {
                await _columns.UpdateAsync(prepared.Column, CancellationToken.None);
            }
        }
    }

    private static PasteExecutionResult BuildResult(WorksheetPastePlan plan, int rowCount, PreparedColumn[] preparedColumns)
    {
        var pastedColumns = preparedColumns
            .Select(prepared => new PastedColumn(
                prepared.Column.Id,
                prepared.Column.Index,
                prepared.Column.Name,
                prepared.Column.DataType,
                prepared.IsNew))
            .ToArray();

        return new PasteExecutionResult(plan.WorksheetId, plan.StartColumnIndex, rowCount, Array.AsReadOnly(pastedColumns));
    }

    // A plan and its data are produced together by the caller; a mismatch is a programming error.
    private static void EnsurePlanDescribesData(WorksheetPastePlan plan, ParsedTabularData data)
    {
        if (plan.ColumnCount != data.Headers.Count || plan.Columns.Count != data.Headers.Count)
        {
            throw new ArgumentException(
                $"The paste plan describes {plan.Columns.Count} columns, but the data has {data.Headers.Count}.", nameof(data));
        }

        if (plan.DataRowCount != data.Rows.Count)
        {
            throw new ArgumentException(
                $"The paste plan describes {plan.DataRowCount} data rows, but the data has {data.Rows.Count}.", nameof(data));
        }

        EnsurePlanHeadersMatch(plan, data.Headers, nameof(data));
    }

    private static void EnsurePlanDescribesTextHeader(WorksheetPastePlan plan, string text)
    {
        var headers = TabularTextScanner.ReadHeaderLine(text);
        EnsurePlanHeadersMatch(plan, headers, nameof(text));
    }

    private static void EnsurePlanHeadersMatch(WorksheetPastePlan plan, IReadOnlyList<string> headers, string dataParamName)
    {
        if (plan.ColumnCount != headers.Count || plan.Columns.Count != headers.Count)
        {
            throw new ArgumentException(
                $"The paste plan describes {plan.Columns.Count} columns, but the data has {headers.Count}.", dataParamName);
        }

        for (var i = 0; i < plan.Columns.Count; i++)
        {
            var planned = plan.Columns[i];
            if (planned.SourceColumnIndex != i
                || planned.TargetColumnIndex != (long)plan.StartColumnIndex + i
                || !string.Equals(planned.OriginalHeader, headers[i], StringComparison.Ordinal))
            {
                throw new ArgumentException($"Planned column {i} does not match the data.", nameof(plan));
            }

            if (planned.DataType is not (WorksheetDataType.Numeric or WorksheetDataType.String))
            {
                throw new ArgumentException($"Planned column {i} has unsupported data type {planned.DataType}.", nameof(plan));
            }
        }
    }

    private static void StreamRows(WorksheetPastePlan plan, string text, IRawDataRowWriter writer)
    {
        var columns = plan.Columns;
        var columnCount = columns.Count;
        var lines = new TabularTextScanner.TsvLineEnumerator(text.AsSpan());
        if (!lines.MoveNext())
        {
            throw new ValidationException("Tabular text must not be empty.");
        }

        var row = 0;
        var lineNumber = 1;
        while (lines.MoveNext())
        {
            lineNumber++;
            if (row >= plan.DataRowCount)
            {
                throw new ArgumentException(
                    $"The paste plan describes {plan.DataRowCount} data rows, but the data has more.", nameof(text));
            }

            writer.BeginRow();
            var cells = new TabularTextScanner.TsvCellEnumerator(lines.Current);
            for (var col = 0; col < columnCount; col++)
            {
                if (!cells.MoveNext())
                {
                    throw new ValidationException(
                        $"Line {lineNumber} has {(col == 1 ? "1 cell" : $"{col} cells")}, but the header has {(columnCount == 1 ? "1 column" : $"{columnCount} columns")}.");
                }

                var cell = cells.Current;
                if (cell.IsWhiteSpace())
                {
                    writer.WriteNull();
                    continue;
                }

                var planned = columns[col];
                if (planned.DataType == WorksheetDataType.String)
                {
                    writer.WriteString(cell);
                }
                else
                {
                    if (!NumericCellParser.TryParse(cell, out var value))
                    {
                        throw new ArgumentException(
                            $"Column '{planned.FinalHeader}' is planned as Numeric, but data row {row + 1} contains the non-numeric value '{cell.ToString()}'.",
                            nameof(text));
                    }

                    writer.WriteDouble(value);
                }
            }

            if (cells.MoveNext())
            {
                var cellCount = columnCount + 1;
                while (cells.MoveNext())
                {
                    cellCount++;
                }

                throw new ValidationException(
                    $"Line {lineNumber} has {cellCount} cells, but the header has {(columnCount == 1 ? "1 column" : $"{columnCount} columns")}.");
            }

            writer.EndRow();
            row++;
        }

        if (row != plan.DataRowCount)
        {
            throw new ArgumentException(
                $"The paste plan describes {plan.DataRowCount} data rows, but the data has {row}.", nameof(text));
        }
    }

    private static Dictionary<int, WorksheetColumn> FindReplacedColumns(
        WorksheetPastePlan plan,
        IReadOnlyList<WorksheetColumn> existingColumns)
    {
        var lastTargetIndex = plan.StartColumnIndex + plan.ColumnCount - 1;
        var replaced = new Dictionary<int, WorksheetColumn>();

        foreach (var column in existingColumns.Where(column => column.Index >= plan.StartColumnIndex && column.Index <= lastTargetIndex))
        {
            if (!replaced.TryAdd(column.Index, column))
            {
                throw new ValidationException($"The worksheet has more than one column at index {column.Index}.");
            }
        }

        return replaced;
    }

    // The plan resolved names against the columns that existed when it was made. If a column outside the target
    // range now carries one of the planned names, the worksheet changed after planning and the paste must be re-planned.
    private static void EnsurePlanIsCurrent(WorksheetPastePlan plan, IReadOnlyList<WorksheetColumn> existingColumns)
    {
        var lastTargetIndex = plan.StartColumnIndex + plan.ColumnCount - 1;
        var survivingNames = existingColumns
            .Where(column => column.Index < plan.StartColumnIndex || column.Index > lastTargetIndex)
            .Select(column => column.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (plan.Columns.Any(planned => survivingNames.Contains(planned.FinalHeader)))
        {
            throw new ValidationException("The worksheet changed after the paste was planned. Plan the paste again.");
        }
    }

    // The data type was decided by the planner; conversion never re-infers it or falls back to String.
    // Empty and whitespace-only cells are null. Non-empty String cells are kept verbatim.
    private static RawDataColumn ConvertColumn(WorksheetColumn column, PlannedPasteColumn planned, ParsedTabularData data)
    {
        var rows = data.Rows;

        if (planned.DataType == WorksheetDataType.String)
        {
            var strings = new string?[rows.Count];
            for (var row = 0; row < rows.Count; row++)
            {
                var cell = rows[row][planned.SourceColumnIndex];
                strings[row] = string.IsNullOrWhiteSpace(cell) ? null : cell;
            }

            return new StringRawDataColumn(column.Id, strings);
        }

        var numbers = new double?[rows.Count];
        for (var row = 0; row < rows.Count; row++)
        {
            var cell = rows[row][planned.SourceColumnIndex];
            if (string.IsNullOrWhiteSpace(cell))
            {
                continue;
            }

            if (!NumericCellParser.TryParse(cell, out var value))
            {
                throw new ArgumentException(
                    $"Column '{planned.FinalHeader}' is planned as Numeric, but data row {row + 1} contains the non-numeric value '{cell}'.",
                    nameof(data));
            }

            numbers[row] = value;
        }

        return new NumericRawDataColumn(column.Id, numbers);
    }

    private sealed record PreparedColumn(WorksheetColumn Column, bool IsNew);
}
