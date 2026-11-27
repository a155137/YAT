using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public sealed record ResolvedRowPositions(
    int HeaderRowNumber,
    int? UnitRowNumber,
    int? TargetRowNumber,
    int? LslRowNumber,
    int? UslRowNumber,
    int DataStartRowNumber);

public sealed record ResolvedHeaderColumn(
    int ColumnIndex,
    string Name);

public sealed record DatalogStructureScanResult(
    bool IsSuccess,
    ResolvedRowPositions? RowPositions,
    IReadOnlyList<string> RawHeaders,
    IReadOnlyList<string> AllHeaders,
    IReadOnlyList<ResolvedCompositePlan> CompositePlans,
    ResolvedHeaderColumn? BinColumn,
    ResolvedHeaderColumn? SiteColumn,
    bool HasConfiguredSiteColumn,
    IReadOnlyList<ResolvedHeaderColumn> IgnoredColumns,
    IReadOnlyList<ResolvedHeaderColumn> AvailableTestHeaders,
    long RowsScanned,
    IReadOnlyList<DatalogDiagnostic> Diagnostics)
{
    public static DatalogStructureScanResult Failure(long rowsScanned, params DatalogDiagnostic[] diagnostics) =>
        new(false, null, [], [], [], null, null, false, [], [], rowsScanned, diagnostics);
}

// Streaming datalog structure scanner (FT-01B).
// Row numbering convention: 1-based source line numbers (matching RowIdentifier.RowNumber >= 1).
// Stops immediately once all structural roles up to DataStartRow are resolved; never reads the entire datalog.
public sealed class DatalogStructureScanner
{
    public const int DefaultMaxKeywordScanRows = 50_000;

    private readonly ImportProfileValidator _validator = new();
    private readonly DatalogRecordTokenizer _tokenizer;
    private readonly int _maxKeywordScanRows;

    public DatalogStructureScanner(
        DatalogRecordTokenizer? tokenizer = null,
        int maxKeywordScanRows = DefaultMaxKeywordScanRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxKeywordScanRows, 1);
        _tokenizer = tokenizer ?? new DatalogRecordTokenizer();
        _maxKeywordScanRows = maxKeywordScanRows;
    }

    public DatalogStructureScanResult Scan(
        ImportProfile profile,
        TextReader reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(reader);

        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            return DatalogStructureScanResult.Failure(
                0,
                new DatalogDiagnostic(DatalogScanErrorCode.InvalidProfile, null, validation.Errors[0].Message));
        }

        var rules = profile.StructureRules;
        var roles = new (string Name, RowIdentifier? Identifier, bool Required)[]
        {
            (nameof(DatalogStructureRuleSet.HeaderRow), rules.HeaderRow, true),
            (nameof(DatalogStructureRuleSet.UnitRow), rules.UnitRow, false),
            (nameof(DatalogStructureRuleSet.TargetRow), rules.TargetRow, false),
            (nameof(DatalogStructureRuleSet.LslRow), rules.LslRow, false),
            (nameof(DatalogStructureRuleSet.UslRow), rules.UslRow, false),
            (nameof(DatalogStructureRuleSet.DataStartRow), rules.DataStartRow, true)
        };

        var matchedRows = new Dictionary<string, int>(StringComparer.Ordinal);
        string[]? rawHeaderFields = null;
        var rowNumber = 0;

        // If all roles are FixedRowIndex, we never need to read past DataStartRow.
        int? maxFixedRow = roles.Where(r => r.Identifier is { Kind: RowMatchKind.FixedRowIndex })
            .Select(r => r.Identifier!.RowNumber!.Value)
            .DefaultIfEmpty(0)
            .Max();
        var hasKeywordRole = roles.Any(r => r.Identifier is { Kind: RowMatchKind.KeywordMatch });
        var scanCap = hasKeywordRole ? _maxKeywordScanRows : Math.Max(1, maxFixedRow.Value);

        try
        {
            while (rowNumber < scanCap)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = _tokenizer.ReadBoundedLine(reader, cancellationToken);
                if (line is null)
                {
                    break;
                }

                rowNumber++;
                if (!DatalogRecordTokenizer.TrySplitFields(line, rules.Delimiter, rowNumber, out var fields, out var splitError))
                {
                    return DatalogStructureScanResult.Failure(rowNumber, splitError!);
                }

                foreach (var (roleName, identifier, _) in roles)
                {
                    if (identifier is null || !MatchesRow(identifier, rowNumber, fields))
                    {
                        continue;
                    }

                    if (matchedRows.TryGetValue(roleName, out var firstMatch))
                    {
                        // Duplicate match for DataStartRow once already found isn't possible if we stop at DataStartRow,
                        // but for Header / metadata roles a second match before DataStartRow is ambiguous.
                        return DatalogStructureScanResult.Failure(
                            rowNumber,
                            new DatalogDiagnostic(
                                DatalogScanErrorCode.AmbiguousRowMatch,
                                rowNumber,
                                $"{roleName} matched multiple rows ({firstMatch} and {rowNumber})."));
                    }

                    matchedRows[roleName] = rowNumber;
                    if (roleName == nameof(DatalogStructureRuleSet.HeaderRow))
                    {
                        rawHeaderFields = fields;
                    }
                }

                // Stop as soon as DataStartRow is reached AND all configured roles have been resolved
                // (or DataStartRow has been reached so no further preamble roles can appear).
                if (matchedRows.ContainsKey(nameof(DatalogStructureRuleSet.DataStartRow)))
                {
                    break;
                }
            }
        }
        catch (DatalogOpenException ex)
        {
            return DatalogStructureScanResult.Failure(
                rowNumber,
                new DatalogDiagnostic(DatalogScanErrorCode.InvalidEncoding, rowNumber + 1, ex.Message));
        }
        catch (DatalogRecordLimitException ex)
        {
            return DatalogStructureScanResult.Failure(
                rowNumber,
                new DatalogDiagnostic(DatalogScanErrorCode.RecordTooLarge, rowNumber + 1, ex.Message));
        }

        // Check missing required rows.
        foreach (var (roleName, identifier, required) in roles)
        {
            if (required && identifier is not null && !matchedRows.ContainsKey(roleName))
            {
                var code = hasKeywordRole && rowNumber >= _maxKeywordScanRows
                    ? DatalogScanErrorCode.ScanLimitExceeded
                    : DatalogScanErrorCode.MissingRequiredRow;
                return DatalogStructureScanResult.Failure(
                    rowNumber,
                    new DatalogDiagnostic(code, null, $"Required structural role '{roleName}' was not found within {rowNumber} scanned rows."));
            }
        }

        var headerNum = matchedRows[nameof(DatalogStructureRuleSet.HeaderRow)];
        var dataStartNum = matchedRows[nameof(DatalogStructureRuleSet.DataStartRow)];
        if (dataStartNum <= headerNum)
        {
            return DatalogStructureScanResult.Failure(
                rowNumber,
                new DatalogDiagnostic(
                    DatalogScanErrorCode.InvalidRowOrdering,
                    dataStartNum,
                    $"DataStartRow ({dataStartNum}) must appear after HeaderRow ({headerNum})."));
        }

        int? GetOptionalRow(string name) => matchedRows.TryGetValue(name, out var n) ? n : null;
        var unitNum = GetOptionalRow(nameof(DatalogStructureRuleSet.UnitRow));
        var targetNum = GetOptionalRow(nameof(DatalogStructureRuleSet.TargetRow));
        var lslNum = GetOptionalRow(nameof(DatalogStructureRuleSet.LslRow));
        var uslNum = GetOptionalRow(nameof(DatalogStructureRuleSet.UslRow));

        foreach (var (name, optNum) in new[]
        {
            (nameof(DatalogStructureRuleSet.UnitRow), unitNum),
            (nameof(DatalogStructureRuleSet.TargetRow), targetNum),
            (nameof(DatalogStructureRuleSet.LslRow), lslNum),
            (nameof(DatalogStructureRuleSet.UslRow), uslNum)
        })
        {
            if (optNum is { } n && (n <= headerNum || n >= dataStartNum))
            {
                return DatalogStructureScanResult.Failure(
                    rowNumber,
                    new DatalogDiagnostic(
                        DatalogScanErrorCode.InvalidRowOrdering,
                        n,
                        $"Optional metadata row '{name}' ({n}) must lie strictly between HeaderRow ({headerNum}) and DataStartRow ({dataStartNum})."));
            }
        }

        var positions = new ResolvedRowPositions(headerNum, unitNum, targetNum, lslNum, uslNum, dataStartNum);

        // Expand composite headers if configured.
        if (!DatalogRecordTokenizer.TryExpandCompositeHeaders(
            rawHeaderFields!,
            rules.CompositeFields,
            out var allHeaders,
            out var compositePlans,
            out var compositeError))
        {
            return DatalogStructureScanResult.Failure(rowNumber, compositeError!);
        }

        // Detect duplicate headers verbatim (exact ordinal, no trimming, no splitting, no numeric conversion).
        var seenHeaders = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < allHeaders.Length; i++)
        {
            if (!seenHeaders.Add(allHeaders[i]))
            {
                return DatalogStructureScanResult.Failure(
                    rowNumber,
                    new DatalogDiagnostic(
                        DatalogScanErrorCode.DuplicateHeader,
                        headerNum,
                        $"Duplicate header '{allHeaders[i]}' detected at column {i}."));
            }
        }

        // Resolve Bin column (required).
        if (!TryResolveColumn(allHeaders, rules.BinColumn, "BinColumn", DatalogScanErrorCode.MissingBinColumn, headerNum, out var binColumn, out var binError))
        {
            return DatalogStructureScanResult.Failure(rowNumber, binError!);
        }

        // Resolve optional Site column (explicitly distinguish configured-but-absent vs intentionally not configured).
        var hasConfiguredSite = rules.SiteColumn is not null;
        ResolvedHeaderColumn? siteColumn = null;
        if (rules.SiteColumn is not null)
        {
            if (!TryResolveColumn(allHeaders, rules.SiteColumn, "SiteColumn", DatalogScanErrorCode.MissingSiteColumn, headerNum, out siteColumn, out var siteError))
            {
                return DatalogStructureScanResult.Failure(rowNumber, siteError!);
            }
        }

        // Resolve Ignored columns.
        var ignoredColumns = new List<ResolvedHeaderColumn>(rules.IgnoredColumns.Count);
        var excludedIndices = new HashSet<int> { binColumn!.ColumnIndex };
        if (siteColumn is not null)
        {
            excludedIndices.Add(siteColumn.ColumnIndex);
        }

        for (var i = 0; i < rules.IgnoredColumns.Count; i++)
        {
            if (!TryResolveColumn(allHeaders, rules.IgnoredColumns[i], $"IgnoredColumns[{i}]", DatalogScanErrorCode.MissingIgnoredColumn, headerNum, out var ignoredCol, out var ignoredError))
            {
                return DatalogStructureScanResult.Failure(rowNumber, ignoredError!);
            }

            ignoredColumns.Add(ignoredCol!);
            excludedIndices.Add(ignoredCol!.ColumnIndex);
        }

        // Remaining columns in exact source order become AvailableTestHeaders.
        var availableTests = new List<ResolvedHeaderColumn>();
        for (var i = 0; i < allHeaders.Length; i++)
        {
            if (!excludedIndices.Contains(i))
            {
                availableTests.Add(new ResolvedHeaderColumn(i, allHeaders[i]));
            }
        }

        return new DatalogStructureScanResult(
            IsSuccess: true,
            RowPositions: positions,
            RawHeaders: rawHeaderFields!,
            AllHeaders: allHeaders,
            CompositePlans: compositePlans,
            BinColumn: binColumn,
            SiteColumn: siteColumn,
            HasConfiguredSiteColumn: hasConfiguredSite,
            IgnoredColumns: ignoredColumns,
            AvailableTestHeaders: availableTests,
            RowsScanned: rowNumber,
            Diagnostics: []);
    }

    private static bool MatchesRow(RowIdentifier identifier, int rowNumber, string[] fields)
    {
        if (identifier.Kind == RowMatchKind.FixedRowIndex)
        {
            return rowNumber == identifier.RowNumber;
        }

        var keyword = identifier.Keyword!;
        if (identifier.ColumnIndex == -1)
        {
            foreach (var cell in fields)
            {
                if (MatchesKeyword(cell, keyword, identifier.MatchMode))
                {
                    return true;
                }
            }

            return false;
        }

        return identifier.ColumnIndex < fields.Length
            && MatchesKeyword(fields[identifier.ColumnIndex], keyword, identifier.MatchMode);
    }

    private static bool MatchesKeyword(string cell, string keyword, KeywordMatchMode mode) => mode switch
    {
        KeywordMatchMode.Exact => string.Equals(cell, keyword, StringComparison.Ordinal),
        KeywordMatchMode.StartsWith => cell.StartsWith(keyword, StringComparison.Ordinal),
        KeywordMatchMode.Contains => cell.Contains(keyword, StringComparison.Ordinal),
        _ => false
    };

    private static bool TryResolveColumn(
        string[] headers,
        ColumnSelector selector,
        string roleName,
        DatalogScanErrorCode missingCode,
        int headerRowNumber,
        out ResolvedHeaderColumn? resolved,
        out DatalogDiagnostic? error)
    {
        resolved = null;
        error = null;

        if (selector.Kind == ColumnSelectorKind.Index)
        {
            var idx = selector.ColumnIndex!.Value;
            if (idx < 0 || idx >= headers.Length)
            {
                error = new DatalogDiagnostic(
                    missingCode,
                    headerRowNumber,
                    $"{roleName} index {idx} is out of range (header count: {headers.Length}).");
                return false;
            }

            resolved = new ResolvedHeaderColumn(idx, headers[idx]);
            return true;
        }

        var matches = new List<int>();
        for (var i = 0; i < headers.Length; i++)
        {
            if (string.Equals(headers[i], selector.HeaderName, StringComparison.Ordinal))
            {
                matches.Add(i);
            }
        }

        if (matches.Count == 0)
        {
            error = new DatalogDiagnostic(
                missingCode,
                headerRowNumber,
                $"{roleName} header '{selector.HeaderName}' was not found in the datalog header row.");
            return false;
        }

        if (matches.Count > 1)
        {
            error = new DatalogDiagnostic(
                DatalogScanErrorCode.AmbiguousColumnReference,
                headerRowNumber,
                $"{roleName} header '{selector.HeaderName}' matched multiple columns.");
            return false;
        }

        resolved = new ResolvedHeaderColumn(matches[0], headers[matches[0]]);
        return true;
    }
}
