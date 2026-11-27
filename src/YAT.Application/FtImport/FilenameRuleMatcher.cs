using System.Globalization;
using System.Text.RegularExpressions;
using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public enum FtTestRole
{
    Original,
    Retest
}

public sealed record MatchedDatalogFilename(
    string FileName,
    string MatchedExtension,
    string LotId,
    string TestStage,
    FtTestRole TestRole,
    int? RetestRound,
    int? FilePart);

public enum FilenameMatchErrorCode
{
    InvalidProfile,
    InvalidFileName,
    ExtensionNotAccepted,
    SegmentOutOfRange,
    FieldExtractionFailed,
    AmbiguousMatch,
    InvalidNumericField,
    UnmatchedRetestRule,
    RegexTimeout,
    PatternMismatch
}

public sealed record FilenameMatchResult(
    bool IsSuccess,
    MatchedDatalogFilename? Value,
    FilenameMatchErrorCode? ErrorCode,
    string? ErrorMessage)
{
    public static FilenameMatchResult Success(MatchedDatalogFilename value) => new(true, value, null, null);

    public static FilenameMatchResult Failure(FilenameMatchErrorCode code, string message) => new(false, null, code, message);
}

// Deterministic filename identifier supporting Structured (delimiter + marker occurrence) and advanced Regex modes.
// Contains no OSAT-specific hardcoded rules.
public sealed class FilenameRuleMatcher
{
    private static readonly TimeSpan DefaultRegexTimeout = TimeSpan.FromMilliseconds(250);
    private readonly ImportProfileValidator _validator = new();
    private readonly TimeSpan _regexTimeout;

    public FilenameRuleMatcher()
        : this(DefaultRegexTimeout)
    {
    }

    public FilenameRuleMatcher(TimeSpan regexTimeout)
    {
        _regexTimeout = regexTimeout;
    }

    public FilenameMatchResult Match(ImportProfile profile, string fileNameOrPath)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            return FilenameMatchResult.Failure(
                FilenameMatchErrorCode.InvalidProfile,
                validation.Errors[0].Message);
        }

        if (string.IsNullOrWhiteSpace(fileNameOrPath))
        {
            return FilenameMatchResult.Failure(FilenameMatchErrorCode.InvalidFileName, "File name must not be empty.");
        }

        var fileName = Path.GetFileName(fileNameOrPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return FilenameMatchResult.Failure(FilenameMatchErrorCode.InvalidFileName, "Could not extract a file name from the path.");
        }

        var rules = profile.FilenameRules;
        var matchedExt = MatchExtension(fileName, rules.AcceptedExtensions);
        if (matchedExt is null)
        {
            return FilenameMatchResult.Failure(
                FilenameMatchErrorCode.ExtensionNotAccepted,
                $"File '{fileName}' does not match any accepted extension ({string.Join(", ", rules.AcceptedExtensions)}).");
        }

        var stem = fileName[..^matchedExt.Length];
        if (string.IsNullOrEmpty(stem))
        {
            return FilenameMatchResult.Failure(FilenameMatchErrorCode.InvalidFileName, $"File '{fileName}' has no name before extension '{matchedExt}'.");
        }

        return rules.Mode == FilenameRuleMode.Regex
            ? MatchRegex(fileName, stem, matchedExt, rules)
            : MatchStructured(fileName, stem, matchedExt, rules);
    }

    private static string? MatchExtension(string fileName, IReadOnlyList<string> acceptedExtensions)
    {
        // Longest extension first so compound extensions (e.g. ".Reg.zip") win over ".zip".
        foreach (var ext in acceptedExtensions.OrderByDescending(e => e.Length))
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return ext;
            }
        }

        return null;
    }

    private FilenameMatchResult MatchStructured(string fileName, string stem, string matchedExt, FilenameRuleSet rules)
    {
        var delimiters = rules.SegmentDelimiters.ToArray();
        var segments = stem.Split(delimiters, StringSplitOptions.None);

        if (!TryExtractField(segments, rules.LotId, nameof(FilenameRuleSet.LotId), out var lotId, out var lotError))
        {
            return lotError!;
        }

        if (!TryExtractField(segments, rules.TestStage, nameof(FilenameRuleSet.TestStage), out var testStage, out var stageError))
        {
            return stageError!;
        }

        int? filePart = null;
        if (rules.FilePart is not null)
        {
            if (!TryExtractField(segments, rules.FilePart, nameof(FilenameRuleSet.FilePart), out var partText, out var partError))
            {
                return partError!;
            }

            if (!TryParsePositiveInt(partText!, nameof(FilenameRuleSet.FilePart), out var parsedPart, out var numError))
            {
                return numError!;
            }

            filePart = parsedPart;
        }

        if (!TryResolveStructuredRetest(segments, rules.Retest, out var role, out var retestRound, out var retestError))
        {
            return retestError!;
        }

        return FilenameMatchResult.Success(new MatchedDatalogFilename(
            fileName,
            matchedExt,
            lotId!,
            testStage!,
            role,
            retestRound,
            filePart));
    }

    private bool TryExtractField(
        string[] segments,
        FilenameFieldRule rule,
        string fieldName,
        out string? value,
        out FilenameMatchResult? error)
    {
        value = null;
        error = null;

        if (rule.Kind == FilenameFieldKind.RegexCapture)
        {
            var source = rule.SegmentIndex >= 0
                ? (rule.SegmentIndex < segments.Length ? segments[rule.SegmentIndex] : null)
                : string.Join(string.Empty, segments);
            if (source is null)
            {
                error = FilenameMatchResult.Failure(
                    FilenameMatchErrorCode.SegmentOutOfRange,
                    $"{fieldName} requires segment {rule.SegmentIndex}, but filename only has {segments.Length} segments.");
                return false;
            }

            try
            {
                var regex = new Regex(rule.Pattern!, RegexOptions.None, _regexTimeout);
                var match = regex.Match(source);
                if (!match.Success)
                {
                    error = FilenameMatchResult.Failure(
                        FilenameMatchErrorCode.FieldExtractionFailed,
                        $"{fieldName} pattern did not match '{source}'.");
                    return false;
                }

                value = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
                if (string.IsNullOrWhiteSpace(value))
                {
                    error = FilenameMatchResult.Failure(FilenameMatchErrorCode.FieldExtractionFailed, $"{fieldName} extracted an empty value.");
                    return false;
                }

                return true;
            }
            catch (RegexMatchTimeoutException)
            {
                error = FilenameMatchResult.Failure(FilenameMatchErrorCode.RegexTimeout, $"{fieldName} regex evaluation timed out.");
                return false;
            }
        }

        if (rule.SegmentIndex < 0 || rule.SegmentIndex >= segments.Length)
        {
            error = FilenameMatchResult.Failure(
                FilenameMatchErrorCode.SegmentOutOfRange,
                $"{fieldName} requires segment {rule.SegmentIndex}, but filename only has {segments.Length} segments.");
            return false;
        }

        var segment = segments[rule.SegmentIndex];
        switch (rule.Kind)
        {
            case FilenameFieldKind.WholeSegment:
                value = segment;
                break;

            case FilenameFieldKind.MarkerBefore:
                if (!TryFindMarkerOccurrence(segment, rule.Marker!, rule.MarkerOccurrence, out var beforeIndex))
                {
                    error = FilenameMatchResult.Failure(
                        FilenameMatchErrorCode.FieldExtractionFailed,
                        $"{fieldName} marker '{rule.Marker}' (occurrence {rule.MarkerOccurrence}) was not found in '{segment}'.");
                    return false;
                }

                value = segment[..beforeIndex];
                break;

            case FilenameFieldKind.MarkerAfter:
                if (!TryFindMarkerOccurrence(segment, rule.Marker!, rule.MarkerOccurrence, out var afterIndex))
                {
                    error = FilenameMatchResult.Failure(
                        FilenameMatchErrorCode.FieldExtractionFailed,
                        $"{fieldName} marker '{rule.Marker}' (occurrence {rule.MarkerOccurrence}) was not found in '{segment}'.");
                    return false;
                }

                value = segment[(afterIndex + rule.Marker!.Length)..];
                break;

            case FilenameFieldKind.FixedRange:
                var start = rule.StartIndex!.Value;
                var len = rule.Length!.Value;
                if (start + len > segment.Length)
                {
                    error = FilenameMatchResult.Failure(
                        FilenameMatchErrorCode.FieldExtractionFailed,
                        $"{fieldName} fixed range [{start}..{start + len}] exceeds segment '{segment}' length {segment.Length}.");
                    return false;
                }

                value = segment.Substring(start, len);
                break;

            default:
                error = FilenameMatchResult.Failure(FilenameMatchErrorCode.FieldExtractionFailed, $"Unsupported field kind '{rule.Kind}'.");
                return false;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            error = FilenameMatchResult.Failure(FilenameMatchErrorCode.FieldExtractionFailed, $"{fieldName} extracted an empty value.");
            return false;
        }

        return true;
    }

    private static bool TryFindMarkerOccurrence(string text, string marker, int occurrence, out int index)
    {
        index = -1;
        var searchStart = 0;
        for (var i = 0; i < occurrence; i++)
        {
            var found = text.IndexOf(marker, searchStart, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            index = found;
            searchStart = found + marker.Length;
        }

        return index >= 0;
    }

    private bool TryResolveStructuredRetest(
        string[] segments,
        FilenameRetestRule retest,
        out FtTestRole role,
        out int? retestRound,
        out FilenameMatchResult? error)
    {
        role = default;
        retestRound = null;
        error = null;

        string? roleSource = null;
        if (retest.RoleField is not null)
        {
            if (!TryExtractField(segments, retest.RoleField, "Retest.RoleField", out roleSource, out error))
            {
                return false;
            }
        }
        else if (retest.RetestRoundField is { SegmentIndex: >= 0 } rf)
        {
            if (rf.SegmentIndex >= segments.Length)
            {
                error = FilenameMatchResult.Failure(
                    FilenameMatchErrorCode.SegmentOutOfRange,
                    $"Retest segment {rf.SegmentIndex} is out of range (segment count: {segments.Length}).");
                return false;
            }

            roleSource = segments[rf.SegmentIndex];
        }

        if (roleSource is null)
        {
            error = FilenameMatchResult.Failure(FilenameMatchErrorCode.UnmatchedRetestRule, "Retest rule could not locate a target segment.");
            return false;
        }

        var hasOriginalToken = !string.IsNullOrEmpty(retest.OriginalToken);
        var hasRetestMarker = !string.IsNullOrEmpty(retest.RetestMarker);
        var matchesOriginal = hasOriginalToken && string.Equals(roleSource, retest.OriginalToken, StringComparison.Ordinal);
        var matchesRetest = hasRetestMarker && roleSource.Contains(retest.RetestMarker!, StringComparison.Ordinal);

        if (matchesOriginal && matchesRetest)
        {
            error = FilenameMatchResult.Failure(FilenameMatchErrorCode.AmbiguousMatch, $"Retest token '{roleSource}' matches both Original and Retest rules.");
            return false;
        }

        if (matchesRetest)
        {
            role = FtTestRole.Retest;
            if (retest.RetestRoundField is not null)
            {
                if (!TryExtractField(segments, retest.RetestRoundField, "Retest.RetestRoundField", out var roundText, out error))
                {
                    return false;
                }

                if (!TryParsePositiveInt(roundText!, "RetestRound", out var parsedRound, out error))
                {
                    return false;
                }

                retestRound = parsedRound;
            }

            return true;
        }

        if (hasOriginalToken)
        {
            if (!matchesOriginal)
            {
                error = FilenameMatchResult.Failure(
                    FilenameMatchErrorCode.UnmatchedRetestRule,
                    $"Value '{roleSource}' matched neither OriginalToken '{retest.OriginalToken}' nor RetestMarker '{retest.RetestMarker}'.");
                return false;
            }

            role = FtTestRole.Original;
            return true;
        }

        // No explicit OriginalToken: absence of RetestMarker in a validated stage segment indicates Original.
        role = FtTestRole.Original;
        return true;
    }

    private FilenameMatchResult MatchRegex(string fileName, string stem, string matchedExt, FilenameRuleSet rules)
    {
        Match match;
        Regex regex;
        try
        {
            regex = new Regex(rules.AdvancedRegexPattern!, RegexOptions.None, _regexTimeout);
            match = regex.Match(fileName);
            if (!match.Success)
            {
                match = regex.Match(stem);
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return FilenameMatchResult.Failure(FilenameMatchErrorCode.RegexTimeout, "Filename regex matching timed out.");
        }

        if (!match.Success)
        {
            return FilenameMatchResult.Failure(FilenameMatchErrorCode.PatternMismatch, $"Filename '{fileName}' did not match the configured Regex pattern.");
        }

        var lotId = match.Groups["lot_id"].Value;
        var testStage = match.Groups["test_stage"].Value;
        if (string.IsNullOrWhiteSpace(lotId) || string.IsNullOrWhiteSpace(testStage))
        {
            return FilenameMatchResult.Failure(FilenameMatchErrorCode.FieldExtractionFailed, "Regex match must capture non-empty 'lot_id' and 'test_stage' groups.");
        }

        int? filePart = null;
        var partGroup = match.Groups["file_part"];
        if (partGroup.Success && !string.IsNullOrEmpty(partGroup.Value))
        {
            if (!TryParsePositiveInt(partGroup.Value, "file_part", out var parsedPart, out var partError))
            {
                return partError!;
            }

            filePart = parsedPart;
        }

        var roundGroup = match.Groups["retest_round"];
        var roleGroup = match.Groups["retest"];
        FtTestRole role;
        int? retestRound = null;

        if (roundGroup.Success && !string.IsNullOrEmpty(roundGroup.Value))
        {
            if (!TryParsePositiveInt(roundGroup.Value, "retest_round", out var parsedRound, out var roundError))
            {
                return roundError!;
            }

            role = FtTestRole.Retest;
            retestRound = parsedRound;
        }
        else if (roleGroup.Success && !string.IsNullOrEmpty(roleGroup.Value))
        {
            if (string.Equals(roleGroup.Value, "Original", StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrEmpty(rules.Retest.OriginalToken)
                    && string.Equals(roleGroup.Value, rules.Retest.OriginalToken, StringComparison.Ordinal)))
            {
                role = FtTestRole.Original;
            }
            else
            {
                return FilenameMatchResult.Failure(
                    FilenameMatchErrorCode.UnmatchedRetestRule,
                    $"Regex 'retest' group '{roleGroup.Value}' is ambiguous or missing 'retest_round'.");
            }
        }
        else if ((roleGroup.Success && string.IsNullOrEmpty(roleGroup.Value))
            || regex.GroupNumberFromName("retest_round") >= 0)
        {
            role = FtTestRole.Original;
        }
        else
        {
            return FilenameMatchResult.Failure(
                FilenameMatchErrorCode.UnmatchedRetestRule,
                "Regex pattern must include 'retest' or 'retest_round' group to determine Original vs Retest.");
        }

        return FilenameMatchResult.Success(new MatchedDatalogFilename(
            fileName,
            matchedExt,
            lotId,
            testStage,
            role,
            retestRound,
            filePart));
    }

    private static bool TryParsePositiveInt(string text, string fieldName, out int value, out FilenameMatchResult? error)
    {
        error = null;
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value) || value <= 0)
        {
            error = FilenameMatchResult.Failure(
                FilenameMatchErrorCode.InvalidNumericField,
                $"{fieldName} value '{text}' is not a valid positive integer.");
            return false;
        }

        return true;
    }
}
