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
        DateTimeOffset publishedAt)
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
            notes);
    }

    // Writes the manifest and reads it back with the reader YAT will use: a manifest that would not read is never left.
    public static void WriteManifest(string manifestPath, ReleaseManifest manifest)
    {
        var json = manifest.ToJson();
        var read = ReleaseManifestReader.Read(Encoding.UTF8.GetString(json));
        if (!read.IsValid)
        {
            throw new InvalidOperationException($"The manifest would not be valid: {string.Join(" ", read.Problems)}");
        }

        File.WriteAllBytes(manifestPath, json);
    }

    // Everything wrong with the release set in this folder; empty when it is consistent. requireManifest: an official
    // release must have its manifest; a scratch build may not have a URL for one yet.
    public static IReadOnlyList<string> Verify(string outputDirectory, ReleaseVersion version, string rid, bool requireManifest)
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
        if (File.Exists(manifestPath))
        {
            VerifyManifest(File.ReadAllText(manifestPath), version, rid, name + ".zip", hash, size, problems);
        }
        else if (requireManifest)
        {
            problems.Add($"There is no {ReleaseManifest.FileName}.");
        }

        VerifyPackage(package, version, rid, problems);
        return problems;
    }

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Sha256(stream);
    }

    public static string Sha256(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));

    private static void VerifyManifest(string json, ReleaseVersion version, string rid, string packageName, string hash, long size, List<string> problems)
    {
        var read = ReleaseManifestReader.Read(json);
        if (read.Manifest is not { } manifest)
        {
            problems.Add($"{ReleaseManifest.FileName} is not a valid manifest: {string.Join(" ", read.Problems)}");
            return;
        }

        if (manifest.Version != version)
        {
            problems.Add($"{ReleaseManifest.FileName} is version {manifest.Version}, not {version}.");
        }

        if (manifest.PackageFor(rid) is not { } entry)
        {
            problems.Add($"{ReleaseManifest.FileName} has no {rid} package.");
            return;
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
    }

    // The ZIP, by the release package rules (ReleasePackageVerifier) - the same rules the updater installs by.
    private static void VerifyPackage(string package, ReleaseVersion version, string rid, List<string> problems)
    {
        using var archive = ZipFile.OpenRead(package);
        problems.AddRange(ReleasePackageVerifier.Verify(archive, version, rid));
    }
}
