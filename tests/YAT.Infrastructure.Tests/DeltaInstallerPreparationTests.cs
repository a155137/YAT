using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using YAT.Infrastructure.Updates;

namespace YAT.Infrastructure.Tests;

public sealed class DeltaInstallerPreparationTests : IDisposable
{
    private static readonly ReleaseVersion Installed = ReleaseVersion.Parse("0.2.0");
    private static readonly ReleaseVersion Next = ReleaseVersion.Parse("0.3.0");
    private static readonly byte[] OldExe = Encoding.UTF8.GetBytes("exe 0.2.0");
    private static readonly byte[] NewExe = Encoding.UTF8.GetBytes("exe 0.3.0");
    private static readonly byte[] OldUpdater = Encoding.UTF8.GetBytes("updater 0.2.0");
    private static readonly byte[] NewUpdater = Encoding.UTF8.GetBytes("updater 0.3.0");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yat-delta-prep-tests", Guid.NewGuid().ToString("N"));

    public DeltaInstallerPreparationTests()
    {
        Directory.CreateDirectory(Installation);
        File.WriteAllBytes(ReleaseInstallation.Executable(Installation), OldExe);
        File.WriteAllBytes(ReleaseInstallation.PathOf(Installation, ReleaseInstallation.UpdaterName), OldUpdater);
        var baseInventory = new ReleaseInventory(
            Installed,
            "win-x64",
            [
                new ReleaseFile("YAT.exe", OldExe.LongLength, Sha(OldExe)),
                new ReleaseFile(ReleaseInstallation.UpdaterName, OldUpdater.LongLength, Sha(OldUpdater))
            ]);
        File.WriteAllBytes(ReleaseInstallation.InventoryPath(Installation), baseInventory.ToJson());
    }

    private string Installation => Path.Combine(_root, "YAT folder");

    private string Updates => Path.Combine(_root, "updates");

    private string StagedUpdaterPath => ReleaseInstallation.UpdaterPath(Installation);

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

    private VerifiedUpdatePackage CreateDeltaPackage(bool updaterChanged, Action<string>? tamperZip = null)
    {
        var baseInventoryBytes = File.ReadAllBytes(ReleaseInstallation.InventoryPath(Installation));
        var baseInventorySha256 = Sha(baseInventoryBytes);

        var targetUpdater = updaterChanged ? NewUpdater : OldUpdater;
        var targetFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["YAT.exe"] = NewExe,
            [ReleaseInstallation.UpdaterName] = targetUpdater
        };
        var targetInventory = new ReleaseInventory(
            Next,
            "win-x64",
            targetFiles.Select(kv => new ReleaseFile(kv.Key, kv.Value.LongLength, Sha(kv.Value))));

        List<string> changed = updaterChanged
            ? ["YAT.exe", ReleaseInstallation.UpdaterName]
            : ["YAT.exe"];
        var delta = new ReleaseDeltaManifest(Installed, Next, "win-x64", baseInventorySha256, changed, []);

        var zip = UpdatePackageLayout.DeltaPackagePath(Updates, Installed, Next, "win-x64");
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        var top = Next.PackageName("win-x64") + "/";
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entries = changed
                .Select(path => (Name: top + path, Data: targetFiles[path]))
                .Append((Name: top + ReleaseInventory.FileName, Data: targetInventory.ToJson()))
                .Append((Name: top + ReleaseDeltaManifest.FileName, Data: delta.ToJson()))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal);
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        var zipBytes = File.ReadAllBytes(zip);
        File.WriteAllBytes(
            UpdatePackageLayout.DeltaSnapshotPath(Updates, Installed, Next),
            new ReleaseManifest(
                Next,
                [new ReleasePackage("win-x64", new Uri("https://example.test/full.zip"), Sha(zipBytes), zipBytes.LongLength)],
                Deltas: [new ReleaseDeltaPackage("win-x64", Installed, new Uri("https://example.test/delta.zip"), Sha(zipBytes), zipBytes.LongLength)]).ToJson());

        tamperZip?.Invoke(zip);
        return new VerifiedUpdatePackage(Next, "win-x64", zip, zipBytes.LongLength, Sha(zipBytes), null, IsDelta: true, BaseVersion: Installed);
    }

    private UpdateInstaller CreateInstaller()
    {
        using var process = Process.GetCurrentProcess();
        return new UpdateInstaller(
            new UpdateInstallTarget(Installation, ReleaseInstallation.Executable(Installation), process.Id, process.StartTime.ToUniversalTime().Ticks, Installed.ToString()),
            Updates,
            TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task PrepareDelta_WhenUpdaterIsKept_CopiesAndVerifiesLocalUpdater()
    {
        var package = CreateDeltaPackage(updaterChanged: false);

        var result = await CreateInstaller().PrepareAsync(package, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateInstallFailure.UpdaterNotReady, result.Failure);
        Assert.True(File.Exists(StagedUpdaterPath));
        Assert.Equal(OldUpdater, File.ReadAllBytes(StagedUpdaterPath));
    }

    [Fact]
    public async Task PrepareDelta_WhenLocalUpdaterIsTampered_Refuses()
    {
        var package = CreateDeltaPackage(updaterChanged: false);
        File.WriteAllBytes(ReleaseInstallation.PathOf(Installation, ReleaseInstallation.UpdaterName), Encoding.UTF8.GetBytes("tampered updater"));

        var result = await CreateInstaller().PrepareAsync(package, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateInstallFailure.PackageInvalid, result.Failure);
        Assert.False(File.Exists(StagedUpdaterPath));
    }

    [Fact]
    public async Task PrepareDelta_WhenUpdaterIsChanged_ExtractsNewUpdaterFromDeltaZip()
    {
        var package = CreateDeltaPackage(updaterChanged: true);

        var result = await CreateInstaller().PrepareAsync(package, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateInstallFailure.UpdaterNotReady, result.Failure);
        Assert.True(File.Exists(StagedUpdaterPath));
        Assert.Equal(NewUpdater, File.ReadAllBytes(StagedUpdaterPath));
    }

    [Fact]
    public async Task PrepareDelta_WhenDeltaZipSha256Mismatches_Refuses()
    {
        var package = CreateDeltaPackage(updaterChanged: true, tamperZip: zip => File.AppendAllBytes(zip, [0x01]));

        var result = await CreateInstaller().PrepareAsync(package, TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(UpdateInstallFailure.PackageInvalid, result.Failure);
        Assert.False(File.Exists(StagedUpdaterPath));
    }

    [Fact]
    public async Task UpdateCheckService_PrepareInstallAsyncRejectsDelta_WhilePrepareDeltaInstallAsyncCallsInstaller()
    {
        var package = CreateDeltaPackage(updaterChanged: false);
        var fakeInstaller = new StubInstaller();
        var service = new UpdateCheckService(
            new FakeFeed(),
            new FakeDownload(),
            new UpdateEnvironment("0.2.0", true, Architecture.X64),
            fakeInstaller);

        var fullAttempt = await service.PrepareInstallAsync(package, TestContext.Current.CancellationToken);
        Assert.False(fullAttempt.Succeeded);
        Assert.Equal(UpdateInstallFailure.NotAReleaseInstallation, fullAttempt.Failure);
        Assert.Equal(0, fakeInstaller.Calls);

        var deltaAttempt = await service.PrepareDeltaInstallAsync(package, TestContext.Current.CancellationToken);
        Assert.Equal("stub", deltaAttempt.Detail);
        Assert.Equal(1, fakeInstaller.Calls);
    }

    private sealed class StubInstaller : IUpdateInstaller
    {
        public int Calls { get; private set; }

        public bool IsAvailable => true;

        public Task<UpdateInstallPreparation> PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(UpdateInstallPreparation.Failed(UpdateInstallFailure.UpdaterNotReady, "stub"));
        }
    }

    private sealed class FakeFeed : IUpdateManifestSource
    {
        public Task<string> FetchAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDownload : IUpdatePackageDownloader
    {
        public Task<VerifiedUpdatePackage?> FindVerifiedAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<VerifiedUpdatePackage> DownloadAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
