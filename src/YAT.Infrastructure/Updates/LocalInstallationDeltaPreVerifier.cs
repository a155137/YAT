using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Infrastructure.Updates;

// Verifies that the local installation matches the base inventory and that every kept file is intact (size, SHA-256,
// no symlinks/junctions/reparse points) before allowing a differential update package to be accepted. Read-only: never
// modifies any installed file or user settings.
public sealed class LocalInstallationDeltaPreVerifier : ILocalDeltaPreVerifier
{
    public bool IsLocalInventoryEligible(string installationDirectory, ReleaseVersion expectedBaseVersion, string expectedRid)
    {
        try
        {
            return TryReadLocalInventory(installationDirectory, expectedBaseVersion, expectedRid, out _, out _, out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public async Task<DeltaPreVerificationResult> VerifyLocalAsync(
        string installationDirectory,
        ReleaseVersion expectedBaseVersion,
        string expectedRid,
        ReleaseDeltaManifest deltaManifest,
        ReleaseInventory targetInventory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRid);
        ArgumentNullException.ThrowIfNull(deltaManifest);
        ArgumentNullException.ThrowIfNull(targetInventory);

        cancellationToken.ThrowIfCancellationRequested();

        if (!TryReadLocalInventory(installationDirectory, expectedBaseVersion, expectedRid, out var localInventory, out var localInventorySha, out var readProblem))
        {
            return new DeltaPreVerificationResult(false, readProblem);
        }

        if (!string.Equals(localInventorySha, deltaManifest.BaseInventorySha256, StringComparison.Ordinal))
        {
            return new DeltaPreVerificationResult(false, "Local yat-files.json SHA-256 does not match delta baseInventorySha256.");
        }

        var localByPath = localInventory!.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var targetByPath = targetInventory.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);

        var expectedChanged = targetInventory.Files
            .Where(t => !localByPath.TryGetValue(t.Path, out var b) || b.Size != t.Size || !string.Equals(b.Sha256, t.Sha256, StringComparison.Ordinal))
            .Select(t => t.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var expectedRemoved = localInventory.Files
            .Where(b => !targetByPath.ContainsKey(b.Path))
            .Select(b => b.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        if (!deltaManifest.ChangedFiles.SequenceEqual(expectedChanged, StringComparer.Ordinal))
        {
            return new DeltaPreVerificationResult(false, "Delta changedFiles does not match local vs target inventory diff.");
        }

        if (!deltaManifest.RemovedFiles.SequenceEqual(expectedRemoved, StringComparer.Ordinal))
        {
            return new DeltaPreVerificationResult(false, "Delta removedFiles does not match local vs target inventory diff.");
        }

        var root = Path.GetFullPath(installationDirectory);
        var rootWithSep = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var changedSet = deltaManifest.ChangedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var targetFile in targetInventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (changedSet.Contains(targetFile.Path))
            {
                continue;
            }

            if (ReleasePath.Normalize(targetFile.Path) != targetFile.Path)
            {
                return new DeltaPreVerificationResult(false, $"Unsafe kept file path: {targetFile.Path}");
            }

            var fullPath = Path.GetFullPath(ReleaseInstallation.PathOf(root, targetFile.Path));
            if (!fullPath.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase))
            {
                return new DeltaPreVerificationResult(false, $"Kept file escapes installation directory: {targetFile.Path}");
            }

            if (HasReparsePointAncestorOrSelf(root, fullPath))
            {
                return new DeltaPreVerificationResult(false, $"Kept file or ancestor directory is a symlink/junction: {targetFile.Path}");
            }

            if (!File.Exists(fullPath))
            {
                return new DeltaPreVerificationResult(false, $"Kept file is missing: {targetFile.Path}");
            }

            try
            {
                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                if (stream.Length != targetFile.Size)
                {
                    return new DeltaPreVerificationResult(false, $"Kept file size mismatch: {targetFile.Path}");
                }

                var actualSha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
                if (!string.Equals(actualSha, targetFile.Sha256, StringComparison.Ordinal))
                {
                    return new DeltaPreVerificationResult(false, $"Kept file SHA-256 mismatch: {targetFile.Path}");
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new DeltaPreVerificationResult(false, $"Kept file could not be read: {targetFile.Path}");
            }
        }

        return new DeltaPreVerificationResult(true);
    }

    private static bool TryReadLocalInventory(
        string installationDirectory,
        ReleaseVersion expectedBaseVersion,
        string expectedRid,
        out ReleaseInventory? inventory,
        out string? inventorySha256,
        out string? problem)
    {
        inventory = null;
        inventorySha256 = null;

        if (string.IsNullOrWhiteSpace(installationDirectory) || !Directory.Exists(installationDirectory))
        {
            problem = "Installation directory does not exist.";
            return false;
        }

        var dirInfo = new DirectoryInfo(installationDirectory);
        if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || dirInfo.LinkTarget is not null)
        {
            problem = "Installation directory is a symlink or junction.";
            return false;
        }

        var invPath = ReleaseInstallation.InventoryPath(installationDirectory);
        if (!File.Exists(invPath))
        {
            problem = "Local yat-files.json is missing.";
            return false;
        }

        var fileInfo = new FileInfo(invPath);
        if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || fileInfo.LinkTarget is not null)
        {
            problem = "Local yat-files.json is a symlink.";
            return false;
        }

        if (fileInfo.Length > ReleasePackageVerifier.MaximumInventoryBytes)
        {
            problem = "Local yat-files.json exceeds maximum size.";
            return false;
        }

        var bytes = File.ReadAllBytes(invPath);
        inventorySha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var json = Encoding.UTF8.GetString(bytes);
        if (!ReleaseInventory.TryRead(json, out inventory, out var parseProblem))
        {
            problem = $"Local yat-files.json is invalid: {parseProblem}";
            return false;
        }

        if (inventory!.Version != expectedBaseVersion || !string.Equals(inventory.Rid, expectedRid, StringComparison.Ordinal))
        {
            problem = $"Local yat-files.json is {inventory.Version} {inventory.Rid}, expected {expectedBaseVersion} {expectedRid}.";
            inventory = null;
            return false;
        }

        if (inventory.Files.Any(file => ReleaseInstallation.IsProtected(file.Path)))
        {
            problem = "Local yat-files.json claims a protected updater or UserData path.";
            inventory = null;
            return false;
        }

        problem = null;
        return true;
    }

    private static bool HasReparsePointAncestorOrSelf(string root, string fullFilePath)
    {
        if (File.Exists(fullFilePath))
        {
            var fileInfo = new FileInfo(fullFilePath);
            if (fileInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || fileInfo.LinkTarget is not null)
            {
                return true;
            }
        }

        var current = Path.GetDirectoryName(fullFilePath);
        var normalizedRoot = Path.GetFullPath(Path.TrimEndingDirectorySeparator(root));
        while (!string.IsNullOrEmpty(current) &&
               !string.Equals(Path.GetFullPath(Path.TrimEndingDirectorySeparator(current)), normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current))
            {
                var dirInfo = new DirectoryInfo(current);
                if (dirInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || dirInfo.LinkTarget is not null)
                {
                    return true;
                }
            }

            current = Path.GetDirectoryName(current);
        }

        return false;
    }
}
