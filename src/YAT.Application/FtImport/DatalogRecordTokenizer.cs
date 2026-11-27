using System.Text;
using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public enum DatalogScanErrorCode
{
    InvalidProfile,
    InvalidEncoding,
    RecordTooLarge,
    ScanLimitExceeded,
    MissingRequiredRow,
    AmbiguousRowMatch,
    InvalidRowOrdering,
    DuplicateHeader,
    MissingBinColumn,
    MissingSiteColumn,
    MissingIgnoredColumn,
    AmbiguousColumnReference,
    InvalidCompositeField,
    UnsupportedQuotedField
}

public sealed record DatalogDiagnostic(
    DatalogScanErrorCode Code,
    int? SourceRowNumber,
    string Message);

// Shared forward-only line reader, delimiter tokenizer, and composite-field expander
// reused by DatalogStructureScanner, DatalogPreviewReader, and FT-01C Streaming Row Parser.
public sealed class DatalogRecordTokenizer
{
    public const int DefaultMaxRecordChars = 4 * 1024 * 1024; // 4M chars (~8 MB UTF-16) per record cap.

    private readonly int _maxRecordChars;

    public DatalogRecordTokenizer(int maxRecordChars = DefaultMaxRecordChars)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRecordChars, 1);
        _maxRecordChars = maxRecordChars;
    }

    // Reads one line bounded by _maxRecordChars so a malformed multi-100MB single-line input cannot OOM.
    // Returns null at EOF. Throws DatalogOpenException on invalid UTF-8 bytes or InvalidOperationException on oversized record.
    public string? ReadBoundedLine(TextReader reader, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        cancellationToken.ThrowIfCancellationRequested();

        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var ch = reader.Read();
                if (ch < 0)
                {
                    return sb.Length == 0 ? null : sb.ToString();
                }

                if (ch == '\r')
                {
                    if (reader.Peek() == '\n')
                    {
                        _ = reader.Read();
                    }

                    return sb.ToString();
                }

                if (ch == '\n')
                {
                    return sb.ToString();
                }

                if (sb.Length >= _maxRecordChars)
                {
                    throw new DatalogRecordLimitException(
                        $"A single datalog record exceeded the maximum allowed length of {_maxRecordChars} characters.");
                }

                sb.Append((char)ch);
            }
        }
        catch (DecoderFallbackException ex)
        {
            throw new DatalogOpenException(
                DatalogOpenErrorCode.InvalidEncoding,
                $"Datalog contains invalid UTF-8 byte sequences: {ex.Message}",
                ex);
        }
    }

    public static string[] SplitFields(string line, string delimiter)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentException.ThrowIfNullOrEmpty(delimiter);

        return delimiter.Length == 1
            ? line.Split(delimiter[0])
            : line.Split(delimiter, StringSplitOptions.None);
    }

    public static bool TrySplitFields(
        string line,
        string delimiter,
        int sourceRowNumber,
        out string[] fields,
        out DatalogDiagnostic? error)
    {
        fields = SplitFields(line, delimiter);
        foreach (var field in fields)
        {
            if (field.IndexOf('"') >= 0 && (field.Count('"') % 2) != 0)
            {
                error = new DatalogDiagnostic(
                    DatalogScanErrorCode.UnsupportedQuotedField,
                    sourceRowNumber,
                    $"Row {sourceRowNumber} appears to contain a quoted field split across delimiter '{delimiter}', which is not supported.");
                return false;
            }
        }

        error = null;
        return true;
    }

    public static bool TryExpandCompositeHeaders(
        IReadOnlyList<string> rawHeaders,
        IReadOnlyList<CompositeFieldRule> compositeRules,
        out string[] expandedHeaders,
        out List<ResolvedCompositePlan> plans,
        out DatalogDiagnostic? error)
    {
        expandedHeaders = [];
        plans = [];
        error = null;

        if (compositeRules.Count == 0)
        {
            expandedHeaders = rawHeaders.ToArray();
            return true;
        }

        var ruleByRawIndex = new Dictionary<int, CompositeFieldRule>();
        foreach (var rule in compositeRules)
        {
            if (rule.OutputNames.Count < 2 || rule.OutputNames.Any(string.IsNullOrWhiteSpace))
            {
                error = new DatalogDiagnostic(
                    DatalogScanErrorCode.InvalidCompositeField,
                    null,
                    "Composite field rule must define at least 2 non-empty output names.");
                return false;
            }

            if (rule.OutputNames.Distinct(StringComparer.Ordinal).Count() != rule.OutputNames.Count)
            {
                error = new DatalogDiagnostic(
                    DatalogScanErrorCode.InvalidCompositeField,
                    null,
                    "Composite field rule contains duplicate output names.");
                return false;
            }

            int targetIndex;
            if (rule.Column.Kind == ColumnSelectorKind.Index)
            {
                targetIndex = rule.Column.ColumnIndex!.Value;
                if (targetIndex < 0 || targetIndex >= rawHeaders.Count)
                {
                    error = new DatalogDiagnostic(
                        DatalogScanErrorCode.InvalidCompositeField,
                        null,
                        $"Composite field column index {targetIndex} is out of range (header count: {rawHeaders.Count}).");
                    return false;
                }
            }
            else
            {
                var matches = new List<int>();
                for (var i = 0; i < rawHeaders.Count; i++)
                {
                    if (string.Equals(rawHeaders[i], rule.Column.HeaderName, StringComparison.Ordinal))
                    {
                        matches.Add(i);
                    }
                }

                if (matches.Count == 0)
                {
                    error = new DatalogDiagnostic(
                        DatalogScanErrorCode.InvalidCompositeField,
                        null,
                        $"Composite field header '{rule.Column.HeaderName}' was not found.");
                    return false;
                }

                if (matches.Count > 1)
                {
                    error = new DatalogDiagnostic(
                        DatalogScanErrorCode.AmbiguousColumnReference,
                        null,
                        $"Composite field header '{rule.Column.HeaderName}' matched multiple columns.");
                    return false;
                }

                targetIndex = matches[0];
            }

            if (!ruleByRawIndex.TryAdd(targetIndex, rule))
            {
                error = new DatalogDiagnostic(
                    DatalogScanErrorCode.InvalidCompositeField,
                    null,
                    $"Column {targetIndex} is targeted by multiple composite field rules.");
                return false;
            }
        }

        var output = new List<string>(rawHeaders.Count + compositeRules.Count);
        for (var i = 0; i < rawHeaders.Count; i++)
        {
            if (ruleByRawIndex.TryGetValue(i, out var rule))
            {
                plans.Add(new ResolvedCompositePlan(i, rule.Separator, rule.OutputNames));
                output.AddRange(rule.OutputNames);
            }
            else
            {
                output.Add(rawHeaders[i]);
            }
        }

        expandedHeaders = output.ToArray();
        return true;
    }

    public static bool TryExpandCompositeRow(
        IReadOnlyList<string> rawFields,
        IReadOnlyList<ResolvedCompositePlan> plans,
        int sourceRowNumber,
        out string[] expandedFields,
        out DatalogDiagnostic? error)
    {
        error = null;
        if (plans.Count == 0)
        {
            expandedFields = rawFields.ToArray();
            return true;
        }

        var planByIndex = plans.ToDictionary(p => p.RawColumnIndex);
        var output = new List<string>(rawFields.Count + plans.Sum(p => p.OutputNames.Count - 1));

        for (var i = 0; i < rawFields.Count; i++)
        {
            if (planByIndex.TryGetValue(i, out var plan))
            {
                var parts = rawFields[i].Split(plan.Separator, StringSplitOptions.None);
                if (parts.Length != plan.OutputNames.Count)
                {
                    expandedFields = [];
                    error = new DatalogDiagnostic(
                        DatalogScanErrorCode.InvalidCompositeField,
                        sourceRowNumber,
                        $"Row {sourceRowNumber} composite column {i} produced {parts.Length} parts using '{plan.Separator}', expected {plan.OutputNames.Count}.");
                    return false;
                }

                output.AddRange(parts);
            }
            else
            {
                output.Add(rawFields[i]);
            }
        }

        expandedFields = output.ToArray();
        return true;
    }
}

public sealed record ResolvedCompositePlan(
    int RawColumnIndex,
    string Separator,
    IReadOnlyList<string> OutputNames);

internal sealed class DatalogRecordLimitException(string message) : Exception(message);
