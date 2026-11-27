using System.Globalization;
using YAT.Application.ImportProfiles;
using YAT.Application.Ingestion;

namespace YAT.Application.FtImport;

public enum DatalogParseErrorCode
{
    InvalidProfile,
    InvalidStructure,
    InvalidColumnMapping,
    CorruptInput,
    InvalidEncoding,
    RecordTooLarge,
    UnsupportedQuotedField,
    InvalidRecordWidth,
    InvalidCompositeField,
    MissingRequiredBin,
    MissingValueRejected,
    InvalidNumericValue,
    NumericOverflow,
    Cancelled
}

public sealed record DatalogParseDiagnostic(
    DatalogParseErrorCode Code,
    string? SourceName,
    int? SourceRowNumber,
    string? ColumnHeader,
    string? ValueExcerpt,
    string Message);

public sealed record ResolvedMeasurementColumn(
    string Header,
    int? SourceColumnIndex,
    bool IsPresentInSource);

public sealed record SelectedMeasurementMapping(
    IReadOnlyList<ResolvedMeasurementColumn> Columns,
    IReadOnlyList<string> AbsentHeaders);

public sealed record DatalogParseProgress(
    string? SourceName,
    long ProcessedRowCount,
    long RetainedRowCount);

/// <summary>
/// A single retained datalog row emitted synchronously to the caller's <c>onRow</c> callback.
/// </summary>
/// <param name="SourceRowNumber">1-based physical row number in the source datalog.</param>
/// <param name="Bin">Required Bin string preserved verbatim without trimming or leading-zero normalization.</param>
/// <param name="Site">
/// Optional Site string. <c>null</c> when no Site column is configured in the profile or when the configured Site cell is empty (<c>""</c>);
/// non-empty values are preserved verbatim.
/// </param>
/// <param name="Measurements">
/// Borrowed measurement buffer aligned with <see cref="SelectedMeasurementMapping.Columns"/>.
/// The underlying array is reused across rows and is valid ONLY for the duration of the synchronous <c>onRow</c> callback;
/// callers must copy the values if they need to retain them after the callback returns.
/// </param>
public sealed record ParsedDatalogRow(
    int SourceRowNumber,
    string Bin,
    string? Site,
    ReadOnlyMemory<double?> Measurements);

public sealed record DatalogParseResult(
    bool IsSuccess,
    SelectedMeasurementMapping? Mapping,
    long ProcessedRowCount,
    long RetainedRowCount,
    IReadOnlyList<DatalogParseDiagnostic> Diagnostics)
{
    public static DatalogParseResult Success(
        SelectedMeasurementMapping mapping,
        long processedRowCount,
        long retainedRowCount) =>
        new(true, mapping, processedRowCount, retainedRowCount, []);

    public static DatalogParseResult Failure(
        SelectedMeasurementMapping? mapping,
        long processedRowCount,
        long retainedRowCount,
        DatalogParseDiagnostic diagnostic) =>
        new(false, mapping, processedRowCount, retainedRowCount, [diagnostic]);
}

// Single-file streaming FT datalog row parser (FT-01C).
// Reuses FT-01A Profile rules, FT-01B StreamOpener/Tokenizer/StructureScanner, and NumericCellParser.
public sealed class DatalogRowParser
{
    private const int ProgressRowInterval = 4_096;
    private const int ColumnCancellationInterval = 1_024;
    private const int MaxExcerptChars = 64;

    private readonly ImportProfileValidator _validator = new();
    private readonly DatalogStreamOpener _opener;
    private readonly DatalogRecordTokenizer _tokenizer;

    public DatalogRowParser(
        DatalogStreamOpener? opener = null,
        DatalogRecordTokenizer? tokenizer = null)
    {
        _opener = opener ?? new DatalogStreamOpener();
        _tokenizer = tokenizer ?? new DatalogRecordTokenizer();
    }

    public static bool TryResolveSelectedColumns(
        DatalogStructureScanResult structure,
        IReadOnlyList<string> selectedHeaders,
        string? sourceName,
        out SelectedMeasurementMapping? mapping,
        out DatalogParseDiagnostic? error)
    {
        ArgumentNullException.ThrowIfNull(structure);
        mapping = null;
        error = null;

        if (!structure.IsSuccess || structure.RowPositions is null || structure.BinColumn is null)
        {
            error = new DatalogParseDiagnostic(
                DatalogParseErrorCode.InvalidStructure,
                sourceName,
                null,
                null,
                null,
                "Datalog structure scan result must be successful before parsing rows.");
            return false;
        }

        if (selectedHeaders is null || selectedHeaders.Count == 0)
        {
            error = new DatalogParseDiagnostic(
                DatalogParseErrorCode.InvalidColumnMapping,
                sourceName,
                null,
                null,
                null,
                "At least one selected measurement header is required.");
            return false;
        }

        var availableByHeader = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var col in structure.AvailableTestHeaders)
        {
            if (!availableByHeader.TryAdd(col.Name, col.ColumnIndex))
            {
                error = new DatalogParseDiagnostic(
                    DatalogParseErrorCode.InvalidColumnMapping,
                    sourceName,
                    structure.RowPositions.HeaderRowNumber,
                    col.Name,
                    null,
                    $"Ambiguous duplicate header '{col.Name}' in source structure.");
                return false;
            }
        }

        var allHeadersSet = structure.AllHeaders.ToHashSet(StringComparer.Ordinal);
        var seenSelected = new HashSet<string>(StringComparer.Ordinal);
        var resolved = new ResolvedMeasurementColumn[selectedHeaders.Count];
        var absent = new List<string>();

        for (var i = 0; i < selectedHeaders.Count; i++)
        {
            var header = selectedHeaders[i];
            if (string.IsNullOrEmpty(header))
            {
                error = new DatalogParseDiagnostic(
                    DatalogParseErrorCode.InvalidColumnMapping,
                    sourceName,
                    null,
                    header,
                    null,
                    $"Selected measurement header at index {i} must not be empty.");
                return false;
            }

            if (!seenSelected.Add(header))
            {
                error = new DatalogParseDiagnostic(
                    DatalogParseErrorCode.InvalidColumnMapping,
                    sourceName,
                    null,
                    header,
                    null,
                    $"Duplicate selected measurement header '{header}'.");
                return false;
            }

            if (availableByHeader.TryGetValue(header, out var srcIdx))
            {
                resolved[i] = new ResolvedMeasurementColumn(header, srcIdx, IsPresentInSource: true);
            }
            else if (allHeadersSet.Contains(header))
            {
                error = new DatalogParseDiagnostic(
                    DatalogParseErrorCode.InvalidColumnMapping,
                    sourceName,
                    structure.RowPositions.HeaderRowNumber,
                    header,
                    null,
                    $"Selected measurement header '{header}' is assigned to Bin, Site, or IgnoredColumns.");
                return false;
            }
            else
            {
                resolved[i] = new ResolvedMeasurementColumn(header, SourceColumnIndex: null, IsPresentInSource: false);
                absent.Add(header);
            }
        }

        mapping = new SelectedMeasurementMapping(resolved, absent);
        return true;
    }

    /// <summary>
    /// Streams and parses a single plain-text or ZIP-compressed FT datalog file, invoking <paramref name="onRow"/>
    /// synchronously for each retained row. The opened file stream and ZIP archive (if any) are always disposed on
    /// completion, parsing failure, cancellation, or callback exception.
    /// <para>
    /// <see cref="ParsedDatalogRow.Measurements"/> is a borrowed slice of a reused buffer and is valid only during the
    /// synchronous <paramref name="onRow"/> callback; callers must copy values if retaining them beyond the callback.
    /// </para>
    /// </summary>
    public DatalogParseResult ParseFile(
        string filePath,
        ImportProfile profile,
        DatalogStructureScanResult structure,
        IReadOnlyList<string> selectedHeaders,
        Action<ParsedDatalogRow> onRow,
        Func<string, bool>? includeBin = null,
        IProgress<DatalogParseProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var sourceName = Path.GetFileName(filePath);

        try
        {
            using var opened = _opener.Open(filePath, cancellationToken);
            return ParseReader(
                opened.Reader,
                sourceName,
                profile,
                structure,
                selectedHeaders,
                onRow,
                includeBin,
                progress,
                cancellationToken);
        }
        catch (DatalogOpenException ex)
        {
            var code = ex.ErrorCode == DatalogOpenErrorCode.InvalidEncoding
                ? DatalogParseErrorCode.InvalidEncoding
                : DatalogParseErrorCode.CorruptInput;
            return DatalogParseResult.Failure(
                null,
                0,
                0,
                new DatalogParseDiagnostic(code, sourceName, null, null, null, ex.Message));
        }
    }

    /// <summary>
    /// Streams and parses FT datalog rows from <paramref name="reader"/>, invoking <paramref name="onRow"/>
    /// synchronously for each retained row.
    /// <para>
    /// <see cref="ParsedDatalogRow.Measurements"/> is a borrowed slice of a reused buffer and is valid only during the
    /// synchronous <paramref name="onRow"/> callback; callers must copy values if retaining them beyond the callback.
    /// </para>
    /// </summary>
    public DatalogParseResult ParseReader(
        TextReader reader,
        string? sourceName,
        ImportProfile profile,
        DatalogStructureScanResult structure,
        IReadOnlyList<string> selectedHeaders,
        Action<ParsedDatalogRow> onRow,
        Func<string, bool>? includeBin = null,
        IProgress<DatalogParseProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(structure);
        ArgumentNullException.ThrowIfNull(onRow);

        var profileValidation = _validator.Validate(profile);
        if (!profileValidation.IsValid)
        {
            return DatalogParseResult.Failure(
                null,
                0,
                0,
                new DatalogParseDiagnostic(
                    DatalogParseErrorCode.InvalidProfile,
                    sourceName,
                    null,
                    null,
                    null,
                    profileValidation.Errors[0].Message));
        }

        if (!TryResolveSelectedColumns(structure, selectedHeaders, sourceName, out var mapping, out var mappingError))
        {
            return DatalogParseResult.Failure(null, 0, 0, mappingError!);
        }

        var dataStartRow = structure.RowPositions!.DataStartRowNumber;
        var expectedFieldCount = structure.AllHeaders.Count;
        var binColIndex = structure.BinColumn!.ColumnIndex;
        var binColName = structure.BinColumn.Name;
        var siteColIndex = structure.HasConfiguredSiteColumn ? structure.SiteColumn!.ColumnIndex : -1;
        var delimiter = profile.StructureRules.Delimiter;
        var compositePlans = structure.CompositePlans;
        var missingRules = profile.MissingValueRules;
        var missingMarkerSet = missingRules.Markers.ToHashSet(StringComparer.Ordinal);
        var columns = mapping!.Columns;
        var measurementBuffer = new double?[columns.Count];

        long processedRows = 0;
        long retainedRows = 0;
        var currentRowNumber = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = _tokenizer.ReadBoundedLine(reader, cancellationToken);
                if (line is null)
                {
                    break;
                }

                currentRowNumber++;
                if (currentRowNumber < dataStartRow)
                {
                    continue;
                }

                // 1-2. Tokenize and validate record structure (including split-quotes and composite fields).
                if (!DatalogRecordTokenizer.TrySplitFields(line, delimiter, currentRowNumber, out var rawFields, out var splitError))
                {
                    return DatalogParseResult.Failure(
                        mapping,
                        processedRows,
                        retainedRows,
                        new DatalogParseDiagnostic(
                            DatalogParseErrorCode.UnsupportedQuotedField,
                            sourceName,
                            currentRowNumber,
                            null,
                            Excerpt(line),
                            splitError!.Message));
                }

                if (!DatalogRecordTokenizer.TryExpandCompositeRow(rawFields, compositePlans, currentRowNumber, out var fields, out var compError))
                {
                    return DatalogParseResult.Failure(
                        mapping,
                        processedRows,
                        retainedRows,
                        new DatalogParseDiagnostic(
                            DatalogParseErrorCode.InvalidCompositeField,
                            sourceName,
                            currentRowNumber,
                            null,
                            Excerpt(line),
                            compError!.Message));
                }

                if (fields.Length != expectedFieldCount)
                {
                    return DatalogParseResult.Failure(
                        mapping,
                        processedRows,
                        retainedRows,
                        new DatalogParseDiagnostic(
                            DatalogParseErrorCode.InvalidRecordWidth,
                            sourceName,
                            currentRowNumber,
                            null,
                            null,
                            $"Row {currentRowNumber} has {fields.Length} fields, expected {expectedFieldCount}."));
                }

                // 3. Resolve required Bin (exact string, preserving leading zeros, rejecting empty/whitespace).
                var binValue = fields[binColIndex];
                if (string.IsNullOrWhiteSpace(binValue))
                {
                    return DatalogParseResult.Failure(
                        mapping,
                        processedRows,
                        retainedRows,
                        new DatalogParseDiagnostic(
                            DatalogParseErrorCode.MissingRequiredBin,
                            sourceName,
                            currentRowNumber,
                            binColName,
                            Excerpt(binValue),
                            $"Row {currentRowNumber} is missing a required Bin value in column '{binColName}'."));
                }

                processedRows++;

                // 4-5. Bin-first row inclusion check: skip Site and measurement conversion if excluded.
                if (includeBin is not null && !includeBin(binValue))
                {
                    if (processedRows % ProgressRowInterval == 0)
                    {
                        progress?.Report(new DatalogParseProgress(sourceName, processedRows, retainedRows));
                    }

                    continue;
                }

                // 6. Resolve optional Site and parse selected measurement columns.
                string? siteValue = null;
                if (siteColIndex >= 0)
                {
                    var rawSite = fields[siteColIndex];
                    siteValue = string.IsNullOrEmpty(rawSite) ? null : rawSite;
                }

                for (var c = 0; c < columns.Count; c++)
                {
                    if (c > 0 && c % ColumnCancellationInterval == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    var col = columns[c];
                    if (!col.IsPresentInSource)
                    {
                        measurementBuffer[c] = null;
                        continue;
                    }

                    var rawCell = fields[col.SourceColumnIndex!.Value];
                    if (!TryParseMeasurementCell(
                        rawCell,
                        missingMarkerSet,
                        missingRules.Policy,
                        sourceName,
                        currentRowNumber,
                        col.Header,
                        out var parsedValue,
                        out var cellError))
                    {
                        return DatalogParseResult.Failure(mapping, processedRows, retainedRows, cellError!);
                    }

                    measurementBuffer[c] = parsedValue;
                }

                retainedRows++;
                onRow(new ParsedDatalogRow(currentRowNumber, binValue, siteValue, measurementBuffer));

                if (processedRows % ProgressRowInterval == 0)
                {
                    progress?.Report(new DatalogParseProgress(sourceName, processedRows, retainedRows));
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatalogOpenException ex)
        {
            return DatalogParseResult.Failure(
                mapping,
                processedRows,
                retainedRows,
                new DatalogParseDiagnostic(DatalogParseErrorCode.InvalidEncoding, sourceName, currentRowNumber + 1, null, null, ex.Message));
        }
        catch (DatalogRecordLimitException ex)
        {
            return DatalogParseResult.Failure(
                mapping,
                processedRows,
                retainedRows,
                new DatalogParseDiagnostic(DatalogParseErrorCode.RecordTooLarge, sourceName, currentRowNumber + 1, null, null, ex.Message));
        }

        progress?.Report(new DatalogParseProgress(sourceName, processedRows, retainedRows));
        return DatalogParseResult.Success(mapping, processedRows, retainedRows);
    }

    private static bool TryParseMeasurementCell(
        string cell,
        HashSet<string> missingMarkers,
        MissingValuePolicy policy,
        string? sourceName,
        int rowNumber,
        string columnHeader,
        out double? value,
        out DatalogParseDiagnostic? error)
    {
        value = null;
        error = null;

        var isRecognizedMissing = missingMarkers.Contains(cell)
            || string.Equals(cell, "NaN", StringComparison.Ordinal)
            || string.Equals(cell, "Infinity", StringComparison.Ordinal)
            || string.Equals(cell, "-Infinity", StringComparison.Ordinal);

        if (isRecognizedMissing)
        {
            switch (policy)
            {
                case MissingValuePolicy.UseMissing:
                    value = null;
                    return true;

                case MissingValuePolicy.ReplaceWithZero:
                    value = 0.0;
                    return true;

                case MissingValuePolicy.StopImport:
                    error = new DatalogParseDiagnostic(
                        DatalogParseErrorCode.MissingValueRejected,
                        sourceName,
                        rowNumber,
                        columnHeader,
                        Excerpt(cell),
                        $"Missing value '{Excerpt(cell)}' encountered in column '{columnHeader}' at row {rowNumber} (StopImport policy).");
                    return false;
            }
        }

        if (NumericCellParser.TryParse(cell, out var numeric))
        {
            value = numeric;
            return true;
        }

        // Distinguish numeric overflow (finite numeric format containing digits exceeding double.MaxValue)
        // from invalid numeric text (including unconfigured non-finite variants like "+Infinity" or "infinity").
        if (double.TryParse(cell, NumberStyles.Float, CultureInfo.InvariantCulture, out var nonFinite)
            && double.IsInfinity(nonFinite)
            && cell.AsSpan().IndexOfAnyInRange('0', '9') >= 0)
        {
            error = new DatalogParseDiagnostic(
                DatalogParseErrorCode.NumericOverflow,
                sourceName,
                rowNumber,
                columnHeader,
                Excerpt(cell),
                $"Numeric overflow '{Excerpt(cell)}' in column '{columnHeader}' at row {rowNumber}.");
            return false;
        }

        error = new DatalogParseDiagnostic(
            DatalogParseErrorCode.InvalidNumericValue,
            sourceName,
            rowNumber,
            columnHeader,
            Excerpt(cell),
            $"Invalid numeric value '{Excerpt(cell)}' in column '{columnHeader}' at row {rowNumber}.");
        return false;
    }

    private static string Excerpt(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<blank>";
        }

        return text.Length <= MaxExcerptChars ? text : text[..MaxExcerptChars] + "...";
    }
}
