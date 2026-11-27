namespace YAT.Application.Distribution;

// A release once it is unpacked and running: a folder (the installation) holding the release's files - those its
// yat-files.json lists, which are the release's own - and whatever else the user put there, which is theirs (Task
// #051.C). Installing an update works in one folder of its own inside the installation, which no release may own:
//
//     <installation>\YAT.exe, ..., yat-files.json          the release
//     <installation>\.yat-update\updater\YAT.Updater.exe   the updater, taken from the new, verified package
//     <installation>\.yat-update\staging\v<version>\       the new release, extracted and verified
//     <installation>\.yat-update\backup\                   the old release's files it replaces, while it installs
//     <installation>\.yat-update\journal.json              what an installation moves, to undo it
//     <installation>\.yat-update\install.lock              held while an updater runs
//     <installation>\.yat-update\updated.json              the version just installed, until YAT has said so once
//     <installation>\.yat-update\update.log                what the updater did
public static class ReleaseInstallation
{
    public const string ExecutableName = "YAT.exe";

    public const string UpdaterName = "YAT.Updater.exe";

    public const string WorkFolderName = ".yat-update";

    // Portable user-owned data directory (FT-00C): never packaged, owned, overwritten, moved, or deleted by releases/updater.
    public const string UserDataFolderName = "UserData";

    // The one runtime releases are made for.
    public const string Rid = "win-x64";

    public static string WorkFolder(string installation) => Path.Combine(installation, WorkFolderName);

    public static string UserDataFolder(string installation) => Path.Combine(installation, UserDataFolderName);

    public static string Executable(string installation) => Path.Combine(installation, ExecutableName);

    public static string InventoryPath(string installation) => Path.Combine(installation, ReleaseInventory.FileName);

    public static string UpdaterFolder(string installation) => Path.Combine(WorkFolder(installation), "updater");

    public static string UpdaterPath(string installation) => Path.Combine(UpdaterFolder(installation), UpdaterName);

    public static string StagingFolder(string installation, ReleaseVersion version) => Path.Combine(WorkFolder(installation), "staging", version.Tag);

    public static string BackupFolder(string installation) => Path.Combine(WorkFolder(installation), "backup");

    public static string JournalPath(string installation) => Path.Combine(WorkFolder(installation), "journal.json");

    public static string LockPath(string installation) => Path.Combine(WorkFolder(installation), "install.lock");

    public static string SuccessMarkerPath(string installation) => Path.Combine(WorkFolder(installation), "updated.json");

    public static string LogPath(string installation) => Path.Combine(WorkFolder(installation), "update.log");

    // A release path inside the installation's own work folder (.yat-update/), which no release may own.
    public static bool IsReserved(string releasePath) => IsTopLevelFolder(releasePath, WorkFolderName);

    // A release path targeting the portable user-owned UserData/ directory or any descendant, which no release or updater may touch.
    public static bool IsUserData(string releasePath) => IsTopLevelFolder(releasePath, UserDataFolderName);

    // Any path inside a release installation that releases and updater mutations must never own, modify, move, or delete.
    public static bool IsProtected(string releasePath) => IsReserved(releasePath) || IsUserData(releasePath);

    private static bool IsTopLevelFolder(string releasePath, string folderName)
    {
        ArgumentNullException.ThrowIfNull(releasePath);
        var unified = releasePath.Replace('\\', '/');
        var trimmed = unified.TrimEnd('/');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var slash = trimmed.IndexOf('/');
        var firstSegment = (slash < 0 ? trimmed : trimmed[..slash]).TrimEnd('.', ' ');
        return string.Equals(firstSegment, folderName, StringComparison.OrdinalIgnoreCase);
    }

    // The installation's path of a release path.
    public static string PathOf(string installation, string releasePath) =>
        Path.Combine(installation, releasePath.Replace('/', Path.DirectorySeparatorChar));
}
