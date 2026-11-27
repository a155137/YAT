using System.Security;
using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.ImportProfiles;

namespace YAT.Infrastructure.ImportProfiles;

// Portable per-file JSON store for FT Import Profiles (FT-01A).
// Default location: <AppContext.BaseDirectory>/UserData/ImportProfiles/ (never %APPDATA%, never CWD-relative).
public sealed class JsonImportProfileStore : IImportProfileStore
{
    private readonly ImportProfileValidator _validator = new();

    public JsonImportProfileStore()
        : this(DefaultDirectoryPath)
    {
    }

    public JsonImportProfileStore(string directoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        DirectoryPath = Path.GetFullPath(directoryPath);
    }

    public static string DefaultDirectoryPath =>
        Path.Combine(AppContext.BaseDirectory, "UserData", "ImportProfiles");

    public string DirectoryPath { get; }

    string? IImportProfileStore.StorageDirectoryPath => DirectoryPath;

    // Test-only fault injection for atomic write verification. Always null in production.
    internal Action<ImportProfileWritePhase>? WritePhaseHook { get; set; }

    public ImportProfileStoreSnapshot Enumerate()
    {
        if (!Directory.Exists(DirectoryPath))
        {
            return ImportProfileStoreSnapshot.Empty;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(DirectoryPath, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return new ImportProfileStoreSnapshot(
                [],
                [new ImportProfileLoadIssue(ImportProfileLoadIssueKind.FileUnreadable, ".", ex.Message)]);
        }

        var loadedById = new Dictionary<Guid, (string FileName, ImportProfile Profile)>();
        var issues = new List<ImportProfileLoadIssue>();

        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            if (IsReparsePoint(filePath))
            {
                issues.Add(new(ImportProfileLoadIssueKind.FileUnreadable, fileName, "Symbolic link or reparse point files are not loaded."));
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(filePath);
            }
            catch (Exception ex) when (IsFileSystemFailure(ex))
            {
                issues.Add(new(ImportProfileLoadIssueKind.FileUnreadable, fileName, ex.Message));
                continue;
            }

            ImportProfile profile;
            try
            {
                profile = ImportProfileJsonSerializer.Deserialize(bytes);
            }
            catch (ImportProfileStoreException ex) when (ex.Error == ImportProfileStoreError.UnsupportedSchemaVersion)
            {
                issues.Add(new(ImportProfileLoadIssueKind.UnsupportedSchemaVersion, fileName, ex.Message));
                continue;
            }
            catch (ImportProfileStoreException ex)
            {
                issues.Add(new(ImportProfileLoadIssueKind.MalformedJson, fileName, ex.Message));
                continue;
            }

            var validation = _validator.Validate(profile);
            if (!validation.IsValid)
            {
                issues.Add(new(ImportProfileLoadIssueKind.InvalidProfile, fileName, validation.Errors[0].Message));
                continue;
            }

            if (Guid.TryParse(Path.GetFileNameWithoutExtension(fileName), out var fileGuid) && fileGuid != profile.Id)
            {
                issues.Add(new(
                    ImportProfileLoadIssueKind.InvalidProfile,
                    fileName,
                    $"Filename GUID '{fileGuid:D}' does not match internal profile Id '{profile.Id:D}'."));
                continue;
            }

            var canonicalFileName = CanonicalFileName(profile.Id);
            if (loadedById.TryGetValue(profile.Id, out var existing))
            {
                // Prefer canonical "<id>.json" deterministically when duplicate identities exist across files.
                if (string.Equals(fileName, canonicalFileName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(existing.FileName, canonicalFileName, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new(ImportProfileLoadIssueKind.DuplicateId, existing.FileName, $"Duplicate profile id '{profile.Id:D}' conflicts with '{canonicalFileName}'."));
                    loadedById[profile.Id] = (fileName, profile);
                }
                else
                {
                    issues.Add(new(ImportProfileLoadIssueKind.DuplicateId, fileName, $"Duplicate profile id '{profile.Id:D}' already loaded from '{existing.FileName}'."));
                }

                continue;
            }

            loadedById[profile.Id] = (fileName, profile);
        }

        var profiles = loadedById.Values
            .Select(entry => entry.Profile)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Id)
            .ToArray();

        return new ImportProfileStoreSnapshot(profiles, issues);
    }

    public ImportProfile? Load(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.InvalidProfile, "Profile Id must not be empty.");
        }

        var path = ResolveProfilePath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        if (IsReparsePoint(path))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, "The profile file is a symbolic link or reparse point.");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, $"Could not read profile '{id:D}'.", ex);
        }

        var profile = ImportProfileJsonSerializer.Deserialize(bytes);
        if (profile.Id != id)
        {
            throw new ImportProfileStoreException(
                ImportProfileStoreError.DuplicateIdentity,
                $"File '{Path.GetFileName(path)}' contains mismatched profile Id '{profile.Id:D}'.");
        }

        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.InvalidProfile, validation.Errors[0].Message);
        }

        return profile;
    }

    public void Create(ImportProfile profile)
    {
        ValidateForWrite(profile);
        EnsureDirectorySafe();

        var targetPath = ResolveProfilePath(profile.Id);
        if (File.Exists(targetPath) || Directory.Exists(targetPath))
        {
            throw new ImportProfileStoreException(
                ImportProfileStoreError.AlreadyExists,
                $"A profile with Id '{profile.Id:D}' already exists.");
        }

        // Also check whether another file in the directory already claims this Id.
        var snapshot = Enumerate();
        if (snapshot.Profiles.Any(p => p.Id == profile.Id))
        {
            throw new ImportProfileStoreException(
                ImportProfileStoreError.DuplicateIdentity,
                $"Another profile file in '{DirectoryPath}' already uses Id '{profile.Id:D}'.");
        }

        WriteAtomic(targetPath, ImportProfileJsonSerializer.Serialize(profile), overwrite: false);
    }

    public void Update(ImportProfile profile)
    {
        ValidateForWrite(profile);
        EnsureDirectorySafe();

        var targetPath = ResolveProfilePath(profile.Id);
        if (!File.Exists(targetPath))
        {
            throw new ImportProfileStoreException(
                ImportProfileStoreError.NotFound,
                $"Profile '{profile.Id:D}' was not found.");
        }

        if (IsReparsePoint(targetPath))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, "The target profile file is a symbolic link or reparse point.");
        }

        // Refuse to silently overwrite a malformed, unsupported-schema, or ID-mismatched file.
        byte[] existingBytes;
        try
        {
            existingBytes = File.ReadAllBytes(targetPath);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, $"Could not read existing profile '{profile.Id:D}' before update.", ex);
        }

        var existingProfile = ImportProfileJsonSerializer.Deserialize(existingBytes);
        if (existingProfile.Id != profile.Id)
        {
            throw new ImportProfileStoreException(
                ImportProfileStoreError.DuplicateIdentity,
                $"File '{Path.GetFileName(targetPath)}' contains mismatched profile Id '{existingProfile.Id:D}' and will not be overwritten.");
        }

        WriteAtomic(targetPath, ImportProfileJsonSerializer.Serialize(profile), overwrite: true);
    }

    public void Delete(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.InvalidProfile, "Profile Id must not be empty.");
        }

        var targetPath = ResolveProfilePath(id);
        if (!File.Exists(targetPath))
        {
            throw new ImportProfileStoreException(
                ImportProfileStoreError.NotFound,
                $"Profile '{id:D}' was not found.");
        }

        if (IsReparsePoint(targetPath))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, "The target profile file is a symbolic link or reparse point.");
        }

        try
        {
            File.Delete(targetPath);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, $"Could not delete profile '{id:D}'.", ex);
        }
    }

    private void ValidateForWrite(ImportProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.InvalidProfile, validation.Errors[0].Message);
        }
    }

    private void EnsureDirectorySafe()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, "Could not create profile storage directory.", ex);
        }

        if (IsReparsePoint(DirectoryPath))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, "Profile storage directory must not be a symbolic link or reparse point.");
        }
    }

    private string ResolveProfilePath(Guid id)
    {
        var fileName = CanonicalFileName(id);
        var combined = Path.GetFullPath(Path.Combine(DirectoryPath, fileName));
        var normalizedRoot = DirectoryPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, "Resolved profile path escapes the storage root.");
        }

        return combined;
    }

    private static string CanonicalFileName(Guid id) => $"{id:D}.json";

    private void WriteAtomic(string targetPath, byte[] bytes, bool overwrite)
    {
        var tempPath = Path.Combine(DirectoryPath, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.tmp");
        string? backupPath = null;
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var split = bytes.Length / 2;
                stream.Write(bytes, 0, split);
                WritePhaseHook?.Invoke(ImportProfileWritePhase.WritingTemporaryFile);
                stream.Write(bytes, split, bytes.Length - split);
                stream.Flush(flushToDisk: true);
            }

            WritePhaseHook?.Invoke(ImportProfileWritePhase.BeforeReplace);

            if (overwrite)
            {
                backupPath = Path.Combine(DirectoryPath, $".{Path.GetFileName(targetPath)}.{Guid.NewGuid():N}.bak");
                File.Replace(tempPath, targetPath, backupPath, ignoreMetadataErrors: true);
                TryDelete(backupPath);
            }
            else
            {
                File.Move(tempPath, targetPath, overwrite: false);
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            TryDelete(tempPath);
            if (backupPath is not null && !File.Exists(targetPath) && File.Exists(backupPath))
            {
                try
                {
                    File.Move(backupPath, targetPath, overwrite: false);
                }
                catch (Exception restoreEx) when (IsFileSystemFailure(restoreEx))
                {
                }
            }
            else if (backupPath is not null)
            {
                TryDelete(backupPath);
            }

            throw new ImportProfileStoreException(ImportProfileStoreError.StorageFailure, "Could not write the profile file.", ex);
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return Path.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return true;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
        }
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;
}

internal enum ImportProfileWritePhase
{
    WritingTemporaryFile,
    BeforeReplace
}
