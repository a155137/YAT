using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace YAT.Application.Distribution;

// One file of a release package: what its inventory says it is, and the entry that holds it.
public sealed record ReleasePackageFile(ReleaseFile File, ZipArchiveEntry Entry);

// What a release package holds, read against its own inventory: the inventory, every file it lists with the entry that
// holds it (in the inventory's order), and every way the package breaks the release rules. Valid only without problems.
public sealed class ReleasePackageContents
{
    internal ReleasePackageContents(
        ReleaseInventory? inventory,
        ZipArchiveEntry? inventoryEntry,
        IReadOnlyList<ReleasePackageFile> files,
        IReadOnlyList<string> problems)
    {
        Inventory = inventory;
        InventoryEntry = inventoryEntry;
        Files = files;
        Problems = problems;
    }

    public ReleaseInventory? Inventory { get; }

    // The entry of yat-files.json itself.
    public ZipArchiveEntry? InventoryEntry { get; }

    public IReadOnlyList<ReleasePackageFile> Files { get; }

    public IReadOnlyList<string> Problems { get; }

    public bool IsValid => Problems.Count == 0 && Inventory is not null && InventoryEntry is not null;
}

public sealed class ReleaseDeltaPackageContents
{
    internal ReleaseDeltaPackageContents(
        ReleaseDeltaManifest? delta,
        ReleaseInventory? targetInventory,
        ZipArchiveEntry? deltaEntry,
        ZipArchiveEntry? inventoryEntry,
        IReadOnlyList<ReleasePackageFile> changedFiles,
        IReadOnlyList<string> problems)
    {
        Delta = delta;
        TargetInventory = targetInventory;
        DeltaEntry = deltaEntry;
        InventoryEntry = inventoryEntry;
        ChangedFiles = changedFiles;
        Problems = problems;
    }

    public ReleaseDeltaManifest? Delta { get; }

    public ReleaseInventory? TargetInventory { get; }

    public ZipArchiveEntry? DeltaEntry { get; }

    public ZipArchiveEntry? InventoryEntry { get; }

    public IReadOnlyList<ReleasePackageFile> ChangedFiles { get; }

    public IReadOnlyList<string> Problems { get; }

    public bool IsValid => Problems.Count == 0 && Delta is not null && TargetInventory is not null && DeltaEntry is not null && InventoryEntry is not null;
}

// The rules a release package - YAT-v<version>-<rid>.zip - must keep (Tasks #051.A and #051.C), written once: the
// release script verifies what it made with them (ReleaseArtifacts) and the updater verifies what it is about to
// install with them (YAT.Updater) - from the package itself, trusting nothing that said it was verified before.
//
//     one top folder named after the release; '/' separated entries, each a safe release path (ReleasePath);
//     no entry twice (ignoring case); no user settings; yat-files.json for this version and runtime;
//     exactly the files the inventory lists - each the size and SHA-256 listed.
//
// BCL only; nothing here touches the file system.
public static class ReleasePackageVerifier
{
    // An inventory larger than this is not one (and is never read whole).
    public const long MaximumInventoryBytes = 16L * 1024 * 1024;

    // Maximum ZIP entries accepted in a single release or delta archive (ZIP bomb protection).
    public const int MaximumEntryCount = 4096;

    private const int BufferSize = 81920;

    // A user's preference files (Task #050), which no release may ever contain.
    public static bool IsUserSettingsFile(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        return fileName.StartsWith("graph-palettes", StringComparison.OrdinalIgnoreCase)
            && (fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase));
    }

    // Every rule, the files' contents included; empty when the package is this release.
    public static IReadOnlyList<string> Verify(ZipArchive archive, ReleaseVersion version, string rid) =>
        Read(archive, version, rid, verifyContents: true).Problems;

    // The package against its inventory. Without verifyContents the files' bytes are not read: whoever extracts them
    // checks each with TryCopy as it is written.
    public static ReleasePackageContents Read(ZipArchive archive, ReleaseVersion version, string rid, bool verifyContents = false)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);

        var problems = new List<string>();
        if (archive.Entries.Count > MaximumEntryCount)
        {
            problems.Add($"The package contains {archive.Entries.Count} entries, exceeding the limit of {MaximumEntryCount}.");
            return new ReleasePackageContents(null, null, [], problems);
        }

        var top = version.PackageName(rid) + "/";
        var entries = CollectPackageEntries(archive, top, rejectReservedPaths: false, problems);

        if (!entries.Remove(ReleaseInventory.FileName, out var inventoryEntry))
        {
            problems.Add($"The package has no {ReleaseInventory.FileName}.");
            return new ReleasePackageContents(null, null, [], problems);
        }

        if (!TryReadEntryUtf8(inventoryEntry, MaximumInventoryBytes, ReleaseInventory.FileName, out var json, out var readProblem))
        {
            problems.Add(readProblem!);
            return new ReleasePackageContents(null, null, [], problems);
        }

        if (!ReleaseInventory.TryRead(json, out var inventory, out var problem))
        {
            problems.Add($"The package's {ReleaseInventory.FileName} is not an inventory: {problem}");
            return new ReleasePackageContents(null, null, [], problems);
        }

        if (inventory!.Version != version || !string.Equals(inventory.Rid, rid, StringComparison.Ordinal))
        {
            problems.Add($"The package's inventory is {inventory.Version} {inventory.Rid}, not {version} {rid}.");
        }

        var files = new List<ReleasePackageFile>();
        foreach (var file in inventory.Files)
        {
            if (!entries.Remove(file.Path, out var entry))
            {
                problems.Add($"The package has no {file.Path}, which its inventory lists.");
                continue;
            }

            var packaged = new ReleasePackageFile(file, entry);
            files.Add(packaged);
            if (verifyContents && !TryCopy(packaged, Stream.Null))
            {
                problems.Add(NotTheListedFile(file.Path));
            }
        }

        foreach (var extra in entries.Keys.Order(StringComparer.Ordinal))
        {
            problems.Add($"The package contains {extra}, which its inventory does not list.");
        }

        return new ReleasePackageContents(inventory, inventoryEntry, files, problems);
    }

    // Copies one file of the package to the destination, checking that it is exactly the file its inventory lists: the
    // length its entry declares, the bytes it holds - never more than the listed size is read, whatever the entry
    // claims - and their SHA-256. False when it is not (the destination may then hold part of it).
    public static bool TryCopy(ReleasePackageFile file, Stream destination)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(destination);
        if (file.Entry.Length != file.File.Size)
        {
            return false;
        }

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var source = file.Entry.Open();
            var buffer = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > file.File.Size)
                {
                    return false;
                }

                hash.AppendData(buffer, 0, read);
                destination.Write(buffer, 0, read);
            }

            return total == file.File.Size && Convert.ToHexStringLower(hash.GetHashAndReset()) == file.File.Sha256;
        }
        catch (InvalidDataException)
        {
            // Damaged compressed data.
            return false;
        }
    }

    public static string NotTheListedFile(string path) => $"The package's {path} is not the file its inventory lists.";

    public static ReleaseDeltaPackageContents ReadDelta(
        ZipArchive archive,
        ReleaseVersion baseVersion,
        ReleaseVersion targetVersion,
        string rid,
        string? expectedBaseInventorySha256 = null,
        bool verifyContents = false)
    {
        ArgumentNullException.ThrowIfNull(archive);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);

        var problems = new List<string>();
        if (targetVersion <= baseVersion)
        {
            problems.Add($"Target version {targetVersion} must be newer than base version {baseVersion}.");
            return new ReleaseDeltaPackageContents(null, null, null, null, [], problems);
        }

        if (archive.Entries.Count > MaximumEntryCount)
        {
            problems.Add($"The delta package contains {archive.Entries.Count} entries, exceeding the limit of {MaximumEntryCount}.");
            return new ReleaseDeltaPackageContents(null, null, null, null, [], problems);
        }

        var top = targetVersion.PackageName(rid) + "/";
        var entries = CollectPackageEntries(archive, top, rejectReservedPaths: true, problems);

        if (!entries.Remove(ReleaseDeltaManifest.FileName, out var deltaEntry))
        {
            problems.Add($"The delta package has no {ReleaseDeltaManifest.FileName}.");
            return new ReleaseDeltaPackageContents(null, null, null, null, [], problems);
        }

        if (!entries.Remove(ReleaseInventory.FileName, out var inventoryEntry))
        {
            problems.Add($"The delta package has no {ReleaseInventory.FileName}.");
            return new ReleaseDeltaPackageContents(null, null, deltaEntry, null, [], problems);
        }

        if (!TryReadEntryUtf8(deltaEntry, MaximumInventoryBytes, ReleaseDeltaManifest.FileName, out var deltaJson, out var deltaReadProblem))
        {
            problems.Add(deltaReadProblem!);
            return new ReleaseDeltaPackageContents(null, null, deltaEntry, inventoryEntry, [], problems);
        }

        if (!ReleaseDeltaManifest.TryRead(deltaJson, out var delta, out var deltaProblem))
        {
            problems.Add($"The delta package's {ReleaseDeltaManifest.FileName} is invalid: {deltaProblem}");
            return new ReleaseDeltaPackageContents(null, null, deltaEntry, inventoryEntry, [], problems);
        }

        if (delta!.FromVersion != baseVersion || delta.ToVersion != targetVersion || !string.Equals(delta.Rid, rid, StringComparison.Ordinal))
        {
            problems.Add($"The delta manifest is {delta.FromVersion} -> {delta.ToVersion} ({delta.Rid}), not {baseVersion} -> {targetVersion} ({rid}).");
        }

        if (expectedBaseInventorySha256 is not null &&
            !string.Equals(delta.BaseInventorySha256, expectedBaseInventorySha256, StringComparison.Ordinal))
        {
            problems.Add("The delta manifest baseInventorySha256 does not match the installed inventory.");
        }

        if (!TryReadEntryUtf8(inventoryEntry, MaximumInventoryBytes, ReleaseInventory.FileName, out var inventoryJson, out var invReadProblem))
        {
            problems.Add(invReadProblem!);
            return new ReleaseDeltaPackageContents(delta, null, deltaEntry, inventoryEntry, [], problems);
        }

        if (!ReleaseInventory.TryRead(inventoryJson, out var inventory, out var invProblem))
        {
            problems.Add($"The delta package's {ReleaseInventory.FileName} is not an inventory: {invProblem}");
            return new ReleaseDeltaPackageContents(delta, null, deltaEntry, inventoryEntry, [], problems);
        }

        if (inventory!.Version != targetVersion || !string.Equals(inventory.Rid, rid, StringComparison.Ordinal))
        {
            problems.Add($"The delta package's inventory is {inventory.Version} {inventory.Rid}, not {targetVersion} {rid}.");
        }

        var inventoryByPath = inventory.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var removed in delta.RemovedFiles)
        {
            if (inventoryByPath.ContainsKey(removed))
            {
                problems.Add($"The delta manifest lists '{removed}' in removedFiles, but it is present in {ReleaseInventory.FileName}.");
            }
        }

        var changedFiles = new List<ReleasePackageFile>(delta.ChangedFiles.Count);
        foreach (var changedPath in delta.ChangedFiles)
        {
            if (!inventoryByPath.TryGetValue(changedPath, out var listedFile))
            {
                problems.Add($"The delta manifest lists '{changedPath}' in changedFiles, but it is absent from {ReleaseInventory.FileName}.");
                continue;
            }

            if (!entries.Remove(changedPath, out var entry))
            {
                problems.Add($"The delta package has no {changedPath}, which {ReleaseDeltaManifest.FileName} lists.");
                continue;
            }

            var packaged = new ReleasePackageFile(listedFile, entry);
            changedFiles.Add(packaged);
            if (verifyContents && !TryCopy(packaged, Stream.Null))
            {
                problems.Add(NotTheListedFile(changedPath));
            }
        }

        foreach (var extra in entries.Keys.Order(StringComparer.Ordinal))
        {
            problems.Add($"The delta package contains {extra}, which {ReleaseDeltaManifest.FileName} does not list.");
        }

        return new ReleaseDeltaPackageContents(delta, inventory, deltaEntry, inventoryEntry, changedFiles, problems);
    }

    private static Dictionary<string, ZipArchiveEntry> CollectPackageEntries(
        ZipArchive archive,
        string top,
        bool rejectReservedPaths,
        List<string> problems)
    {
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            if (IsSymlinkEntry(entry))
            {
                problems.Add($"The package entry '{entry.FullName}' is a symbolic link.");
                continue;
            }

            if (!entry.FullName.StartsWith(top, StringComparison.Ordinal))
            {
                problems.Add($"The package entry '{entry.FullName}' is not inside {top}.");
                continue;
            }

            var relative = entry.FullName[top.Length..];
            if (relative.Length == 0 || relative.EndsWith('/'))
            {
                continue;
            }

            if (entry.FullName.Contains('\\') || ReleasePath.Normalize(relative) != relative)
            {
                problems.Add($"The package entry '{entry.FullName}' is not a safe release path.");
                continue;
            }

            if (rejectReservedPaths && ReleaseInstallation.IsReserved(relative))
            {
                problems.Add($"The package contains a reserved updater path: {relative}.");
                continue;
            }

            if (IsUserSettingsFile(entry.Name))
            {
                problems.Add($"The package contains a user settings file: {relative}.");
            }

            if (!entries.TryAdd(relative, entry))
            {
                problems.Add($"The package contains '{relative}' twice.");
            }
        }

        return entries;
    }

    private static bool IsSymlinkEntry(ZipArchiveEntry entry) =>
        ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;

    private static bool TryReadEntryUtf8(
        ZipArchiveEntry entry,
        long maximumBytes,
        string displayName,
        out string text,
        out string? problem)
    {
        text = string.Empty;
        if (entry.Length > maximumBytes)
        {
            problem = $"The package's {displayName} is larger than {maximumBytes / 1024 / 1024} MB.";
            return false;
        }

        try
        {
            using var source = entry.Open();
            using var buffer = new MemoryStream();
            var chunk = new byte[BufferSize];
            long total = 0;
            int read;
            while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > maximumBytes)
                {
                    problem = $"The package's {displayName} exceeds {maximumBytes / 1024 / 1024} MB while reading.";
                    return false;
                }

                buffer.Write(chunk, 0, read);
            }

            text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            problem = null;
            return true;
        }
        catch (InvalidDataException exception)
        {
            problem = $"The package's {displayName} cannot be read: {exception.Message}";
            return false;
        }
    }
}
