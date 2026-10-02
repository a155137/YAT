using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

namespace YAT.Updater.Tests;

// A folder of the test's own under the system temp folder: a fake YAT installation and an updates folder. Never the
// real YAT, never the user's %LOCALAPPDATA%. Deleted at the end (deny rules a test set are removed first).
internal sealed class Sandbox : IDisposable
{
    public Sandbox()
    {
        Root = Path.Combine(Path.GetTempPath(), "yat-updater-tests", Guid.NewGuid().ToString("N"));
        Installation = Path.Combine(Root, "YAT folder");
        Updates = Path.Combine(Root, "updates");
        Directory.CreateDirectory(Installation);
        Directory.CreateDirectory(Updates);
        Log = new UpdaterLog(null, Lines.Add);
    }

    public string Root { get; }

    public string Installation { get; }

    public string Updates { get; }

    public List<string> Lines { get; } = [];

    public UpdaterLog Log { get; }

    public List<string> Denied { get; } = [];

    public string InInstallation(string releasePath) => ReleaseInstallation.PathOf(Installation, releasePath);

    // Denies the current user deleting - and so renaming - a file: Delete on the file, and deleting children on its folder
    // (which would otherwise allow it).
    public void DenyDelete(string path)
    {
        Rule(new FileInfo(path), FileSystemRights.Delete, add: true);
        Rule(new FileInfo(path).Directory!, FileSystemRights.DeleteSubdirectoriesAndFiles, add: true);
        Denied.Add(path);
    }

    public void Allow(string path)
    {
        if (File.Exists(path))
        {
            Rule(new FileInfo(path), FileSystemRights.Delete, add: false);
        }

        if (new FileInfo(path).Directory is { Exists: true } folder)
        {
            Rule(folder, FileSystemRights.DeleteSubdirectoriesAndFiles, add: false);
        }
    }

    private static void Rule(FileSystemInfo item, FileSystemRights rights, bool add)
    {
        var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
        if (item is FileInfo file)
        {
            var security = file.GetAccessControl();
            _ = add ? AddTo(security, rule) : security.RemoveAccessRule(rule);
            file.SetAccessControl(security);
        }
        else
        {
            var folder = (DirectoryInfo)item;
            var security = folder.GetAccessControl();
            _ = add ? AddTo(security, rule) : security.RemoveAccessRule(rule);
            folder.SetAccessControl(security);
        }
    }

    private static bool AddTo(FileSystemSecurity security, FileSystemAccessRule rule)
    {
        security.AddAccessRule(rule);
        return true;
    }

    public void Dispose()
    {
        foreach (var path in Denied)
        {
            try
            {
                Allow(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }

        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// Fake YAT releases: files whose content tells their version, installed into a folder or packed as the package YAT
// downloads (the ZIP and its manifest snapshot, laid out as UpdatePackageLayout says).
internal static class Releases
{
    public const string Rid = "win-x64";

    public static readonly ReleaseVersion V2 = ReleaseVersion.Parse("0.2.0");

    public static readonly ReleaseVersion V3 = ReleaseVersion.Parse("0.3.0");

    // YAT.exe and YAT.dll change; the runtime and a license do not; 0.2.0 has two files 0.3.0 removes, 0.3.0 two new
    // ones (one in a new folder).
    public static SortedDictionary<string, byte[]> Files(ReleaseVersion version)
    {
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["YAT.exe"] = Bytes($"exe {version}"),
            ["YAT.dll"] = Bytes($"YAT library {version}"),
            ["YAT.Updater.exe"] = Bytes($"updater {version}"),
            ["coreclr.dll"] = Bytes("the .NET runtime, the same in both"),
            ["licenses/SkiaSharp/LICENSE.txt"] = Bytes("MIT, the same in both"),
            ["README.txt"] = Bytes($"readme {version}")
        };

        if (version == V2)
        {
            files["old-only.dll"] = Bytes("removed in 0.3.0");
            files["plugins/legacy/old.dll"] = Bytes("a folder removed in 0.3.0");
        }
        else
        {
            files["new-only.dll"] = Bytes("added in 0.3.0");
            files["lang/zh-Hant/YAT.resources.dll"] = Bytes($"資源 {version}");
        }

        return files;
    }

    public static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    public static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static ReleaseInventory Inventory(ReleaseVersion version, IDictionary<string, byte[]> files) =>
        new(version, Rid, files.Select(file => new ReleaseFile(file.Key, file.Value.LongLength, Sha(file.Value))));

    // The release's files and yat-files.json, written into a folder (an unpacked release).
    public static void Install(string installation, ReleaseVersion version, IDictionary<string, byte[]>? files = null)
    {
        files ??= Files(version);
        foreach (var file in files)
        {
            var path = ReleaseInstallation.PathOf(installation, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }

        File.WriteAllBytes(ReleaseInstallation.InventoryPath(installation), Inventory(version, files).ToJson());
    }

    // The package YAT downloaded and verified: YAT-v<version>-win-x64.zip, made as build/publish.ps1 makes it (one top
    // folder, '/' entries in ordinal order, yat-files.json among them), and yat-update.json beside it naming its size and
    // SHA-256. `edit` changes the archive before it is closed; `inventory` replaces yat-files.json; `manifest` the
    // manifest written.
    public static string Package(
        string updates,
        ReleaseVersion version,
        IDictionary<string, byte[]>? files = null,
        Action<ZipArchive, string>? edit = null,
        byte[]? inventory = null,
        Func<ReleaseManifest, ReleaseManifest>? manifest = null)
    {
        files ??= Files(version);
        var top = version.PackageName(Rid) + "/";
        var zip = UpdatePackageLayout.PackagePath(updates, version, Rid);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entries = files.Select(file => (Name: top + file.Key, Data: file.Value))
                .Append((Name: top + ReleaseInventory.FileName, Data: inventory ?? Inventory(version, files).ToJson()))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal);
            foreach (var (name, data) in entries)
            {
                Add(archive, name, data);
            }

            edit?.Invoke(archive, top);
        }

        var bytes = File.ReadAllBytes(zip);
        var written = new ReleaseManifest(version, [new ReleasePackage(Rid, new Uri($"https://example.test/{version.Tag}/{Path.GetFileName(zip)}"), Sha(bytes), bytes.LongLength)]);
        File.WriteAllBytes(UpdatePackageLayout.SnapshotPath(updates, version), (manifest?.Invoke(written) ?? written).ToJson());
        return zip;
    }

    public static void Add(ZipArchive archive, string name, byte[] data)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(data);
    }

    // What an installation is, to compare before and after: every file but the updater's work folder, with its SHA-256.
    public static SortedDictionary<string, string> Snapshot(string folder)
    {
        var snapshot = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            if (!ReleaseInstallation.IsReserved(relative))
            {
                snapshot[relative] = Sha(File.ReadAllBytes(path));
            }
        }

        return snapshot;
    }

    // The folders of an installation (but the work folder).
    public static SortedSet<string> Folders(string folder) =>
        new(Directory.EnumerateDirectories(folder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(folder, path).Replace('\\', '/'))
            .Where(relative => !ReleaseInstallation.IsReserved(relative)), StringComparer.Ordinal);

    // The release's files as an installation of it must hold them.
    public static SortedDictionary<string, string> Expected(ReleaseVersion version, IDictionary<string, byte[]>? files = null, IDictionary<string, byte[]>? user = null)
    {
        files ??= Files(version);
        var expected = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in files.Concat(user ?? new Dictionary<string, byte[]>()))
        {
            expected[file.Key] = Sha(file.Value);
        }

        expected[ReleaseInventory.FileName] = Sha(Inventory(version, files).ToJson());
        return expected;
    }
}

// The updater's Windows, recorded: what it told the user, what it started; file versions read from the fake YAT.exe
// ("exe 0.3.0" is version 0.3.0.0).
internal sealed class FakeHost : IUpdaterHost
{
    public List<(string Message, bool Error)> Notices { get; } = [];

    public List<string> Started { get; } = [];

    public bool CanStart { get; set; } = true;

    public List<int> Running { get; } = [];

    public Func<string, string?>? Version { get; set; }

    public void Notify(string message, bool error) => Notices.Add((message, error));

    public bool Start(string executable, string workingDirectory)
    {
        Started.Add($"{File.ReadAllText(executable)} in {workingDirectory}");
        return CanStart;
    }

    public string? FileVersionOf(string executable)
    {
        if (Version is not null)
        {
            return Version(executable);
        }

        return File.Exists(executable) && File.ReadAllText(executable) is { } text && text.StartsWith("exe ", StringComparison.Ordinal)
            && ReleaseVersion.TryParse(text[4..], out var version)
            ? version.FileVersion
            : null;
    }

    public IReadOnlyList<int> OthersRunning(string executable) => Running;
}
