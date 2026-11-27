using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

// Discovers FT datalog files from explicit file paths or a single folder (non-recursive), identifies filenames
// through the existing FilenameRuleMatcher, detects logical file collisions, and produces a deterministic
// Lot -> Stage -> Original / Retest Round -> File Part hierarchy without opening datalog contents.
public sealed class FtFileDiscoveryService
{
    private static readonly StringComparer PathKeyComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly FilenameRuleMatcher _matcher;
    private readonly ImportProfileValidator _validator;

    public FtFileDiscoveryService(
        FilenameRuleMatcher? matcher = null,
        ImportProfileValidator? validator = null)
    {
        _matcher = matcher ?? new FilenameRuleMatcher();
        _validator = validator ?? new ImportProfileValidator();
    }

    public FtFileDiscoveryResult DiscoverFiles(
        ImportProfile profile,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(filePaths);
        return Discover(profile, explicitFilePaths: filePaths, folderPath: null, cancellationToken);
    }

    public FtFileDiscoveryResult DiscoverFolder(
        ImportProfile profile,
        string folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        return Discover(profile, explicitFilePaths: null, folderPath: folderPath, cancellationToken);
    }

    public FtFileDiscoveryResult Discover(
        ImportProfile profile,
        IReadOnlyList<string>? explicitFilePaths,
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = FtImportProfileCloner.Clone(profile);
        var diagnostics = new List<FtImportDiagnostic>();

        var profileValidation = _validator.Validate(snapshot);
        if (!profileValidation.IsValid)
        {
            foreach (var err in profileValidation.Errors)
            {
                diagnostics.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidProfile,
                    $"{err.Field}: {err.Message}"));
            }

            return new FtFileDiscoveryResult(
                snapshot,
                [],
                [],
                [],
                Array.AsReadOnly(diagnostics.ToArray()),
                SkippedUnsupportedFolderFileCount: 0,
                SkippedDuplicatePathCount: 0);
        }

        var acceptedExtensions = snapshot.FilenameRules.AcceptedExtensions
            .OrderByDescending(e => e.Length)
            .ToArray();

        var seenPaths = new HashSet<string>(PathKeyComparer);
        var candidates = new List<(string FullPath, string PathKey, string FileName, bool IsFromFolder)>();
        var skippedUnsupportedFolderFiles = 0;
        var skippedDuplicatePaths = 0;

        if (explicitFilePaths is not null)
        {
            foreach (var rawPath in explicitFilePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(rawPath))
                {
                    diagnostics.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Error,
                        FtImportDiagnosticCode.MissingPath,
                        "An empty file path was provided."));
                    continue;
                }

                if (!TryNormalizePath(rawPath, out var fullPath, out var normError))
                {
                    diagnostics.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Error,
                        FtImportDiagnosticCode.InaccessiblePath,
                        $"Path '{rawPath}' is invalid or inaccessible: {normError}",
                        SourcePath: rawPath));
                    continue;
                }

                var pathKey = NormalizePathKey(fullPath);
                if (!seenPaths.Add(pathKey))
                {
                    skippedDuplicatePaths++;
                    diagnostics.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Info,
                        FtImportDiagnosticCode.DuplicatePhysicalPath,
                        $"Duplicate path skipped: '{fullPath}'.",
                        SourcePath: fullPath));
                    continue;
                }

                if (!TryVerifyFileExists(fullPath, out var exists, out var accessError))
                {
                    diagnostics.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Error,
                        FtImportDiagnosticCode.InaccessiblePath,
                        $"File '{fullPath}' could not be accessed: {accessError}",
                        SourcePath: fullPath));
                    continue;
                }

                if (!exists)
                {
                    diagnostics.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Error,
                        FtImportDiagnosticCode.MissingPath,
                        $"File '{fullPath}' does not exist.",
                        SourcePath: fullPath));
                    continue;
                }

                candidates.Add((fullPath, pathKey, Path.GetFileName(fullPath), IsFromFolder: false));
            }
        }

        if (!string.IsNullOrWhiteSpace(folderPath))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryNormalizePath(folderPath, out var fullFolderPath, out var folderNormError))
            {
                diagnostics.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InaccessiblePath,
                    $"Folder '{folderPath}' is invalid or inaccessible: {folderNormError}",
                    SourcePath: folderPath));
            }
            else if (!Directory.Exists(fullFolderPath))
            {
                diagnostics.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.MissingPath,
                    $"Folder '{fullFolderPath}' does not exist.",
                    SourcePath: fullFolderPath));
            }
            else
            {
                try
                {
                    foreach (var entry in Directory.EnumerateFiles(fullFolderPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var fileName = Path.GetFileName(entry);
                        if (!HasAcceptedExtension(fileName, acceptedExtensions))
                        {
                            skippedUnsupportedFolderFiles++;
                            continue;
                        }

                        var fullPath = Path.GetFullPath(entry);
                        var pathKey = NormalizePathKey(fullPath);
                        if (!seenPaths.Add(pathKey))
                        {
                            skippedDuplicatePaths++;
                            diagnostics.Add(new FtImportDiagnostic(
                                FtImportDiagnosticSeverity.Info,
                                FtImportDiagnosticCode.DuplicatePhysicalPath,
                                $"Duplicate path skipped: '{fullPath}'.",
                                SourcePath: fullPath));
                            continue;
                        }

                        candidates.Add((fullPath, pathKey, fileName, IsFromFolder: true));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Error,
                        FtImportDiagnosticCode.InaccessiblePath,
                        $"Folder '{fullFolderPath}' could not be enumerated: {ex.Message}",
                        SourcePath: fullFolderPath));
                }
            }
        }

        // Deterministic pass order before filename matching.
        candidates.Sort((a, b) =>
        {
            var cmp = StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = StringComparer.Ordinal.Compare(a.FileName, b.FileName);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
            return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.FullPath, b.FullPath);
        });

        var provisionalFiles = new List<FtDiscoveredFile>(candidates.Count);
        var logicalBuckets = new Dictionary<FtLogicalFileKey, List<int>>();

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var matchResult = _matcher.Match(snapshot, candidate.FullPath);
            if (matchResult.IsSuccess && matchResult.Value is { } matched)
            {
                var logicalKey = new FtLogicalFileKey(
                    matched.LotId,
                    matched.TestStage,
                    matched.TestRole,
                    matched.RetestRound,
                    matched.FilePart);

                var index = provisionalFiles.Count;
                provisionalFiles.Add(new FtDiscoveredFile(
                    Id: Guid.NewGuid(),
                    SourcePath: candidate.FullPath,
                    NormalizedPathKey: candidate.PathKey,
                    FileName: candidate.FileName,
                    IsFromFolder: candidate.IsFromFolder,
                    IsIdentified: true,
                    Match: matched,
                    LogicalKey: logicalKey,
                    HasLogicalCollision: false,
                    IdentificationDiagnostic: null));

                if (!logicalBuckets.TryGetValue(logicalKey, out var bucket))
                {
                    bucket = [];
                    logicalBuckets[logicalKey] = bucket;
                }

                bucket.Add(index);
            }
            else
            {
                var code = matchResult.ErrorCode == FilenameMatchErrorCode.ExtensionNotAccepted
                    ? FtImportDiagnosticCode.UnsupportedExtension
                    : FtImportDiagnosticCode.FilenameMatchFailed;
                var diag = new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    code,
                    matchResult.ErrorMessage ?? $"Filename '{candidate.FileName}' could not be identified.",
                    SourcePath: candidate.FullPath);
                diagnostics.Add(diag);

                provisionalFiles.Add(new FtDiscoveredFile(
                    Id: Guid.NewGuid(),
                    SourcePath: candidate.FullPath,
                    NormalizedPathKey: candidate.PathKey,
                    FileName: candidate.FileName,
                    IsFromFolder: candidate.IsFromFolder,
                    IsIdentified: false,
                    Match: null,
                    LogicalKey: null,
                    HasLogicalCollision: false,
                    IdentificationDiagnostic: diag));
            }
        }

        // Flag logical file collisions (distinct physical files resolving to the same Lot/Stage/Role/Round/Part).
        var collisions = new List<FtLogicalFileCollision>();
        foreach (var (key, indices) in logicalBuckets
            .Where(kv => kv.Value.Count > 1)
            .OrderBy(kv => kv.Key.LotId, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.TestStage, StringComparer.Ordinal)
            .ThenBy(kv => kv.Key.TestRole)
            .ThenBy(kv => kv.Key.RetestRound ?? int.MaxValue)
            .ThenBy(kv => kv.Key.FilePart ?? int.MaxValue))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var collidingFiles = new List<FtDiscoveredFile>(indices.Count);
            foreach (var idx in indices)
            {
                var flagged = provisionalFiles[idx] with { HasLogicalCollision = true };
                provisionalFiles[idx] = flagged;
                collidingFiles.Add(flagged);
            }

            collidingFiles.Sort(CompareDiscoveredFiles);
            collisions.Add(new FtLogicalFileCollision(key, Array.AsReadOnly(collidingFiles.ToArray())));

            var roleDesc = key.TestRole == FtTestRole.Original
                ? "Original"
                : key.RetestRound is { } round ? $"Retest Round {round}" : "Retest";
            var partDesc = key.FilePart is { } part ? $", Part {part}" : string.Empty;
            var fileList = string.Join(", ", collidingFiles.Select(f => f.FileName));

            diagnostics.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Warning,
                FtImportDiagnosticCode.LogicalFileCollision,
                $"Logical file collision detected for Lot '{key.LotId}', Stage '{key.TestStage}', {roleDesc}{partDesc}: {collidingFiles.Count} distinct files ({fileList}). Exclude the unwanted file(s) before importing.",
                LotId: key.LotId,
                TestStage: key.TestStage));
        }

        // Build deterministic Lot -> Stage -> Original / RetestRound -> Files hierarchy.
        var lotGroups = BuildLotGroups(provisionalFiles, cancellationToken);

        return new FtFileDiscoveryResult(
            snapshot,
            Array.AsReadOnly(provisionalFiles.ToArray()),
            lotGroups,
            Array.AsReadOnly(collisions.ToArray()),
            Array.AsReadOnly(diagnostics.ToArray()),
            skippedUnsupportedFolderFiles,
            skippedDuplicatePaths);
    }

    private static IReadOnlyList<FtLotGroup> BuildLotGroups(
        List<FtDiscoveredFile> files,
        CancellationToken cancellationToken)
    {
        var lotBuckets = new Dictionary<string, Dictionary<string, List<FtDiscoveredFile>>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.IsIdentified || file.Match is null)
            {
                continue;
            }

            if (!lotBuckets.TryGetValue(file.Match.LotId, out var stageBuckets))
            {
                stageBuckets = new Dictionary<string, List<FtDiscoveredFile>>(StringComparer.Ordinal);
                lotBuckets[file.Match.LotId] = stageBuckets;
            }

            if (!stageBuckets.TryGetValue(file.Match.TestStage, out var stageFiles))
            {
                stageFiles = [];
                stageBuckets[file.Match.TestStage] = stageFiles;
            }

            stageFiles.Add(file);
        }

        var lotGroups = new List<FtLotGroup>(lotBuckets.Count);
        foreach (var lotId in lotBuckets.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stageBuckets = lotBuckets[lotId];
            var stageGroups = new List<FtStageGroup>(stageBuckets.Count);

            foreach (var stage in stageBuckets.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var stageFiles = stageBuckets[stage];
                var originals = new List<FtDiscoveredFile>();
                var retestBuckets = new Dictionary<int, List<FtDiscoveredFile>>();
                List<FtDiscoveredFile>? unnumberedRetests = null;

                foreach (var f in stageFiles)
                {
                    if (f.Match!.TestRole == FtTestRole.Original)
                    {
                        originals.Add(f);
                    }
                    else if (f.Match.RetestRound is { } round)
                    {
                        if (!retestBuckets.TryGetValue(round, out var roundList))
                        {
                            roundList = [];
                            retestBuckets[round] = roundList;
                        }

                        roundList.Add(f);
                    }
                    else
                    {
                        unnumberedRetests ??= [];
                        unnumberedRetests.Add(f);
                    }
                }

                originals.Sort(CompareDiscoveredFiles);

                var retestRounds = new List<FtRetestRoundGroup>(retestBuckets.Count + (unnumberedRetests is null ? 0 : 1));
                foreach (var round in retestBuckets.Keys.OrderBy(r => r))
                {
                    var roundFiles = retestBuckets[round];
                    roundFiles.Sort(CompareDiscoveredFiles);
                    retestRounds.Add(new FtRetestRoundGroup(round, Array.AsReadOnly(roundFiles.ToArray())));
                }

                if (unnumberedRetests is not null)
                {
                    unnumberedRetests.Sort(CompareDiscoveredFiles);
                    retestRounds.Add(new FtRetestRoundGroup(null, Array.AsReadOnly(unnumberedRetests.ToArray())));
                }

                stageGroups.Add(new FtStageGroup(
                    stage,
                    Array.AsReadOnly(originals.ToArray()),
                    Array.AsReadOnly(retestRounds.ToArray())));
            }

            lotGroups.Add(new FtLotGroup(lotId, Array.AsReadOnly(stageGroups.ToArray())));
        }

        return Array.AsReadOnly(lotGroups.ToArray());
    }

    internal static int CompareDiscoveredFiles(FtDiscoveredFile a, FtDiscoveredFile b)
    {
        var partA = a.Match?.FilePart ?? int.MaxValue;
        var partB = b.Match?.FilePart ?? int.MaxValue;
        var cmp = partA.CompareTo(partB);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = StringComparer.Ordinal.Compare(a.FileName, b.FileName);
        if (cmp != 0)
        {
            return cmp;
        }

        cmp = StringComparer.OrdinalIgnoreCase.Compare(a.SourcePath, b.SourcePath);
        return cmp != 0 ? cmp : StringComparer.Ordinal.Compare(a.SourcePath, b.SourcePath);
    }

    private static bool HasAcceptedExtension(string fileName, string[] acceptedExtensionsDesc)
    {
        foreach (var ext in acceptedExtensionsDesc)
        {
            if (fileName.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryNormalizePath(string rawPath, out string fullPath, out string? error)
    {
        try
        {
            fullPath = Path.GetFullPath(rawPath.Trim());
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            fullPath = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static string NormalizePathKey(string fullPath) =>
        OperatingSystem.IsWindows() ? fullPath.ToUpperInvariant() : fullPath;

    private static bool TryVerifyFileExists(string fullPath, out bool exists, out string? error)
    {
        try
        {
            if (Directory.Exists(fullPath))
            {
                exists = false;
                error = null;
                return true;
            }

            exists = File.Exists(fullPath);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            exists = false;
            error = ex.Message;
            return false;
        }
    }
}
