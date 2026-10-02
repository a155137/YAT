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

    // The one runtime releases are made for.
    public const string Rid = "win-x64";

    public static string WorkFolder(string installation) => Path.Combine(installation, WorkFolderName);

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

    // A release path inside the installation's own work folder, which no release may own.
    public static bool IsReserved(string releasePath)
    {
        ArgumentNullException.ThrowIfNull(releasePath);
        return string.Equals(releasePath, WorkFolderName, StringComparison.OrdinalIgnoreCase)
            || releasePath.StartsWith(WorkFolderName + "/", StringComparison.OrdinalIgnoreCase);
    }

    // The installation's path of a release path.
    public static string PathOf(string installation, string releasePath) =>
        Path.Combine(installation, releasePath.Replace('/', Path.DirectorySeparatorChar));
}
