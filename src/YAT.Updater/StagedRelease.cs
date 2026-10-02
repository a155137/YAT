using System.IO.Compression;
using System.Security.Cryptography;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Updater;

// The new release, verified again from the package YAT downloaded - offline, trusting nothing YAT said about it - and
// extracted into <installation>\.yat-update\staging\v<version>\ (Task #051.C):
//
//     1. the manifest snapshot beside the package is a valid manifest of this version, with a win-x64 package;
//     2. the package file is that size and has that SHA-256 (held open, unwritable, from here to the end);
//     3. the package keeps every release package rule (ReleasePackageVerifier): one top folder, safe paths, no entry
//        twice, its inventory for this version and runtime, exactly the inventory's files;
//     4. the inventory owns nothing in .yat-update and has YAT.exe;
//     5. each file is written here by the updater - at the inventory's path, which must stay inside the staging folder -
//        and checked as it is written: never more bytes than listed, the listed size and SHA-256;
//     6. the staging folder then holds exactly the inventory's files and yat-files.json, and no links.
//
// The installation itself is never written. Anything wrong removes the staging folder and throws UpdaterException.
internal sealed class StagedRelease
{
    public const long MaximumManifestBytes = 1024 * 1024;

    private StagedRelease(ReleaseVersion version, string folder, ReleaseInventory inventory, byte[] inventoryJson)
    {
        Version = version;
        Folder = folder;
        Inventory = inventory;
        InventoryJson = inventoryJson;
    }

    public ReleaseVersion Version { get; }

    public string Folder { get; }

    public ReleaseInventory Inventory { get; }

    // yat-files.json exactly as the package has it.
    public byte[] InventoryJson { get; }

    // afterFile: a test seam, called after each file is extracted (to interrupt an extraction).
    public static StagedRelease Prepare(string installation, string updatesRoot, ReleaseVersion version, UpdaterLog log, Action<string>? afterFile = null)
    {
        var manifest = ReadSnapshot(updatesRoot, version);
        var package = manifest.PackageFor(ReleaseInstallation.Rid)
            ?? throw new UpdaterException($"The update information of {version} has no {ReleaseInstallation.Rid} package.");
        var zip = UpdatePackageLayout.PackagePath(updatesRoot, version, ReleaseInstallation.Rid);
        var folder = ReleaseInstallation.StagingFolder(installation, version);

        FileStream stream;
        try
        {
            stream = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdaterException($"The update package {zip} cannot be opened: {exception.Message}", exception);
        }

        using (stream)
        {
            if (stream.Length != package.Size)
            {
                throw new UpdaterException($"The update package is {stream.Length} bytes, not the {package.Size} its update information gives.");
            }

            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != package.Sha256)
            {
                throw new UpdaterException("The update package does not have the SHA-256 its update information gives.");
            }

            stream.Position = 0;
            log.Write($"Package verified: {zip} ({package.Size} bytes, SHA-256 {package.Sha256}).");
            try
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
                return Extract(archive, version, folder, log, afterFile);
            }
            catch (InvalidDataException exception)
            {
                Remove(folder);
                throw new UpdaterException($"The update package is not a valid ZIP file: {exception.Message}", exception);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Remove(folder);
                throw new UpdaterException($"The update could not be extracted: {exception.Message}", exception);
            }
            catch
            {
                Remove(folder);
                throw;
            }
        }
    }

    // The staging folder of a version, removed (it is the updater's own; nothing else is ever in it) - and the folder of
    // staging folders with it when no other is left.
    public static void Remove(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(folder));
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ReleaseManifest ReadSnapshot(string updatesRoot, ReleaseVersion version)
    {
        var path = UpdatePackageLayout.SnapshotPath(updatesRoot, version);
        string json;
        try
        {
            if (new FileInfo(path).Length > MaximumManifestBytes)
            {
                throw new UpdaterException($"The update information {path} is larger than 1 MB.");
            }

            json = File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdaterException($"The update information {path} cannot be read: {exception.Message}", exception);
        }

        var read = ReleaseManifestReader.Read(json);
        if (read.Manifest is not { } manifest || !read.IsValid)
        {
            throw new UpdaterException($"The update information is not valid: {string.Join(" ", read.Problems)}");
        }

        if (manifest.Version != version)
        {
            throw new UpdaterException($"The update information is for {manifest.Version}, not {version}.");
        }

        return manifest;
    }

    private static StagedRelease Extract(ZipArchive archive, ReleaseVersion version, string folder, UpdaterLog log, Action<string>? afterFile)
    {
        var contents = ReleasePackageVerifier.Read(archive, version, ReleaseInstallation.Rid);
        if (!contents.IsValid)
        {
            throw new UpdaterException($"The update package is not a valid YAT {version} release: {string.Join(" ", contents.Problems.Take(5))}");
        }

        var inventory = contents.Inventory!;
        if (inventory.Files.FirstOrDefault(file => ReleaseInstallation.IsReserved(file.Path)) is { } reserved)
        {
            throw new UpdaterException($"The update package claims {reserved.Path}, which belongs to the updater.");
        }

        if (!inventory.Files.Any(file => file.Path == ReleaseInstallation.ExecutableName))
        {
            throw new UpdaterException($"The update package has no {ReleaseInstallation.ExecutableName}.");
        }

        Remove(folder);
        if (Directory.Exists(folder))
        {
            throw new UpdaterException($"The old staging folder {folder} cannot be removed.");
        }

        Directory.CreateDirectory(folder);
        var root = Path.GetFullPath(folder);
        CheckSpace(root, inventory.Files.Sum(file => file.Size));

        foreach (var file in contents.Files)
        {
            var target = Inside(root, file.File.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                if (!ReleasePackageVerifier.TryCopy(file, output))
                {
                    throw new UpdaterException(ReleasePackageVerifier.NotTheListedFile(file.File.Path));
                }
            }

            afterFile?.Invoke(file.File.Path);
        }

        // yat-files.json as the package has it - and it must read as the inventory just verified.
        byte[] json;
        using (var source = contents.InventoryEntry!.Open())
        using (var buffer = new MemoryStream())
        {
            source.CopyTo(buffer);
            json = buffer.ToArray();
        }

        if (!ReleaseInventory.TryRead(System.Text.Encoding.UTF8.GetString(json), out var again, out _)
            || again!.Version != inventory.Version || !again.Files.SequenceEqual(inventory.Files))
        {
            throw new UpdaterException($"The package's {ReleaseInventory.FileName} changed while it was read.");
        }

        File.WriteAllBytes(Path.Combine(root, ReleaseInventory.FileName), json);

        // Exactly the inventory's files and the inventory, and no links.
        var expected = inventory.Files.Select(file => file.Path).Append(ReleaseInventory.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, entry.FullName).Replace('\\', '/');
            if (entry.LinkTarget is not null)
            {
                throw new UpdaterException($"The staged release has a link: {relative}.");
            }

            if (entry is FileInfo && !expected.Remove(relative))
            {
                throw new UpdaterException($"The staged release has a file its inventory does not list: {relative}.");
            }
        }

        if (expected.Count > 0)
        {
            throw new UpdaterException($"The staged release lacks {expected.First()}.");
        }

        log.Write($"Staged {inventory.Files.Count} files ({inventory.Files.Sum(file => file.Size)} bytes) in {root}.");
        return new StagedRelease(version, root, inventory, json);
    }

    // The path of a release path inside a folder - which must stay inside it.
    public static string Inside(string root, string releasePath)
    {
        if (ReleasePath.Normalize(releasePath) != releasePath)
        {
            throw new UpdaterException($"'{releasePath}' is not a release path.");
        }

        var path = Path.GetFullPath(ReleaseInstallation.PathOf(root, releasePath));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new UpdaterException($"'{releasePath}' would be outside {root}.");
        }

        return path;
    }

    private static void CheckSpace(string folder, long bytes)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            if (drive.IsReady && drive.AvailableFreeSpace < bytes + (64L * 1024 * 1024))
            {
                throw new UpdaterException($"There is not enough free disk space to install the update ({bytes / 1024 / 1024} MB and some room are needed).");
            }
        }
        catch (ArgumentException)
        {
            // A network share has no drive to ask; writing will tell.
        }
    }
}
