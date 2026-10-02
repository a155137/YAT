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
        var top = version.PackageName(rid) + "/";
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
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

            if (IsUserSettingsFile(entry.Name))
            {
                problems.Add($"The package contains a user settings file: {relative}.");
            }

            if (!entries.TryAdd(relative, entry))
            {
                problems.Add($"The package contains '{relative}' twice.");
            }
        }

        if (!entries.Remove(ReleaseInventory.FileName, out var inventoryEntry))
        {
            problems.Add($"The package has no {ReleaseInventory.FileName}.");
            return new ReleasePackageContents(null, null, [], problems);
        }

        if (inventoryEntry.Length > MaximumInventoryBytes)
        {
            problems.Add($"The package's {ReleaseInventory.FileName} is larger than {MaximumInventoryBytes / 1024 / 1024} MB.");
            return new ReleasePackageContents(null, null, [], problems);
        }

        string json;
        try
        {
            using var reader = new StreamReader(inventoryEntry.Open(), Encoding.UTF8);
            json = reader.ReadToEnd();
        }
        catch (InvalidDataException exception)
        {
            problems.Add($"The package's {ReleaseInventory.FileName} cannot be read: {exception.Message}");
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
}
