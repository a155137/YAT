using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public enum FtImportDiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public enum FtImportDiagnosticCode
{
    InvalidProfile,
    MissingPath,
    InaccessiblePath,
    DuplicatePhysicalPath,
    UnsupportedExtension,
    FilenameMatchFailed,
    LogicalFileCollision,
    EmptySelection,
    MultipleTestStages,
    UnresolvedSelectedFile,
    AmbiguousMixedRetestRounds,
    PartialLotSelection,
    PartialRoundSelection,
    OriginalFilesExcluded,
    HigherRetestRoundsExcluded,
    RetestOnlySelection
}

public sealed record FtImportDiagnostic(
    FtImportDiagnosticSeverity Severity,
    FtImportDiagnosticCode Code,
    string Message,
    string? SourcePath = null,
    string? LotId = null,
    string? TestStage = null);

// Logical identity of an identified FT datalog file within a Lot/Stage/Role/Round/Part hierarchy.
public sealed record FtLogicalFileKey(
    string LotId,
    string TestStage,
    FtTestRole TestRole,
    int? RetestRound,
    int? FilePart);

public sealed record FtDiscoveredFile(
    Guid Id,
    string SourcePath,
    string NormalizedPathKey,
    string FileName,
    bool IsFromFolder,
    bool IsIdentified,
    MatchedDatalogFilename? Match,
    FtLogicalFileKey? LogicalKey,
    bool HasLogicalCollision,
    FtImportDiagnostic? IdentificationDiagnostic);

public sealed record FtRetestRoundGroup(
    int? RetestRound,
    IReadOnlyList<FtDiscoveredFile> Files);

public sealed record FtStageGroup(
    string TestStage,
    IReadOnlyList<FtDiscoveredFile> OriginalFiles,
    IReadOnlyList<FtRetestRoundGroup> RetestRounds)
{
    public IReadOnlyList<FtDiscoveredFile> GetAllFiles()
    {
        var list = new List<FtDiscoveredFile>(OriginalFiles.Count + RetestRounds.Sum(r => r.Files.Count));
        list.AddRange(OriginalFiles);
        foreach (var round in RetestRounds)
        {
            list.AddRange(round.Files);
        }

        return list;
    }
}

public sealed record FtLotGroup(
    string LotId,
    IReadOnlyList<FtStageGroup> Stages)
{
    public IReadOnlyList<FtDiscoveredFile> GetAllFiles()
    {
        var list = new List<FtDiscoveredFile>();
        foreach (var stage in Stages)
        {
            list.AddRange(stage.GetAllFiles());
        }

        return list;
    }
}

public sealed record FtLogicalFileCollision(
    FtLogicalFileKey LogicalKey,
    IReadOnlyList<FtDiscoveredFile> Files);

public sealed record FtFileDiscoveryResult(
    ImportProfile ProfileSnapshot,
    IReadOnlyList<FtDiscoveredFile> Files,
    IReadOnlyList<FtLotGroup> LotGroups,
    IReadOnlyList<FtLogicalFileCollision> Collisions,
    IReadOnlyList<FtImportDiagnostic> Diagnostics,
    int SkippedUnsupportedFolderFileCount,
    int SkippedDuplicatePathCount);

internal static class FtImportProfileCloner
{
    // Deep defensive copy of ImportProfile so subsequent caller mutations cannot affect a discovery result or plan.
    public static ImportProfile Clone(ImportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var fn = profile.FilenameRules;
        var st = profile.StructureRules;
        var bin = profile.BinRules;
        var mv = profile.MissingValueRules;

        return profile with
        {
            FilenameRules = fn is null
                ? null!
                : fn with
                {
                    AcceptedExtensions = fn.AcceptedExtensions is null ? null! : Array.AsReadOnly(fn.AcceptedExtensions.ToArray()),
                    SegmentDelimiters = fn.SegmentDelimiters is null ? null! : Array.AsReadOnly(fn.SegmentDelimiters.ToArray()),
                    LotId = fn.LotId is null ? null! : fn.LotId with { },
                    TestStage = fn.TestStage is null ? null! : fn.TestStage with { },
                    Retest = fn.Retest is null
                        ? null!
                        : fn.Retest with
                        {
                            RoleField = fn.Retest.RoleField is null ? null : fn.Retest.RoleField with { },
                            RetestRoundField = fn.Retest.RetestRoundField is null ? null : fn.Retest.RetestRoundField with { }
                        },
                    FilePart = fn.FilePart is null ? null : fn.FilePart with { }
                },
            StructureRules = st is null
                ? null!
                : st with
                {
                    CompositeFields = st.CompositeFields is null
                        ? Array.AsReadOnly(Array.Empty<CompositeFieldRule>())
                        : Array.AsReadOnly(st.CompositeFields
                            .Select(c => c is null
                                ? null!
                                : new CompositeFieldRule(
                                    c.Column is null ? null! : c.Column with { },
                                    c.Separator,
                                    c.OutputNames is null ? null! : Array.AsReadOnly(c.OutputNames.ToArray())))
                            .ToArray()),
                    HeaderRow = st.HeaderRow is null ? null! : st.HeaderRow with { },
                    UnitRow = st.UnitRow is null ? null : st.UnitRow with { },
                    TargetRow = st.TargetRow is null ? null : st.TargetRow with { },
                    LslRow = st.LslRow is null ? null : st.LslRow with { },
                    UslRow = st.UslRow is null ? null : st.UslRow with { },
                    DataStartRow = st.DataStartRow is null ? null! : st.DataStartRow with { },
                    BinColumn = st.BinColumn is null ? null! : st.BinColumn with { },
                    SiteColumn = st.SiteColumn is null ? null : st.SiteColumn with { },
                    IgnoredColumns = st.IgnoredColumns is null
                        ? Array.AsReadOnly(Array.Empty<ColumnSelector>())
                        : Array.AsReadOnly(st.IgnoredColumns.Select(c => c is null ? null! : c with { }).ToArray())
                },
            BinRules = bin is null
                ? null!
                : bin with
                {
                    GoodBins = bin.GoodBins is null ? null! : Array.AsReadOnly(bin.GoodBins.ToArray()),
                    NonRetestFailBins = bin.NonRetestFailBins is null ? null! : Array.AsReadOnly(bin.NonRetestFailBins.ToArray())
                },
            MissingValueRules = mv is null
                ? null!
                : mv with
                {
                    Markers = mv.Markers is null ? null! : Array.AsReadOnly(mv.Markers.ToArray())
                }
        };
    }
}
