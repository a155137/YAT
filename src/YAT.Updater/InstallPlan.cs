using System.Security.Cryptography;
using YAT.Application.Distribution;

namespace YAT.Updater;

internal enum MoveKind
{
    // A file of the old release, out of the installation into .yat-update\backup.
    Backup,

    // A file of the new release, out of the staging folder into the installation.
    Place
}

// One rename of an installation (Task #051.C). Size and Sha256 are the file's as its inventory lists it - the old
// release's for a backup, the new release's for a placement - so that recovery can tell a placed file from any other.
internal sealed record PlannedMove(MoveKind Kind, string Path, long Size, string Sha256);

// Every rename that turns the installed release into the new one (Task #051.C), worked out before anything is moved -
// and refused, changing nothing, when the installation is not what the plan may touch:
//
//     the release's own files are those its yat-files.json lists (and yat-files.json): only they are ever moved;
//     a file both releases list with the same SHA-256 stays where it is, once the file there is checked to be it;
//     a changed file is backed up and the new one placed; a file only the old release lists is backed up (removed);
//     a file only the new release lists is placed - unless something is already at its path that the old release does
//     not own: an ownership collision, which stops the update (the user's file is never moved);
//     a link (symbolic link, junction) anywhere on the way stops it, and so does a folder where a release file should be.
//
// Order: YAT.exe is backed up first and placed last, so a YAT.exe in the installation always means a whole release.
internal sealed class InstallPlan
{
    private InstallPlan(ReleaseVersion from, ReleaseVersion to, IReadOnlyList<PlannedMove> moves, IReadOnlyList<string> kept, IReadOnlyList<string> createdDirectories)
    {
        From = from;
        To = to;
        Moves = moves;
        Kept = kept;
        CreatedDirectories = createdDirectories;
    }

    public ReleaseVersion From { get; }

    public ReleaseVersion To { get; }

    public IReadOnlyList<PlannedMove> Moves { get; }

    // Release paths left untouched (the same file in both releases).
    public IReadOnlyList<string> Kept { get; }

    // Release-style paths of the folders placing the new release creates, deepest first.
    public IReadOnlyList<string> CreatedDirectories { get; }

    public static InstallPlan Create(InstalledRelease installed, StagedRelease staged, UpdaterLog log)
    {
        var installation = installed.Folder;
        var oldFiles = installed.Inventory.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var newPaths = staged.Inventory.Files.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var backups = new List<PlannedMove>();
        var places = new List<PlannedMove>();
        var kept = new List<string>();
        var collisions = new List<string>();
        var created = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in staged.Inventory.Files)
        {
            var target = StagedRelease.Inside(installation, file.Path);
            Ancestors(installation, file.Path, oldFiles, newPaths, created, collisions);
            if (oldFiles.TryGetValue(file.Path, out var old))
            {
                if (Directory.Exists(target))
                {
                    throw new UpdaterException($"There is a folder where YAT's file {file.Path} should be: {target}.");
                }

                if (File.Exists(target))
                {
                    if (new FileInfo(target).LinkTarget is not null)
                    {
                        throw new UpdaterException($"YAT's file {file.Path} is a link; YAT updates only real files.");
                    }

                    if (old.Sha256 == file.Sha256 && IsFile(target, file))
                    {
                        kept.Add(file.Path);
                        continue;
                    }

                    backups.Add(new PlannedMove(MoveKind.Backup, old.Path, old.Size, old.Sha256));
                }

                places.Add(new PlannedMove(MoveKind.Place, file.Path, file.Size, file.Sha256));
            }
            else if (File.Exists(target) || Directory.Exists(target))
            {
                collisions.Add(file.Path);
            }
            else
            {
                places.Add(new PlannedMove(MoveKind.Place, file.Path, file.Size, file.Sha256));
            }
        }

        // Files only the old release has: removed (backed up).
        foreach (var old in installed.Inventory.Files.Where(file => !newPaths.Contains(file.Path)))
        {
            var target = StagedRelease.Inside(installation, old.Path);
            Ancestors(installation, old.Path, oldFiles, newPaths, null, null);
            if (File.Exists(target))
            {
                if (new FileInfo(target).LinkTarget is not null)
                {
                    throw new UpdaterException($"YAT's file {old.Path} is a link; YAT updates only real files.");
                }

                backups.Add(new PlannedMove(MoveKind.Backup, old.Path, old.Size, old.Sha256));
            }
        }

        if (collisions.Count > 0)
        {
            throw new UpdaterException(
                "The new version needs these files, which are already in YAT's folder but are not YAT's own: "
                + string.Join(", ", collisions.Order(StringComparer.Ordinal).Take(10))
                + ". Move them out of YAT's folder, then update again.");
        }

        // The inventory itself: always replaced.
        var inventory = ReleaseInventory.FileName;
        var oldInventory = ReleaseInstallation.InventoryPath(installation);
        if (new FileInfo(oldInventory).LinkTarget is not null)
        {
            throw new UpdaterException($"{inventory} is a link; YAT updates only real files.");
        }

        backups.Add(new PlannedMove(MoveKind.Backup, inventory, new FileInfo(oldInventory).Length, Sha256(oldInventory)));
        places.Add(new PlannedMove(MoveKind.Place, inventory, staged.InventoryJson.LongLength, Convert.ToHexStringLower(SHA256.HashData(staged.InventoryJson))));

        var moves = backups.OrderBy(move => !IsExecutable(move.Path)).ThenBy(move => move.Path, StringComparer.Ordinal)
            .Concat(places.OrderBy(move => IsExecutable(move.Path)).ThenBy(move => move.Path == inventory).ThenBy(move => move.Path, StringComparer.Ordinal))
            .ToList();
        var directories = created.OrderByDescending(path => path.Count(character => character == '/')).ThenBy(path => path, StringComparer.Ordinal).ToList();
        log.Write($"Plan {installed.Inventory.Version} -> {staged.Version}: {kept.Count} kept, {backups.Count} backed up, {places.Count} placed, {directories.Count} folders created.");
        return new InstallPlan(installed.Inventory.Version, staged.Version, moves, kept, directories);
    }

    public static bool IsFile(string path, ReleaseFile file)
    {
        try
        {
            if (new FileInfo(path).Length != file.Size)
            {
                return false;
            }

            return Sha256(path) == file.Sha256;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static string Sha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool IsExecutable(string path) => string.Equals(path, ReleaseInstallation.ExecutableName, StringComparison.OrdinalIgnoreCase);

    // The folders on the way to a release path: never a link; a file in the way is a collision unless the old release
    // owns it (and so moves it out first); folders that are not there yet are created (and noted).
    private static void Ancestors(
        string installation,
        string releasePath,
        Dictionary<string, ReleaseFile> oldFiles,
        HashSet<string> newPaths,
        HashSet<string>? created,
        List<string>? collisions)
    {
        var parts = releasePath.Split('/');
        for (var depth = 1; depth < parts.Length; depth++)
        {
            var relative = string.Join('/', parts.Take(depth));
            var path = ReleaseInstallation.PathOf(installation, relative);
            if (Directory.Exists(path))
            {
                if (new DirectoryInfo(path).LinkTarget is not null)
                {
                    throw new UpdaterException($"The folder {relative} in YAT's folder is a link; YAT updates only real folders.");
                }
            }
            else if (File.Exists(path))
            {
                if (!oldFiles.ContainsKey(relative) || newPaths.Contains(relative))
                {
                    collisions?.Add(relative);
                }
                else
                {
                    created?.Add(relative);
                }
            }
            else
            {
                created?.Add(relative);
            }
        }
    }
}
