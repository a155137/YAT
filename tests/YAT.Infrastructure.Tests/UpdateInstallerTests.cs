using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using YAT.Infrastructure.Updates;

namespace YAT.Infrastructure.Tests;

// Preparing an installation while YAT is still open (Task #051.C), against a fake installation and package in a folder
// of the test's own: everything that can be known to fail fails here, leaving the installation untouched - and the
// updater, taken from the verified package and checked as it is written, is started and must say "ready".
public sealed class UpdateInstallerTests : IDisposable
{
    private static readonly ReleaseVersion Installed = ReleaseVersion.Parse("0.2.0");
    private static readonly ReleaseVersion Next = ReleaseVersion.Parse("0.3.0");
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yat-tests", Guid.NewGuid().ToString("N"));

    public UpdateInstallerTests()
    {
        Directory.CreateDirectory(Installation);
        File.WriteAllText(ReleaseInstallation.Executable(Installation), "exe 0.2.0");
        File.WriteAllBytes(ReleaseInstallation.InventoryPath(Installation), new ReleaseInventory(Installed, "win-x64", [new ReleaseFile("YAT.exe", 9, Sha(Encoding.UTF8.GetBytes("exe 0.2.0")))]).ToJson());
    }

    private string Installation => Path.Combine(_root, "YAT folder");

    private string Updates => Path.Combine(_root, "updates");

    private string UpdaterPath => ReleaseInstallation.UpdaterPath(Installation);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    // The package YAT downloaded: YAT.exe and the given updater (none: no updater), its inventory, the ZIP and manifest.
    private VerifiedUpdatePackage Package(byte[]? updater, ReleaseVersion? version = null, Action<string>? tamper = null)
    {
        var release = version ?? Next;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal) { ["YAT.exe"] = Encoding.UTF8.GetBytes($"exe {release}") };
        if (updater is not null)
        {
            files[ReleaseInstallation.UpdaterName] = updater;
        }

        var zip = UpdatePackageLayout.PackagePath(Updates, release, "win-x64");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        var top = release.PackageName("win-x64") + "/";
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var inventory = new ReleaseInventory(release, "win-x64", files.Select(file => new ReleaseFile(file.Key, file.Value.LongLength, Sha(file.Value))));
            foreach (var (name, data) in files.Select(file => (top + file.Key, file.Value)).Append((top + ReleaseInventory.FileName, inventory.ToJson())).OrderBy(entry => entry.Item1, StringComparer.Ordinal))
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        var bytes = File.ReadAllBytes(zip);
        File.WriteAllBytes(
            UpdatePackageLayout.SnapshotPath(Updates, release),
            new ReleaseManifest(release, [new ReleasePackage("win-x64", new Uri("https://example.test/p.zip"), Sha(bytes), bytes.LongLength)]).ToJson());
        tamper?.Invoke(zip);
        return new VerifiedUpdatePackage(release, "win-x64", zip, bytes.LongLength, Sha(bytes), null);
    }

    private UpdateInstaller Installer(UpdateInstallTarget? target = null)
    {
        using var process = Process.GetCurrentProcess();
        return new UpdateInstaller(
            target ?? new UpdateInstallTarget(Installation, ReleaseInstallation.Executable(Installation), process.Id, process.StartTime.ToUniversalTime().Ticks, "0.2.0"),
            Updates,
            TimeSpan.FromSeconds(10));
    }

    private UpdateInstallPreparation Prepare(VerifiedUpdatePackage package, UpdateInstallTarget? target = null) =>
        Installer(target).PrepareAsync(package, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private void AssertFailed(UpdateInstallPreparation result, UpdateInstallFailure failure)
    {
        Assert.False(result.Succeeded);
        Assert.Equal(failure, result.Failure);
        Assert.Equal(["YAT.exe", "yat-files.json"], Directory.EnumerateFiles(Installation).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheUpdaterIsTakenFromTheVerifiedPackageAndStarted()
    {
        // A real program that is not an updater: it starts, but never says "ready".
        var updater = File.ReadAllBytes(Ping);

        var result = Prepare(Package(updater));

        Assert.Equal(UpdateInstallFailure.UpdaterNotReady, result.Failure);
        Assert.Contains("stopped before it was ready", result.Detail, StringComparison.Ordinal);
        Assert.Equal(updater, File.ReadAllBytes(UpdaterPath));
        Assert.False(File.Exists(UpdaterPath + ".partial"));
    }

    [Fact]
    public void AnUpdaterThatIsNotAProgramCannotStart()
    {
        AssertFailedButPlaced(Prepare(Package(Encoding.UTF8.GetBytes("not a program"))));
    }

    private void AssertFailedButPlaced(UpdateInstallPreparation result)
    {
        Assert.Equal(UpdateInstallFailure.UpdaterNotReady, result.Failure);
        Assert.True(File.Exists(UpdaterPath));
    }

    [Fact]
    public void OnlyAnUnpackedReleaseCanBeUpdated()
    {
        Assert.True(Installer().IsAvailable);

        File.Delete(ReleaseInstallation.InventoryPath(Installation));
        Assert.False(Installer().IsAvailable);
        Assert.Equal(UpdateInstallFailure.NotAReleaseInstallation, Prepare(Package(File.ReadAllBytes(Ping))).Failure);
        Assert.False(Directory.Exists(ReleaseInstallation.WorkFolder(Installation)));
    }

    [Fact]
    public void AnInstallationOfAnotherVersionOrExecutableCannotBeUpdated()
    {
        using var process = Process.GetCurrentProcess();
        var target = new UpdateInstallTarget(Installation, ReleaseInstallation.Executable(Installation), process.Id, 1, "0.2.1");
        Assert.False(Installer(target).IsAvailable);
        Assert.False(Installer(target with { InstalledVersion = "0.2.0", Executable = Path.Combine(Installation, "Other.exe") }).IsAvailable);
        Assert.Equal(UpdateInstallFailure.NotAReleaseInstallation, Prepare(Package(File.ReadAllBytes(Ping)), target).Failure);
    }

    [Fact]
    public void AnOlderPackageIsNotInstalled()
    {
        AssertFailed(Prepare(Package(File.ReadAllBytes(Ping), ReleaseVersion.Parse("0.1.9"))), UpdateInstallFailure.PackageInvalid);
    }

    [Fact]
    public void APackageChangedSinceItWasVerifiedIsRefusedAndNoUpdaterIsWritten()
    {
        var package = Package(File.ReadAllBytes(Ping), tamper: zip =>
        {
            var bytes = File.ReadAllBytes(zip);
            bytes[200] ^= 0xFF;
            File.WriteAllBytes(zip, bytes);
        });

        AssertFailed(Prepare(package), UpdateInstallFailure.PackageInvalid);
        Assert.False(File.Exists(UpdaterPath));
    }

    [Fact]
    public void APackageWithoutAnUpdaterCannotBeInstalled()
    {
        var result = Prepare(Package(updater: null));

        AssertFailed(result, UpdateInstallFailure.PackageInvalid);
        Assert.Contains("no YAT.Updater.exe", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void APackageWithoutItsManifestCannotBeInstalled()
    {
        var package = Package(File.ReadAllBytes(Ping));
        File.Delete(UpdatePackageLayout.SnapshotPath(Updates, Next));

        AssertFailed(Prepare(package), UpdateInstallFailure.PackageInvalid);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenIsSaidBeforeYatCloses()
    {
        File.WriteAllText(ReleaseInstallation.WorkFolder(Installation), "a file where the update folder must be");

        var result = Prepare(Package(File.ReadAllBytes(Ping)));

        Assert.Equal(UpdateInstallFailure.InstallationNotWritable, result.Failure);
    }

    [Fact]
    public void AnUpdateAlreadyInProgressIsNotStartedAgain()
    {
        Directory.CreateDirectory(ReleaseInstallation.WorkFolder(Installation));
        using var held = new FileStream(ReleaseInstallation.LockPath(Installation), FileMode.Create, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(UpdateInstallFailure.UpdateInProgress, Prepare(Package(File.ReadAllBytes(Ping))).Failure);
        Assert.False(File.Exists(UpdaterPath));
    }

    [Fact]
    public void TheUpdatedNoticeIsTakenOnceAndTheUpdaterWithIt()
    {
        Directory.CreateDirectory(ReleaseInstallation.UpdaterFolder(Installation));
        File.WriteAllText(UpdaterPath, "the updater that installed 0.3.0");
        File.WriteAllText(ReleaseInstallation.SuccessMarkerPath(Installation), """{"schemaVersion":1,"version":"0.3.0"}""");

        Assert.Null(UpdateInstallNotice.Take(Installation, "0.2.0"));
        Assert.False(File.Exists(ReleaseInstallation.SuccessMarkerPath(Installation)), "a notice for another version is dropped");

        File.WriteAllText(ReleaseInstallation.SuccessMarkerPath(Installation), """{"schemaVersion":1,"version":"0.3.0"}""");
        Assert.Equal("0.3.0", UpdateInstallNotice.Take(Installation, "0.3.0"));
        Assert.Null(UpdateInstallNotice.Take(Installation, "0.3.0"));
        Assert.False(Directory.Exists(ReleaseInstallation.UpdaterFolder(Installation)));
    }
}
