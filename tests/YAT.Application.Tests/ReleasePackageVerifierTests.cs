using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Application.Tests;

// The release package rules written once (Task #051.C) - for the release script and the updater alike - over packages
// made in memory; the installation's layout; and installing through the update service.
public class ReleasePackageVerifierTests
{
    private static readonly ReleaseVersion Version = ReleaseVersion.Parse("0.3.0");

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static readonly Dictionary<string, byte[]> Files = new()
    {
        ["YAT.exe"] = Bytes("exe 0.3.0"),
        ["licenses/A/LICENSE.txt"] = Bytes("MIT")
    };

    // A package as build/publish.ps1 makes it; `listed` replaces what the inventory says of the files.
    private static MemoryStream Package(Dictionary<string, byte[]>? files = null, Dictionary<string, byte[]>? listed = null, Action<ZipArchive>? edit = null)
    {
        files ??= Files;
        var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var inventory = new ReleaseInventory(Version, "win-x64", (listed ?? files).Select(file => new ReleaseFile(file.Key, file.Value.Length, Sha(file.Value))));
            foreach (var (name, data) in files.Select(file => ("YAT-v0.3.0-win-x64/" + file.Key, file.Value)).Append(("YAT-v0.3.0-win-x64/yat-files.json", inventory.ToJson())))
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }

            edit?.Invoke(archive);
        }

        buffer.Position = 0;
        return buffer;
    }

    private static IReadOnlyList<string> Verify(MemoryStream package)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read);
        return ReleasePackageVerifier.Verify(archive, Version, "win-x64");
    }

    [Fact]
    public void AReleasePackageHasNoProblemsAndListsItsFilesWithTheirEntries()
    {
        using var archive = new ZipArchive(Package(), ZipArchiveMode.Read);

        var contents = ReleasePackageVerifier.Read(archive, Version, "win-x64");

        Assert.True(contents.IsValid);
        Assert.Equal(["YAT.exe", "licenses/A/LICENSE.txt"], contents.Files.Select(file => file.File.Path));
        Assert.All(contents.Files, file => Assert.EndsWith(file.File.Path, file.Entry.FullName, StringComparison.Ordinal));
        Assert.Equal("YAT-v0.3.0-win-x64/yat-files.json", contents.InventoryEntry!.FullName);
    }

    [Fact]
    public void ReadingWithoutContentsDoesNotReadTheFilesButCopyingChecksThem()
    {
        var files = new Dictionary<string, byte[]>(Files) { ["YAT.exe"] = Bytes("exe 0.3.0, changed") };
        using var archive = new ZipArchive(Package(files, listed: Files), ZipArchiveMode.Read);

        var contents = ReleasePackageVerifier.Read(archive, Version, "win-x64");

        Assert.True(contents.IsValid, "the structure is right");
        Assert.False(ReleasePackageVerifier.TryCopy(contents.Files[0], Stream.Null));
        Assert.True(ReleasePackageVerifier.TryCopy(contents.Files[1], Stream.Null));
        Assert.Equal([ReleasePackageVerifier.NotTheListedFile("YAT.exe")], ReleasePackageVerifier.Read(archive, Version, "win-x64", verifyContents: true).Problems);
    }

    [Fact]
    public void AFileLongerThanListedIsNeverCopiedPastItsListedSize()
    {
        var files = new Dictionary<string, byte[]>(Files) { ["YAT.exe"] = new byte[1_000_000] };
        var listed = new Dictionary<string, byte[]>(Files) { ["YAT.exe"] = new byte[10] };
        using var archive = new ZipArchive(Package(files, listed), ZipArchiveMode.Read);
        var file = ReleasePackageVerifier.Read(archive, Version, "win-x64").Files[0];
        using var copied = new MemoryStream();

        Assert.False(ReleasePackageVerifier.TryCopy(file, copied));
        Assert.True(copied.Length <= 81920, "the copy stops at the first chunk past the listed size");
    }

    [Fact]
    public void ACopyIsTheFileItself()
    {
        using var archive = new ZipArchive(Package(), ZipArchiveMode.Read);
        using var copied = new MemoryStream();

        Assert.True(ReleasePackageVerifier.TryCopy(ReleasePackageVerifier.Read(archive, Version, "win-x64").Files[0], copied));
        Assert.Equal(Files["YAT.exe"], copied.ToArray());
    }

    [Theory]
    [InlineData("other/evil.dll", "not inside")]
    [InlineData("YAT-v0.3.0-win-x64/../evil.dll", "not a safe release path")]
    [InlineData("YAT-v0.3.0-win-x64/a/../../evil.dll", "not a safe release path")]
    [InlineData("YAT-v0.3.0-win-x64/yat.EXE", "twice")]
    [InlineData("YAT-v0.3.0-win-x64/extra.dll", "extra.dll, which its inventory does not list")]
    [InlineData("YAT-v0.3.0-win-x64/graph-palettes.json", "user settings")]
    public void EveryRuleIsTheReleaseScriptsRule(string entry, string problem)
    {
        var problems = Verify(Package(edit: archive => archive.CreateEntry(entry)));

        Assert.Contains(problems, text => text.Contains(problem, StringComparison.Ordinal));
    }

    [Fact]
    public void APackageOfAnotherVersionIsNotThisRelease()
    {
        using var archive = new ZipArchive(Package(), ZipArchiveMode.Read);

        Assert.Contains("The package entry 'YAT-v0.3.0-win-x64/YAT.exe' is not inside YAT-v0.4.0-win-x64/.", ReleasePackageVerifier.Verify(archive, ReleaseVersion.Parse("0.4.0"), "win-x64"));
    }

    [Fact]
    public void UserSettingsAreKnownByName()
    {
        Assert.True(ReleasePackageVerifier.IsUserSettingsFile("graph-palettes.json"));
        Assert.True(ReleasePackageVerifier.IsUserSettingsFile("graph-palettes.json.1a2b.tmp"));
        Assert.False(ReleasePackageVerifier.IsUserSettingsFile("YAT.deps.json"));
    }

    // ---- The installation ----

    [Fact]
    public void TheUpdatersFolderIsReservedAndEverythingItUsesIsInIt()
    {
        var installation = Path.Combine(Path.GetTempPath(), "YAT folder");

        Assert.True(ReleaseInstallation.IsReserved(".yat-update"));
        Assert.True(ReleaseInstallation.IsReserved(".YAT-UPDATE/journal.json"));
        Assert.False(ReleaseInstallation.IsReserved(".yat-updates/x.dll"));
        Assert.False(ReleaseInstallation.IsReserved("YAT.exe"));
        foreach (var path in new[]
                 {
                     ReleaseInstallation.UpdaterPath(installation), ReleaseInstallation.StagingFolder(installation, Version), ReleaseInstallation.BackupFolder(installation),
                     ReleaseInstallation.JournalPath(installation), ReleaseInstallation.LockPath(installation), ReleaseInstallation.SuccessMarkerPath(installation),
                     ReleaseInstallation.LogPath(installation)
                 })
        {
            Assert.StartsWith(Path.Combine(installation, ".yat-update") + Path.DirectorySeparatorChar, path, StringComparison.Ordinal);
        }

        Assert.Equal(Path.Combine(installation, ".yat-update", "staging", "v0.3.0"), ReleaseInstallation.StagingFolder(installation, Version));
        Assert.Equal(Path.Combine(installation, "licenses", "A", "LICENSE.txt"), ReleaseInstallation.PathOf(installation, "licenses/A/LICENSE.txt"));
    }

    [Fact]
    public void ThePackageIsWhereTheDownloaderKeptIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "updates");

        Assert.Equal(Path.Combine(root, "v0.3.0", "YAT-v0.3.0-win-x64.zip"), UpdatePackageLayout.PackagePath(root, Version, "win-x64"));
        Assert.Equal(Path.Combine(root, "v0.3.0", "yat-update.json"), UpdatePackageLayout.SnapshotPath(root, Version));
        Assert.Throws<ArgumentException>(() => UpdatePackageLayout.PackagePath(root, Version, @"..\x"));
    }

    // ---- Installing through the service ----

    private sealed class Installer(bool available) : IUpdateInstaller
    {
        public bool IsAvailable => available;

        public List<VerifiedUpdatePackage> Asked { get; } = [];

        public Task<UpdateInstallPreparation> PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
        {
            Asked.Add(package);
            return Task.FromResult(UpdateInstallPreparation.Failed(UpdateInstallFailure.UpdaterNotReady));
        }
    }

    private sealed class Nothing : IUpdateManifestSource, IUpdatePackageDownloader
    {
        public Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult("{}");

        public Task<VerifiedUpdatePackage?> FindVerifiedAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<VerifiedUpdatePackage?>(null);

        public Task<VerifiedUpdatePackage> DownloadAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task TheServiceInstallsOnlyThroughAnAvailableInstaller()
    {
        var package = new VerifiedUpdatePackage(Version, "win-x64", @"C:\updates\v0.3.0\YAT-v0.3.0-win-x64.zip", 1, new string('a', 64), null);
        var environment = new UpdateEnvironment("0.2.0", true, Architecture.X64);
        var installer = new Installer(available: true);

        Assert.False(new UpdateCheckService(new Nothing(), new Nothing(), environment).CanInstall);
        Assert.False(new UpdateCheckService(new Nothing(), new Nothing(), environment, new Installer(available: false)).CanInstall);
        var service = new UpdateCheckService(new Nothing(), new Nothing(), environment, installer);
        Assert.True(service.CanInstall);

        var result = await service.PrepareInstallAsync(package, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateInstallFailure.UpdaterNotReady, result.Failure);
        Assert.Same(package, Assert.Single(installer.Asked));
        Assert.Equal(
            UpdateInstallFailure.NotAReleaseInstallation,
            (await new UpdateCheckService(new Nothing(), new Nothing(), environment).PrepareInstallAsync(package, TestContext.Current.CancellationToken)).Failure);
    }
}
