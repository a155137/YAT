using YAT.Application.Distribution;

namespace YAT.Application.Updates;

// Where a downloaded release package is kept (Task #051.B), named only from the validated version and a checked runtime
// id - written once, for the downloader that keeps it there and the updater that installs it from there (#051.C):
//
//     <updates>\v0.2.0\YAT-v0.2.0-win-x64.zip      the package, once its size and SHA-256 matched the manifest
//     <updates>\v0.2.0\yat-update.json             the manifest it was verified against
public static class UpdatePackageLayout
{
    public static string Folder(string root, ReleaseVersion version) => Path.Combine(root, version.Tag);

    public static string PackagePath(string root, ReleaseVersion version, string rid) =>
        Path.Combine(Folder(root, version), version.PackageName(CheckedRid(rid)) + ".zip");

    public static string SnapshotPath(string root, ReleaseVersion version) => Path.Combine(Folder(root, version), ReleaseManifest.FileName);

    // A runtime id as YAT writes them ("win-x64"): nothing that could make a name a path.
    public static string CheckedRid(string rid)
    {
        ArgumentNullException.ThrowIfNull(rid);
        return rid.Length is > 0 and <= 32 && rid.All(character => char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character == '-')
            ? rid
            : throw new ArgumentException($"'{rid}' is not a runtime id.", nameof(rid));
    }
}
