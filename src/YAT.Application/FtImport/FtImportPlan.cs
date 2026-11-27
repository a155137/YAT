using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public enum FtSelectionState
{
    Unselected,
    PartiallySelected,
    Selected
}

// Explicit discriminator for the latest selected Retest group within a Lot/Stage, avoiding overloading null.
public enum FtLatestRetestKind
{
    None,
    NumberedRound,
    UnnumberedGroup
}

public sealed record FtPlannedSourceFile(
    Guid FileId,
    string SourcePath,
    string FileName,
    string LotId,
    string TestStage,
    FtTestRole TestRole,
    int? RetestRound,
    int? FilePart);

public sealed record FtPlannedRetestRound(
    int? RetestRound,
    IReadOnlyList<FtPlannedSourceFile> Files,
    IReadOnlyList<int> FileParts);

public sealed record FtPlannedLot(
    string LotId,
    string TestStage,
    bool HasOriginalFiles,
    bool HasRetestFiles,
    FtLatestRetestKind LatestRetestKind,
    IReadOnlyList<int> SelectedRetestRounds,
    int? LatestSelectedRetestRound,
    IReadOnlyList<FtPlannedSourceFile> OriginalFiles,
    IReadOnlyList<FtPlannedRetestRound> RetestRounds,
    IReadOnlyList<FtPlannedSourceFile> OrderedFiles);

// Independent, immutable, validated snapshot of a selected FT datalog import plan for FT-03.2 / FT-03.3.
// Holds no open streams, no parsed rows, and no reference to mutable UI selection state.
public sealed record FtImportPlan(
    ImportProfile ProfileSnapshot,
    string TestStage,
    IReadOnlyList<FtPlannedLot> Lots,
    IReadOnlyList<FtPlannedSourceFile> OrderedFiles,
    IReadOnlyList<FtImportDiagnostic> Diagnostics);

public sealed record FtImportPlanResult(
    bool IsValid,
    FtImportPlan? Plan,
    IReadOnlyList<FtImportDiagnostic> Errors,
    IReadOnlyList<FtImportDiagnostic> Warnings,
    IReadOnlyList<FtImportDiagnostic> AllDiagnostics);
