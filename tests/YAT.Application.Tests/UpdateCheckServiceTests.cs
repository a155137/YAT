using System.Runtime.InteropServices;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Application.Tests;

// Checking for an update and downloading one (Task #051.B), over fake sources: the manifest read with the release
// contract's own reader, versions compared, the win-x64 package chosen - and every failure a result of its own, never
// "up to date". Nothing is downloaded until DownloadAsync is called.
public class UpdateCheckServiceTests
{
    private const string Sha = "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a";

    private static readonly UpdateEnvironment Windows = new("0.2.0", true, Architecture.X64);

    private static string Manifest(string version = "0.3.0", string rid = "win-x64", string extra = "") => $$"""
        { "schemaVersion": 1, "version": "{{version}}"{{extra}},
          "packages": [ { "rid": "{{rid}}", "url": "https://example.test/YAT-v{{version}}-{{rid}}.zip", "sha256": "{{Sha}}", "size": 1000 } ] }
        """;

    private sealed class Source(Func<CancellationToken, Task<string>> fetch) : IUpdateManifestSource
    {
        public int Fetches { get; private set; }

        public Task<string> FetchAsync(CancellationToken cancellationToken)
        {
            Fetches++;
            return fetch(cancellationToken);
        }
    }

    private sealed class Downloader : IUpdatePackageDownloader
    {
        public VerifiedUpdatePackage? Existing { get; set; }

        public Exception? Failure { get; set; }

        public List<string> Calls { get; } = [];

        public Task<VerifiedUpdatePackage?> FindVerifiedAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add($"find {offer.Version} {rid}");
            return Task.FromResult(Existing);
        }

        public Task<VerifiedUpdatePackage> DownloadAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add($"download {offer.Version} {rid}");
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                throw Failure;
            }

            progress?.Report(new UpdateDownloadProgress(offer.Package.Size, offer.Package.Size, Verifying: true));
            return Task.FromResult(new VerifiedUpdatePackage(offer.Version, rid, $@"C:\updates\{offer.Version.Tag}\x.zip", offer.Package.Size, offer.Package.Sha256, offer.ReleaseNotesUrl));
        }
    }

    private static (UpdateCheckService Service, Source Source, Downloader Downloader) Service(string json, UpdateEnvironment? environment = null)
    {
        var source = new Source(_ => Task.FromResult(json));
        var downloader = new Downloader();
        return (new UpdateCheckService(source, downloader, environment ?? Windows), source, downloader);
    }

    private static UpdateCheckService Failing(UpdateFailure failure)
    {
        var source = new Source(_ => throw new UpdateTransportException(failure, "detail"));
        return new UpdateCheckService(source, new Downloader(), Windows);
    }

    // ---- Versions ----

    [Fact]
    public async Task ANewerReleaseIsOfferedAndNothingIsDownloaded()
    {
        var (service, _, downloader) = Service(Manifest("0.3.0", extra: """, "releaseNotesUrl": "https://example.test/notes" """));

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(ReleaseVersion.Parse("0.2.0"), result.Installed);
        Assert.Equal(ReleaseVersion.Parse("0.3.0"), result.Available);
        Assert.Equal("win-x64", result.Offer!.Package.Rid);
        Assert.Equal(1000, result.Offer.Package.Size);
        Assert.Equal("https://example.test/notes", result.Offer.ReleaseNotesUrl!.AbsoluteUri);
        Assert.Null(result.Failure);
        Assert.Empty(downloader.Calls);
    }

    [Fact]
    public async Task TheSameVersionIsUpToDate()
    {
        var result = await Service(Manifest("0.2.0")).Service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.UpToDate, result.Status);
        Assert.Null(result.Offer);
    }

    [Fact]
    public async Task AnOlderPublishedReleaseIsKeptApartFromUpToDate()
    {
        var result = await Service(Manifest("0.1.9")).Service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.RemoteVersionOlder, result.Status);
        Assert.Equal(ReleaseVersion.Parse("0.1.9"), result.Available);
        Assert.Null(result.Offer);
    }

    [Theory]
    [InlineData("0.2.0-beta.1")]
    [InlineData("0.2")]
    [InlineData("")]
    public async Task AnInstalledVersionThatIsNotStableIsUnknownAndNothingIsFetched(string installed)
    {
        var (service, source, _) = Service(Manifest(), Windows with { InstalledVersion = installed });

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal((UpdateCheckStatus.Failed, UpdateFailure.InstalledVersionUnknown), (result.Status, result.Failure));
        Assert.Equal(0, source.Fetches);
    }

    [Theory]
    [InlineData(false, Architecture.X64)]
    [InlineData(true, Architecture.Arm64)]
    [InlineData(true, Architecture.X86)]
    public async Task AnotherPlatformIsUnsupportedAndNothingIsFetched(bool isWindows, Architecture architecture)
    {
        var (service, source, _) = Service(Manifest(), Windows with { IsWindows = isWindows, ProcessArchitecture = architecture });

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateFailure.UnsupportedPlatform, result.Failure);
        Assert.Equal(0, source.Fetches);
    }

    // ---- The manifest ----

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    public async Task TextThatIsNotJsonIsMalformed(string json)
    {
        Assert.Equal(UpdateFailure.ManifestMalformed, (await Service(json).Service.CheckAsync(CancellationToken.None)).Failure);
    }

    [Fact]
    public async Task ANewerSchemaIsSaidAsSuch()
    {
        var result = await Service("""{ "schemaVersion": 2, "anything": true }""").Service.CheckAsync(CancellationToken.None);

        Assert.Equal((UpdateCheckStatus.Failed, UpdateFailure.ManifestNewerSchema), (result.Status, result.Failure));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.3.0", "packages": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.3.0", "packages": [ { "rid": "win-x64", "url": "http://example.test/p.zip", "sha256": "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a", "size": 1 } ] }""")]
    public async Task AManifestThatBreaksTheContractIsInvalid(string json)
    {
        var result = await Service(json).Service.CheckAsync(CancellationToken.None);

        Assert.Equal((UpdateCheckStatus.Failed, UpdateFailure.ManifestInvalid), (result.Status, result.Failure));
    }

    [Fact]
    public async Task AReleaseWithoutAWinX64PackageHasNothingForThisComputer()
    {
        var result = await Service(Manifest(rid: "win-arm64")).Service.CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateFailure.NoPackageForRuntime, result.Failure);
    }

    // ---- Transport ----

    [Theory]
    [InlineData(UpdateFailure.ManifestNotFound)]
    [InlineData(UpdateFailure.NetworkUnavailable)]
    [InlineData(UpdateFailure.Timeout)]
    [InlineData(UpdateFailure.HttpError)]
    [InlineData(UpdateFailure.ManifestInvalid)]
    public async Task ATransportFailureIsAFailedCheckNeverUpToDate(UpdateFailure failure)
    {
        var result = await Failing(failure).CheckAsync(CancellationToken.None);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(failure, result.Failure);
        Assert.Equal("detail", result.Detail);
        Assert.Equal(ReleaseVersion.Parse("0.2.0"), result.Installed);
    }

    [Fact]
    public async Task CancellingTheCheckIsNotAFailure()
    {
        var source = new Source(token => Task.FromCanceled<string>(token));
        var service = new UpdateCheckService(source, new Downloader(), Windows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CheckAsync(cancellation.Token));
    }

    // ---- Downloading, when asked ----

    private static async Task<(UpdateCheckService Service, Downloader Downloader, UpdateOffer Offer)> Offered()
    {
        var (service, _, downloader) = Service(Manifest());
        var offer = (await service.CheckAsync(CancellationToken.None)).Offer!;
        return (service, downloader, offer);
    }

    [Fact]
    public async Task DownloadingGivesAVerifiedPackageOfTheOffer()
    {
        var (service, downloader, offer) = await Offered();
        var reports = new List<UpdateDownloadProgress>();

        var result = await service.DownloadAsync(offer, new SynchronousProgress(reports.Add), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal((ReleaseVersion.Parse("0.3.0"), "win-x64", 1000L, Sha), (result.Package!.Version, result.Package.Rid, result.Package.Size, result.Package.Sha256));
        Assert.Equal(["find 0.3.0 win-x64", "download 0.3.0 win-x64"], downloader.Calls);
        Assert.True(Assert.Single(reports).Verifying);
    }

    [Fact]
    public async Task APackageAlreadyVerifiedIsUsedWithoutDownloadingAgain()
    {
        var (service, downloader, offer) = await Offered();
        downloader.Existing = new VerifiedUpdatePackage(offer.Version, "win-x64", @"C:\updates\v0.3.0\x.zip", 1000, Sha, null);

        var result = await service.DownloadAsync(offer, null, CancellationToken.None);

        Assert.Same(downloader.Existing, result.Package);
        Assert.Equal(["find 0.3.0 win-x64"], downloader.Calls);
    }

    [Theory]
    [InlineData(UpdateFailure.SizeMismatch)]
    [InlineData(UpdateFailure.HashMismatch)]
    [InlineData(UpdateFailure.DownloadInterrupted)]
    [InlineData(UpdateFailure.PackageNotFound)]
    [InlineData(UpdateFailure.StorageFailed)]
    public async Task AFailedDownloadIsAResultWithoutAPackage(UpdateFailure failure)
    {
        var (service, downloader, offer) = await Offered();
        downloader.Failure = new UpdateTransportException(failure, "why");

        var result = await service.DownloadAsync(offer, null, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Null(result.Package);
        Assert.Equal((failure, "why"), (result.Failure!.Value, result.Detail));
    }

    [Fact]
    public async Task CancellingTheDownloadIsNotAFailure()
    {
        var (service, _, offer) = await Offered();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DownloadAsync(offer, null, cancellation.Token));
    }

    private sealed class SynchronousProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
