using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using YAT.Infrastructure.Distribution;
using YAT.Infrastructure.Updates;

namespace YAT.Infrastructure.Tests;

public sealed class DeltaDownloadAndFallbackTests : IDisposable
{
    private const string Rid = "win-x64";
    private static readonly ReleaseVersion BaseVersion = ReleaseVersion.Parse("0.4.2");
    private static readonly ReleaseVersion TargetVersion = ReleaseVersion.Parse("0.4.3");
    private static readonly UpdateEnvironment Env = new("0.4.2", true, Architecture.X64);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yat-stage3-tests", Guid.NewGuid().ToString("N"));
    private string InstallDir => Path.Combine(_root, "install");
    private string UpdatesDir => Path.Combine(_root, "updates");

    public DeltaDownloadAndFallbackTests()
    {
        Directory.CreateDirectory(InstallDir);
        Directory.CreateDirectory(UpdatesDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private (byte[] FullZipBytes, byte[] DeltaZipBytes, string BaseInvSha, string FullSha, string DeltaSha) BuildPackages(
        bool writeLocalInstall = true,
        bool corruptDeltaZip = false,
        string? overrideBaseInvSha = null)
    {
        var baseFiles = new Dictionary<string, byte[]>
        {
            ["YAT.exe"] = Encoding.UTF8.GetBytes("exe-0.4.2"),
            ["duckdb.dll"] = Encoding.UTF8.GetBytes("duckdb-kept"),
            ["old.dll"] = Encoding.UTF8.GetBytes("removed-in-0.4.3")
        };

        var targetFiles = new Dictionary<string, byte[]>
        {
            ["YAT.exe"] = Encoding.UTF8.GetBytes("exe-0.4.3"),
            ["duckdb.dll"] = Encoding.UTF8.GetBytes("duckdb-kept"),
            ["new.dll"] = Encoding.UTF8.GetBytes("added-in-0.4.3")
        };

        var baseInventory = new ReleaseInventory(BaseVersion, Rid, baseFiles.Select(f => new ReleaseFile(f.Key, f.Value.Length, Sha(f.Value))));
        var baseInvBytes = baseInventory.ToJson();
        var baseInvSha = overrideBaseInvSha ?? Sha(baseInvBytes);

        if (writeLocalInstall)
        {
            foreach (var (rel, data) in baseFiles)
            {
                var full = Path.Combine(InstallDir, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, data);
            }

            File.WriteAllBytes(ReleaseInstallation.InventoryPath(InstallDir), baseInvBytes);
        }

        var targetInventory = new ReleaseInventory(TargetVersion, Rid, targetFiles.Select(f => new ReleaseFile(f.Key, f.Value.Length, Sha(f.Value))));
        var targetInvBytes = targetInventory.ToJson();

        byte[] fullZipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (rel, data) in targetFiles)
                {
                    using var s = zip.CreateEntry($"YAT-v{TargetVersion}-{Rid}/{rel}").Open();
                    s.Write(data);
                }

                using var inv = zip.CreateEntry($"YAT-v{TargetVersion}-{Rid}/{ReleaseInventory.FileName}").Open();
                inv.Write(targetInvBytes);
            }

            fullZipBytes = ms.ToArray();
        }

        byte[] deltaZipBytes;
        if (corruptDeltaZip)
        {
            deltaZipBytes = Encoding.UTF8.GetBytes("not-a-valid-zip");
        }
        else
        {
            var deltaManifest = new ReleaseDeltaManifest(BaseVersion, TargetVersion, Rid, baseInvSha, ["YAT.exe", "new.dll"], ["old.dll"]);
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                void Add(string rel, byte[] data)
                {
                    using var s = zip.CreateEntry($"YAT-v{TargetVersion}-{Rid}/{rel}").Open();
                    s.Write(data);
                }

                Add(ReleaseDeltaManifest.FileName, deltaManifest.ToJson());
                Add(ReleaseInventory.FileName, targetInvBytes);
                Add("YAT.exe", targetFiles["YAT.exe"]);
                Add("new.dll", targetFiles["new.dll"]);
            }

            deltaZipBytes = ms.ToArray();
        }

        return (fullZipBytes, deltaZipBytes, baseInvSha, Sha(fullZipBytes), Sha(deltaZipBytes));
    }

    private static UpdateOffer CreateOffer(byte[] fullZip, string fullSha, byte[] deltaZip, string deltaSha, ReleaseVersion? deltaBase = null)
    {
        var fullPkg = new ReleasePackage(Rid, new Uri("https://releases.example.test/full.zip"), fullSha, fullZip.Length);
        var deltaPkg = new ReleaseDeltaPackage(Rid, deltaBase ?? BaseVersion, new Uri("https://releases.example.test/diff.zip"), deltaSha, deltaZip.Length);
        var manifest = new ReleaseManifest(TargetVersion, [fullPkg], DateTimeOffset.UtcNow, null, [deltaPkg]);
        return new UpdateOffer(manifest, fullPkg);
    }

    private UpdateCheckService CreateService(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler,
        IUpdateInstaller? installer = null,
        ILocalDeltaPreVerifier? preVerifier = null)
    {
        var client = new HttpClient(new StubHandler(handler));
        var store = new UpdatePackageStore(UpdatesDir);
        var downloader = new HttpUpdatePackageDownloader(client, store);
        var source = new HttpUpdateManifestSource(client, new Uri("https://releases.example.test/yat-update.json"));
        return new UpdateCheckService(
            source,
            downloader,
            Env,
            installer,
            preVerifier ?? new LocalInstallationDeltaPreVerifier(),
            InstallDir);
    }

    [Fact]
    public async Task EligibleDeltaDownloadsVerifiesAndBlocksInstallationBeforeStage4()
    {
        var (fullZip, deltaZip, _, fullSha, deltaSha) = BuildPackages();
        var offer = CreateOffer(fullZip, fullSha, deltaZip, deltaSha);
        var requestedUrls = new List<string>();
        var installer = new RecordingInstaller();

        var service = CreateService((req, _) =>
        {
            requestedUrls.Add(req.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(deltaZip) });
        }, installer);

        var result = await service.DownloadDeltaOrFallbackAsync(offer, null, Token);

        Assert.True(result.Succeeded);
        Assert.True(result.Package!.IsDelta);
        Assert.Equal(BaseVersion, result.Package.BaseVersion);
        Assert.Single(requestedUrls);
        Assert.Equal("https://releases.example.test/diff.zip", requestedUrls[0]);
        Assert.True(File.Exists(result.Package.LocalPackagePath));
        Assert.False(File.Exists(UpdatePackageLayout.PackagePath(UpdatesDir, TargetVersion, Rid)));

        // Stage 3 safety boundary: PrepareInstallAsync MUST refuse delta packages without touching IUpdateInstaller.
        var prep = await service.PrepareInstallAsync(result.Package, Token);
        Assert.False(prep.Succeeded);
        Assert.Equal(0, installer.Calls);
    }

    [Fact]
    public async Task BaseVersionMismatchOrMissingLocalInventoryFallsBackImmediatelyToFullZip()
    {
        var (fullZip, deltaZip, _, fullSha, deltaSha) = BuildPackages(writeLocalInstall: false);
        var offerWithWrongBase = CreateOffer(fullZip, fullSha, deltaZip, deltaSha, deltaBase: ReleaseVersion.Parse("0.4.1"));
        var requestedUrls = new List<string>();

        var service = CreateService((req, _) =>
        {
            requestedUrls.Add(req.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fullZip) });
        });

        var res1 = await service.DownloadDeltaOrFallbackAsync(offerWithWrongBase, null, Token);
        Assert.True(res1.Succeeded);
        Assert.False(res1.Package!.IsDelta);
        Assert.Equal(["https://releases.example.test/full.zip"], requestedUrls);

        // Now matching baseVersion, but missing local yat-files.json -> still skips delta and downloads Full ZIP
        File.Delete(res1.Package.LocalPackagePath);
        requestedUrls.Clear();
        var offerMatchingBase = CreateOffer(fullZip, fullSha, deltaZip, deltaSha, deltaBase: BaseVersion);
        var res2 = await service.DownloadDeltaOrFallbackAsync(offerMatchingBase, null, Token);
        Assert.True(res2.Succeeded);
        Assert.False(res2.Package!.IsDelta);
        Assert.Equal(["https://releases.example.test/full.zip"], requestedUrls);
    }

    [Fact]
    public async Task DeltaDownloadErrorsCorruptedZipAndHashMismatchTriggerFullZipFallback()
    {
        var (fullZip, deltaZip, _, fullSha, deltaSha) = BuildPackages();

        // 1. Delta 404 -> Full ZIP fallback
        var offer = CreateOffer(fullZip, fullSha, deltaZip, deltaSha);
        var service404 = CreateService((req, _) => Task.FromResult(
            req.RequestUri!.AbsoluteUri.EndsWith("diff.zip", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(fullZip) }));
        var res404 = await service404.DownloadDeltaOrFallbackAsync(offer, null, Token);
        Assert.True(res404.Succeeded);
        Assert.False(res404.Package!.IsDelta);

        // 2. Delta SHA256 mismatch -> Full ZIP fallback
        var offerBadHash = CreateOffer(fullZip, fullSha, deltaZip, new string('f', 64));
        var serviceBadHash = CreateService((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(req.RequestUri!.AbsoluteUri.EndsWith("diff.zip", StringComparison.Ordinal) ? deltaZip : fullZip)
        }));
        var resBadHash = await serviceBadHash.DownloadDeltaOrFallbackAsync(offerBadHash, null, Token);
        Assert.True(resBadHash.Succeeded);
        Assert.False(resBadHash.Package!.IsDelta);

        // 3. Corrupted Delta ZIP (matching its declared hash) -> ReadDelta fails -> Full ZIP fallback
        var (fullZip2, corruptDelta, _, fullSha2, corruptSha) = BuildPackages(corruptDeltaZip: true);
        var offerCorrupt = CreateOffer(fullZip2, fullSha2, corruptDelta, corruptSha);
        var serviceCorrupt = CreateService((req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(req.RequestUri!.AbsoluteUri.EndsWith("diff.zip", StringComparison.Ordinal) ? corruptDelta : fullZip2)
        }));
        var resCorrupt = await serviceCorrupt.DownloadDeltaOrFallbackAsync(offerCorrupt, null, Token);
        Assert.True(resCorrupt.Succeeded);
        Assert.False(resCorrupt.Package!.IsDelta);
    }

    [Fact]
    public async Task LocalKeptFileTamperedMissingOrBaseInventoryMismatchTriggersFullZipFallback()
    {
        var (fullZip, deltaZip, _, fullSha, deltaSha) = BuildPackages();
        var offer = CreateOffer(fullZip, fullSha, deltaZip, deltaSha);

        // Tamper with kept file on disk
        File.WriteAllText(Path.Combine(InstallDir, "duckdb.dll"), "tampered-kept-file");

        var requestedUrls = new List<string>();
        var service = CreateService((req, _) =>
        {
            requestedUrls.Add(req.RequestUri!.AbsoluteUri);
            var bytes = req.RequestUri.AbsoluteUri.EndsWith("diff.zip", StringComparison.Ordinal) ? deltaZip : fullZip;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        });

        var res = await service.DownloadDeltaOrFallbackAsync(offer, null, Token);
        Assert.True(res.Succeeded);
        Assert.False(res.Package!.IsDelta);
        Assert.Equal(["https://releases.example.test/diff.zip", "https://releases.example.test/full.zip"], requestedUrls);
        Assert.False(File.Exists(UpdatePackageLayout.DeltaPackagePath(UpdatesDir, BaseVersion, TargetVersion, Rid)));

        // Missing kept file -> also falls back to Full ZIP
        File.Delete(Path.Combine(InstallDir, "duckdb.dll"));
        requestedUrls.Clear();
        var resMissing = await service.DownloadDeltaOrFallbackAsync(offer, null, Token);
        Assert.True(resMissing.Succeeded);
        Assert.False(resMissing.Package!.IsDelta);
    }

    [Fact]
    public async Task ReparsePointRejectionTriggersFullZipFallback()
    {
        var (fullZip, deltaZip, _, fullSha, deltaSha) = BuildPackages();
        var offer = CreateOffer(fullZip, fullSha, deltaZip, deltaSha);

        var service = CreateService(
            (req, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(req.RequestUri!.AbsoluteUri.EndsWith("diff.zip", StringComparison.Ordinal) ? deltaZip : fullZip)
            }),
            preVerifier: new RejectingPreVerifier("Kept file is a symlink/junction."));

        var res = await service.DownloadDeltaOrFallbackAsync(offer, null, Token);
        Assert.True(res.Succeeded);
        Assert.False(res.Package!.IsDelta);
    }

    [Fact]
    public async Task CancellationDoesNotTriggerFallbackAndBothFailingReportsError()
    {
        var (fullZip, deltaZip, _, fullSha, deltaSha) = BuildPackages();
        var offer = CreateOffer(fullZip, fullSha, deltaZip, deltaSha);

        using var cts = new CancellationTokenSource();
        var requestedUrls = new List<string>();
        var cancelService = CreateService((req, _) =>
        {
            requestedUrls.Add(req.RequestUri!.AbsoluteUri);
            cts.Cancel();
            throw new OperationCanceledException(cts.Token);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelService.DownloadDeltaOrFallbackAsync(offer, null, cts.Token));
        Assert.Equal(["https://releases.example.test/diff.zip"], requestedUrls);

        // Both Delta and Full fail -> returns failure cleanly
        var failService = CreateService((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        var failResult = await failService.DownloadDeltaOrFallbackAsync(offer, null, Token);
        Assert.False(failResult.Succeeded);
        Assert.Equal(UpdateFailure.PackageNotFound, failResult.Failure);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(request, cancellationToken);
    }

    private sealed class RecordingInstaller : IUpdateInstaller
    {
        public bool IsAvailable => true;
        public int Calls { get; private set; }
        public Task<UpdateInstallPreparation> PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(UpdateInstallPreparation.Failed(UpdateInstallFailure.NotAReleaseInstallation));
        }
    }

    private sealed class RejectingPreVerifier(string reason) : ILocalDeltaPreVerifier
    {
        public bool IsLocalInventoryEligible(string installationDirectory, ReleaseVersion expectedBaseVersion, string expectedRid) => true;
        public Task<DeltaPreVerificationResult> VerifyLocalAsync(
            string installationDirectory,
            ReleaseVersion expectedBaseVersion,
            string expectedRid,
            ReleaseDeltaManifest deltaManifest,
            ReleaseInventory targetInventory,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeltaPreVerificationResult(false, reason));
    }
}
