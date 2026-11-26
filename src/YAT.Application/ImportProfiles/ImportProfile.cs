namespace YAT.Application.ImportProfiles;

// Portable FT Datalog Import Profile (FT-01A). Holds identity, filename rules, datalog structure rules,
// Bin classification rules, and missing-value rules. Independent of UI and DuckDB.
public sealed record ImportProfile
{
    public const int CurrentSchemaVersion = 1;

    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required FilenameRuleSet FilenameRules { get; init; }

    public required DatalogStructureRuleSet StructureRules { get; init; }

    public required BinRuleSet BinRules { get; init; }

    public required MissingValueRuleSet MissingValueRules { get; init; }
}

public enum FilenameRuleMode
{
    Structured,
    Regex
}

public enum FilenameFieldKind
{
    WholeSegment,
    MarkerBefore,
    MarkerAfter,
    FixedRange,
    RegexCapture
}

public sealed record FilenameFieldRule
{
    public required FilenameFieldKind Kind { get; init; }

    // Zero-based segment index after splitting the filename stem by SegmentDelimiters (used by Structured modes).
    public int SegmentIndex { get; init; } = -1;

    // Delimiter/marker token inside a composite segment (for MarkerBefore / MarkerAfter).
    public string? Marker { get; init; }

    // Character slice inside the segment (for FixedRange).
    public int? StartIndex { get; init; }

    public int? Length { get; init; }

    // Optional capture pattern (for RegexCapture).
    public string? Pattern { get; init; }
}

public sealed record FilenameRetestRule
{
    // Identifies Original vs Retest (e.g. absence/presence of RetestMarker or a dedicated token).
    public FilenameFieldRule? RoleField { get; init; }

    public string? OriginalToken { get; init; }

    public string? RetestMarker { get; init; }

    // Optional rule extracting the Retest Round (e.g. "1" from "R1").
    public FilenameFieldRule? RetestRoundField { get; init; }
}

public sealed record FilenameRuleSet
{
    public required IReadOnlyList<string> AcceptedExtensions { get; init; }

    public FilenameRuleMode Mode { get; init; } = FilenameRuleMode.Structured;

    public IReadOnlyList<char> SegmentDelimiters { get; init; } = ['.', '_'];

    public required FilenameFieldRule LotId { get; init; }

    public required FilenameFieldRule TestStage { get; init; }

    public required FilenameRetestRule Retest { get; init; }

    public FilenameFieldRule? FilePart { get; init; }

    // Optional full-filename regex for advanced users when Mode == Regex.
    public string? AdvancedRegexPattern { get; init; }
}

public enum RowMatchKind
{
    FixedRowIndex,
    KeywordMatch
}

public enum KeywordMatchMode
{
    Exact,
    StartsWith,
    Contains
}

public sealed record RowIdentifier
{
    public required RowMatchKind Kind { get; init; }

    // 1-based row number when Kind == FixedRowIndex.
    public int? RowNumber { get; init; }

    // Keyword text when Kind == KeywordMatch.
    public string? Keyword { get; init; }

    // 0-based column index to inspect for Keyword, or -1 to search any column in the row.
    public int ColumnIndex { get; init; } = 0;

    public KeywordMatchMode MatchMode { get; init; } = KeywordMatchMode.Exact;
}

public enum ColumnSelectorKind
{
    Index,
    HeaderName
}

public sealed record ColumnSelector
{
    public required ColumnSelectorKind Kind { get; init; }

    // 0-based column index when Kind == Index.
    public int? ColumnIndex { get; init; }

    // Exact header text when Kind == HeaderName.
    public string? HeaderName { get; init; }
}

public sealed record CompositeFieldRule(
    ColumnSelector Column,
    string Separator,
    IReadOnlyList<string> OutputNames);

public sealed record DatalogStructureRuleSet
{
    public required string Delimiter { get; init; }

    public IReadOnlyList<CompositeFieldRule> CompositeFields { get; init; } = [];

    public required RowIdentifier HeaderRow { get; init; }

    public RowIdentifier? UnitRow { get; init; }

    public RowIdentifier? TargetRow { get; init; }

    public RowIdentifier? LslRow { get; init; }

    public RowIdentifier? UslRow { get; init; }

    public required RowIdentifier DataStartRow { get; init; }

    public required ColumnSelector BinColumn { get; init; }

    public ColumnSelector? SiteColumn { get; init; }

    public IReadOnlyList<ColumnSelector> IgnoredColumns { get; init; } = [];
}

public sealed record BinRuleSet
{
    // Exact string values preserving leading zeros (e.g. "1", "01", "001").
    public required IReadOnlyList<string> GoodBins { get; init; }

    public required IReadOnlyList<string> NonRetestFailBins { get; init; }
}

public enum MissingValuePolicy
{
    StopImport,
    UseMissing,
    ReplaceWithZero
}

public sealed record MissingValueRuleSet
{
    public required IReadOnlyList<string> Markers { get; init; }

    public required MissingValuePolicy Policy { get; init; }
}
