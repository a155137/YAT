using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;

namespace YAT.Infrastructure.Distribution;

// The files of a YAT release on disk (Task #051), as build/publish.ps1 makes them:
//
//     YAT-v<version>-<rid>/                the release folder, with yat-files.json - its inventory - at its root
//     YAT-v<version>-<rid>.zip             that folder, zipped (one top folder, '/' separated entries)
//     YAT-v<version>-<rid>.zip.sha256      "<sha256>  YAT-v<version>-<rid>.zip"
//     yat-update.json                      the release manifest, when the package's URL is known
//
// It makes the inventory, the checksum and the manifest, and verifies a whole set against itself: names and versions
// agree, the checksum and the manifest are the ZIP's, and the ZIP holds exactly the files its inventory lists - each the
// size and SHA-256 listed - and no user settings. Build-time only: nothing here is used while YAT runs, and nothing here
// connects to anything.
public static class ReleaseArtifacts
{
    // A user's preference files (Task #050), which no release may ever contain - the release rules' own test.
    public static bool IsUserSettingsFile(string fileName) => ReleasePackageVerifier.IsUserSettingsFile(fileName);

    // The inventory of a release folder: every file in it but the inventory itself, hashed. Refuses a folder with user
    // settings in it, or a file whose path cannot be a release path.
    public static ReleaseInventory Scan(string releaseDirectory, ReleaseVersion version, string rid)
    {
        var root = Path.GetFullPath(releaseDirectory);
        var files = new List<ReleaseFile>();
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var info = new FileInfo(file);
            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException($"'{file}' is a link; a release contains files only.");
            }

            if (IsUserSettingsFile(info.Name))
            {
                throw new InvalidOperationException($"The release contains a user settings file: {Path.GetRelativePath(root, file)}.");
            }

            var path = ReleasePath.Normalize(Path.GetRelativePath(root, file))
                ?? throw new InvalidOperationException($"'{file}' cannot be a release path.");
            if (string.Equals(path, ReleaseInventory.FileName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = File.OpenRead(file);
            files.Add(new ReleaseFile(path, info.Length, Sha256(stream)));
        }

        return new ReleaseInventory(version, rid, files);
    }

    public static void WriteInventory(string releaseDirectory, ReleaseInventory inventory) =>
        File.WriteAllBytes(Path.Combine(releaseDirectory, ReleaseInventory.FileName), inventory.ToJson());

    // "<sha256>  <file name>", the checksum file beside a package.
    public static string WriteChecksum(string packagePath)
    {
        var hash = Sha256(packagePath);
        File.WriteAllText(ChecksumPath(packagePath), $"{hash}  {Path.GetFileName(packagePath)}\n", new UTF8Encoding(false));
        return hash;
    }

    public static string ChecksumPath(string packagePath) => packagePath + ".sha256";

    // The manifest of a package: its URL is the base URL the release will be hosted at, and the package's file name.
    // The base URL must be https; nothing is guessed when there is none.
    public static ReleaseManifest CreateManifest(
        ReleaseVersion version,
        string rid,
        string packagePath,
        string packageBaseUrl,
        string? releaseNotesUrl,
        DateTimeOffset publishedAt,
        IReadOnlyList<ReleaseDeltaPackage>? deltas = null)
    {
        if (!ReleaseManifestReader.TryHttps(packageBaseUrl, out var baseUrl))
        {
            throw new ArgumentException($"The package base URL '{packageBaseUrl}' is not an https URL.", nameof(packageBaseUrl));
        }

        Uri? notes = null;
        if (releaseNotesUrl is not null && !ReleaseManifestReader.TryHttps(releaseNotesUrl, out notes))
        {
            throw new ArgumentException($"The release notes URL '{releaseNotesUrl}' is not an https URL.", nameof(releaseNotesUrl));
        }

        var directory = baseUrl!.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        var url = new Uri(directory, Uri.EscapeDataString(Path.GetFileName(packagePath)));
        return new ReleaseManifest(
            version,
            [new ReleasePackage(rid, url, Sha256(packagePath), new FileInfo(packagePath).Length)],
            publishedAt,
            notes,
            deltas);
    }

    public static ReleaseDeltaPackage CreateDeltaManifestEntry(
        ReleaseVersion baseVersion,
        string rid,
        string deltaPackagePath,
        string packageBaseUrl)
    {
        if (!ReleaseManifestReader.TryHttps(packageBaseUrl, out var baseUrl))
        {
            throw new ArgumentException($"The package base URL '{packageBaseUrl}' is not an https URL.", nameof(packageBaseUrl));
        }

        var directory = baseUrl!.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
        var url = new Uri(directory, Uri.EscapeDataString(Path.GetFileName(deltaPackagePath)));
        return new ReleaseDeltaPackage(rid, baseVersion, url, Sha256(deltaPackagePath), new FileInfo(deltaPackagePath).Length);
    }

    // Writes the manifest and reads it back with the reader YAT will use: a manifest that would not read is never left.
    public static void WriteManifest(string manifestPath, ReleaseManifest manifest)
    {
        var json = manifest.ToJson();
        var read = ReleaseManifestReader.Read(Encoding.UTF8.GetString(json), strictDeltas: true);
        if (!read.IsValid)
        {
            throw new InvalidOperationException($"The manifest would not be valid: {string.Join(" ", read.Problems)}");
        }

        File.WriteAllBytes(manifestPath, json);
    }

    public static string DeltaPackageName(ReleaseVersion baseVersion, ReleaseVersion targetVersion, string rid) =>
        $"YAT-{targetVersion.Tag}-from-{baseVersion.Tag}-{rid}-diff.zip";

    public static (ReleaseInventory Inventory, string InventorySha256) VerifyBasePackage(
        string baseZipPath,
        ReleaseVersion baseVersion,
        ReleaseVersion targetVersion,
        string rid,
        string expectedSha256)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseZipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);

        if (targetVersion <= baseVersion)
        {
            throw new InvalidOperationException($"Target version {targetVersion} must be newer than base version {baseVersion}.");
        }

        var normalizedExpected = ReleaseManifestReader.NormalizeSha256(expectedSha256)
            ?? throw new ArgumentException("A trusted 64-hex lower-case SHA-256 is required for the base package.", nameof(expectedSha256));

        if (!File.Exists(baseZipPath))
        {
            throw new FileNotFoundException($"Base package '{baseZipPath}' was not found.", baseZipPath);
        }

        var actualSha = Sha256(baseZipPath);
        if (!string.Equals(actualSha, normalizedExpected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Base package '{baseZipPath}' SHA-256 '{actualSha}' does not match expected '{normalizedExpected}'.");
        }

        using var archive = ZipFile.OpenRead(baseZipPath);
        var contents = ReleasePackageVerifier.Read(archive, baseVersion, rid, verifyContents: true);
        if (!contents.IsValid)
        {
            throw new InvalidOperationException($"Base package '{baseZipPath}' failed verification: {string.Join(" ", contents.Problems.Take(5))}");
        }

        var inventorySha = ComputeEntrySha256(contents.InventoryEntry!);
        return (contents.Inventory!, inventorySha);
    }

    public static ReleaseDeltaManifest CreateDeltaPackage(
        string baseZipPath,
        ReleaseVersion baseVersion,
        string expectedBaseSha256,
        string targetZipPath,
        ReleaseVersion targetVersion,
        string rid,
        string deltaZipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetZipPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(deltaZipPath);

        var (baseInventory, baseInventorySha) = VerifyBasePackage(baseZipPath, baseVersion, targetVersion, rid, expectedBaseSha256);

        if (!File.Exists(targetZipPath))
        {
            throw new FileNotFoundException($"Target package '{targetZipPath}' was not found.", targetZipPath);
        }

        using var targetArchive = ZipFile.OpenRead(targetZipPath);
        var targetContents = ReleasePackageVerifier.Read(targetArchive, targetVersion, rid, verifyContents: true);
        if (!targetContents.IsValid)
        {
            throw new InvalidOperationException($"Target package '{targetZipPath}' failed verification: {string.Join(" ", targetContents.Problems.Take(5))}");
        }

        var targetInventory = targetContents.Inventory!;
        var baseByPath = baseInventory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var targetByPath = targetInventory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        var changedFiles = new List<string>();
        foreach (var targetFile in targetInventory.Files)
        {
            if (!baseByPath.TryGetValue(targetFile.Path, out var baseFile) ||
                baseFile.Size != targetFile.Size ||
                !string.Equals(baseFile.Sha256, targetFile.Sha256, StringComparison.Ordinal))
            {
                changedFiles.Add(targetFile.Path);
            }
        }

        var removedFiles = baseInventory.Files
            .Where(baseFile => !targetByPath.ContainsKey(baseFile.Path))
            .Select(baseFile => baseFile.Path)
            .ToList();

        var deltaManifest = new ReleaseDeltaManifest(baseVersion, targetVersion, rid, baseInventorySha, changedFiles, removedFiles);

        var tempPath = $"{deltaZipPath}.{Guid.NewGuid():N}.tmp";
        var top = targetVersion.PackageName(rid) + "/";
        var changedSet = deltaManifest.ChangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetEntries = targetContents.Files
            .Where(f => changedSet.Contains(f.File.Path))
            .ToDictionary(f => f.File.Path, StringComparer.OrdinalIgnoreCase);

        try
        {
            var entriesToWrite = new List<(string EntryName, Func<byte[]> ReadBytes)>
            {
                (top + ReleaseDeltaManifest.FileName, deltaManifest.ToJson),
                (top + ReleaseInventory.FileName, () => ReadEntryBytes(targetContents.InventoryEntry!))
            };

            foreach (var changedPath in deltaManifest.ChangedFiles)
            {
                var packaged = targetEntries[changedPath];
                entriesToWrite.Add((top + changedPath, () => ReadEntryBytes(packaged.Entry)));
            }

            entriesToWrite.Sort((a, b) => string.CompareOrdinal(a.EntryName, b.EntryName));

            using (var archive = ZipFile.Open(tempPath, ZipArchiveMode.Create))
            {
                foreach (var (entryName, readBytes) in entriesToWrite)
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using var dest = entry.Open();
                    var data = readBytes();
                    dest.Write(data, 0, data.Length);
                }
            }

            File.Move(tempPath, deltaZipPath, overwrite: true);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { }
            throw;
        }

        WriteChecksum(deltaZipPath);
        return deltaManifest;
    }

    public static IReadOnlyList<string> VerifyDeltaReconstruction(
        string baseZipPath,
        string expectedBaseSha256,
        string deltaZipPath,
        string targetZipPath,
        ReleaseVersion baseVersion,
        ReleaseVersion targetVersion,
        string rid)
    {
        var problems = new List<string>();
        ReleaseInventory baseInventory;
        string baseInventorySha;
        try
        {
            (baseInventory, baseInventorySha) = VerifyBasePackage(baseZipPath, baseVersion, targetVersion, rid, expectedBaseSha256);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException or InvalidDataException)
        {
            return [exception.Message];
        }

        if (!File.Exists(deltaZipPath))
        {
            return [$"There is no {Path.GetFileName(deltaZipPath)}."];
        }

        if (!File.Exists(targetZipPath))
        {
            return [$"There is no {Path.GetFileName(targetZipPath)}."];
        }

        try
        {
            using var baseArchive = ZipFile.OpenRead(baseZipPath);
            var baseContents = ReleasePackageVerifier.Read(baseArchive, baseVersion, rid, verifyContents: false);
            if (!baseContents.IsValid)
            {
                problems.AddRange(baseContents.Problems);
                return problems;
            }

            using var targetArchive = ZipFile.OpenRead(targetZipPath);
            var targetContents = ReleasePackageVerifier.Read(targetArchive, targetVersion, rid, verifyContents: true);
            if (!targetContents.IsValid)
            {
                problems.AddRange(targetContents.Problems);
                return problems;
            }

            using var deltaArchive = ZipFile.OpenRead(deltaZipPath);
            var deltaContents = ReleasePackageVerifier.ReadDelta(deltaArchive, baseVersion, targetVersion, rid, baseInventorySha, verifyContents: true);
            if (!deltaContents.IsValid)
            {
                problems.AddRange(deltaContents.Problems);
                return problems;
            }

            return VerifyDeltaReconstructionCore(baseInventory, baseContents, targetContents, deltaContents);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return [exception.Message];
        }
    }

    private static IReadOnlyList<string> VerifyDeltaReconstructionCore(
        ReleaseInventory baseInventory,
        ReleasePackageContents baseContents,
        ReleasePackageContents targetContents,
        ReleaseDeltaPackageContents deltaContents)
    {
        var problems = new List<string>();

        var delta = deltaContents.Delta!;
        var targetInventory = targetContents.Inventory!;
        if (!deltaContents.TargetInventory!.Files.SequenceEqual(targetInventory.Files))
        {
            problems.Add($"The delta package's {ReleaseInventory.FileName} does not match the target package's {ReleaseInventory.FileName}.");
            return problems;
        }

        var baseByPath = baseInventory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var targetByPath = targetInventory.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);

        foreach (var removed in delta.RemovedFiles)
        {
            if (!baseByPath.ContainsKey(removed))
            {
                problems.Add($"Removed file '{removed}' was not owned by base inventory.");
            }
        }

        var expectedChanged = targetInventory.Files
            .Where(t => !baseByPath.TryGetValue(t.Path, out var b) || b.Size != t.Size || !string.Equals(b.Sha256, t.Sha256, StringComparison.Ordinal))
            .Select(t => t.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        var expectedRemoved = baseInventory.Files
            .Where(b => !targetByPath.ContainsKey(b.Path))
            .Select(b => b.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        if (!delta.ChangedFiles.SequenceEqual(expectedChanged, StringComparer.Ordinal))
        {
            problems.Add("Delta changedFiles does not match the exact diff between base and target inventories.");
        }

        if (!delta.RemovedFiles.SequenceEqual(expectedRemoved, StringComparer.Ordinal))
        {
            problems.Add("Delta removedFiles does not match the exact diff between base and target inventories.");
        }

        var baseFilesByPath = baseContents.Files.ToDictionary(f => f.File.Path, StringComparer.OrdinalIgnoreCase);
        var changedSet = delta.ChangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reconstructed = new Dictionary<string, ReleaseFile>(StringComparer.OrdinalIgnoreCase);

        foreach (var changed in deltaContents.ChangedFiles)
        {
            reconstructed[changed.File.Path] = changed.File;
        }

        foreach (var targetFile in targetInventory.Files)
        {
            if (changedSet.Contains(targetFile.Path))
            {
                continue;
            }

            if (!baseFilesByPath.TryGetValue(targetFile.Path, out var keptBaseFile) ||
                !ReleasePackageVerifier.TryCopy(keptBaseFile, Stream.Null))
            {
                problems.Add($"Kept file '{targetFile.Path}' could not be verified from the base package.");
                continue;
            }

            reconstructed[targetFile.Path] = keptBaseFile.File;
        }

        if (reconstructed.Count != targetInventory.Files.Count ||
            targetInventory.Files.Any(t => !reconstructed.TryGetValue(t.Path, out var r) || r != t))
        {
            problems.Add("Reconstructed base + delta file set does not match the target inventory.");
        }

        return problems;
    }

    // Everything wrong with the release set in this folder; empty when it is consistent. requireManifest: an official
    // release must have its manifest; a scratch build may not have a URL for one yet.
    public static IReadOnlyList<string> Verify(
        string outputDirectory,
        ReleaseVersion version,
        string rid,
        bool requireManifest,
        string? previousPackagePath = null,
        string? previousPackageSha256 = null)
    {
        var problems = new List<string>();
        var name = version.PackageName(rid);
        var package = Path.Combine(outputDirectory, name + ".zip");
        if (!File.Exists(package))
        {
            return [$"There is no {name}.zip."];
        }

        var hash = Sha256(package);
        var size = new FileInfo(package).Length;

        var checksum = ChecksumPath(package);
        if (!File.Exists(checksum))
        {
            problems.Add($"There is no {Path.GetFileName(checksum)}.");
        }
        else if (File.ReadAllText(checksum) != $"{hash}  {name}.zip\n")
        {
            problems.Add($"{Path.GetFileName(checksum)} is not the SHA-256 of {name}.zip.");
        }

        var manifestPath = Path.Combine(outputDirectory, ReleaseManifest.FileName);
        ReleaseManifest? manifest = null;
        if (File.Exists(manifestPath))
        {
            manifest = VerifyManifest(File.ReadAllText(manifestPath), version, rid, name + ".zip", hash, size, problems);
        }
        else if (requireManifest)
        {
            problems.Add($"There is no {ReleaseManifest.FileName}.");
        }

        VerifyPackage(package, version, rid, problems);

        if (previousPackagePath is not null)
        {
            if (string.IsNullOrWhiteSpace(previousPackageSha256))
            {
                problems.Add("A trusted base package SHA-256 is required to verify the differential package.");
            }
            else if (!TryParsePackageVersion(previousPackagePath, rid, out var baseVersion))
            {
                problems.Add($"Cannot determine base version from '{Path.GetFileName(previousPackagePath)}'.");
            }
            else
            {
                var deltaName = DeltaPackageName(baseVersion, version, rid);
                var deltaPath = Path.Combine(outputDirectory, deltaName);
                if (!File.Exists(deltaPath))
                {
                    problems.Add($"There is no {deltaName}.");
                }
                else
                {
                    var deltaHash = Sha256(deltaPath);
                    var deltaSize = new FileInfo(deltaPath).Length;
                    var deltaChecksum = ChecksumPath(deltaPath);
                    if (!File.Exists(deltaChecksum))
                    {
                        problems.Add($"There is no {Path.GetFileName(deltaChecksum)}.");
                    }
                    else if (File.ReadAllText(deltaChecksum) != $"{deltaHash}  {deltaName}\n")
                    {
                        problems.Add($"{Path.GetFileName(deltaChecksum)} is not the SHA-256 of {deltaName}.");
                    }

                    if (manifest is not null)
                    {
                        var deltaEntry = manifest.DeltaFor(rid, baseVersion);
                        if (deltaEntry is null)
                        {
                            problems.Add($"{ReleaseManifest.FileName} has no {rid} delta for baseVersion {baseVersion}.");
                        }
                        else
                        {
                            if (Uri.UnescapeDataString(deltaEntry.Url.Segments[^1]) != deltaName)
                            {
                                problems.Add($"{ReleaseManifest.FileName} delta points to {deltaEntry.Url}, not to {deltaName}.");
                            }

                            if (deltaEntry.Sha256 != deltaHash)
                            {
                                problems.Add($"{ReleaseManifest.FileName} delta has SHA-256 {deltaEntry.Sha256}, not {deltaHash}.");
                            }

                            if (deltaEntry.Size != deltaSize)
                            {
                                problems.Add($"{ReleaseManifest.FileName} delta has size {deltaEntry.Size}, not {deltaSize}.");
                            }
                        }
                    }

                    problems.AddRange(VerifyDeltaReconstruction(previousPackagePath, previousPackageSha256, deltaPath, package, baseVersion, version, rid));
                }
            }
        }

        return problems;
    }

    public static bool TryParsePackageVersion(string packagePath, string rid, out ReleaseVersion version)
    {
        version = default;
        var fileName = Path.GetFileName(packagePath);
        var suffix = $"-{rid}.zip";
        if (!fileName.StartsWith("YAT-v", StringComparison.Ordinal) || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var versionText = fileName["YAT-v".Length..^suffix.Length];
        return ReleaseVersion.TryParse(versionText, out version);
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256(stream);
    }

    public static string Sha256(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    private static ReleaseManifest? VerifyManifest(string json, ReleaseVersion version, string rid, string packageName, string hash, long size, List<string> problems)
    {
        var read = ReleaseManifestReader.Read(json, strictDeltas: true);
        if (read.Manifest is not { } manifest)
        {
            problems.Add($"{ReleaseManifest.FileName} is not a valid manifest: {string.Join(" ", read.Problems)}");
            return null;
        }

        if (manifest.Version != version)
        {
            problems.Add($"{ReleaseManifest.FileName} is version {manifest.Version}, not {version}.");
        }

        if (manifest.PackageFor(rid) is not { } entry)
        {
            problems.Add($"{ReleaseManifest.FileName} has no {rid} package.");
            return manifest;
        }

        if (Uri.UnescapeDataString(entry.Url.Segments[^1]) != packageName)
        {
            problems.Add($"{ReleaseManifest.FileName} points to {entry.Url}, not to {packageName}.");
        }

        if (entry.Sha256 != hash)
        {
            problems.Add($"{ReleaseManifest.FileName} has the SHA-256 {entry.Sha256}, not the package's {hash}.");
        }

        if (entry.Size != size)
        {
            problems.Add($"{ReleaseManifest.FileName} has the size {entry.Size}, not the package's {size}.");
        }

        return manifest;
    }

    private static string ComputeEntrySha256(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return Sha256(stream);
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    // The ZIP, by the release package rules (ReleasePackageVerifier) - the same rules the updater installs by.
    private static void VerifyPackage(string package, ReleaseVersion version, string rid, List<string> problems)
    {
        using var archive = ZipFile.OpenRead(package);
        problems.AddRange(ReleasePackageVerifier.Verify(archive, version, rid));
    }
}
