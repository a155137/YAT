using System.Net;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using YAT.Infrastructure.Updates;

namespace YAT.Infrastructure.Tests;

// The update client's transport and storage (Task #051.B), over a fake HTTP handler and a folder of the test's own -
// never the network: the manifest's statuses, limits and timeout; the package streamed to a .partial file, its size and
// SHA-256 checked as it comes, renamed only when both match, and the manifest kept beside it; every failure and
// cancellation leaving no .partial; a package already there trusted only once verified again.
public sealed class UpdateDownloadTests : IDisposable
{
    private static readonly Uri ManifestUrl = new("https://updates.example.test/latest/yat-update.json");
    private static readonly Uri PackageUrl = new("https://updates.example.test/v0.3.0/YAT-v0.3.0-win-x64.zip");
    private static readonly byte[] Payload = [.. Enumerable.Range(0, 300_000).Select(index => (byte)(index * 31 % 251))];
    private static readonly string PayloadSha = Convert.ToHexStringLower(SHA256.HashData(Payload));
    private static readonly ReleaseVersion Version = ReleaseVersion.Parse("0.3.0");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yat-tests", Guid.NewGuid().ToString("N"), "updates");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // ---- Fake HTTP ----

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    // A body that hands out its bytes in chunks; it can break off, stall, or hide its length.
    private sealed class Body(byte[] data, int breakAfter = -1, int stallAfter = -1) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (breakAfter >= 0 && _position >= breakAfter)
            {
                throw new IOException("The connection was reset.");
            }

            if (stallAfter >= 0 && _position >= stallAfter)
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var count = Math.Min(Math.Min(buffer.Length, 16_384), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer.Span);
            _position += count;
            return count;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static HttpResponseMessage Response(HttpStatusCode status, HttpContent? content = null, Uri? finalUrl = null) =>
        new(status) { Content = content ?? new ByteArrayContent([]), RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl ?? PackageUrl) };

    private static HttpContent Content(byte[] data, long? declaredLength = -1, int breakAfter = -1, int stallAfter = -1)
    {
        var content = new StreamContent(new Body(data, breakAfter, stallAfter));
        content.Headers.ContentLength = declaredLength == -1 ? data.Length : declaredLength;
        return content;
    }

    private static UpdateOffer Offer(long? size = null, string? sha = null, Uri? url = null)
    {
        var package = new ReleasePackage("win-x64", url ?? PackageUrl, sha ?? PayloadSha, size ?? Payload.Length);
        return new UpdateOffer(new ReleaseManifest(Version, [package], null, new Uri("https://updates.example.test/notes")), package);
    }

    private (HttpUpdatePackageDownloader Downloader, UpdatePackageStore Store, Handler Handler) Downloader(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? idle = null)
    {
        var handler = new Handler(respond);
        var client = UpdateHttp.CreateClient("0.2.0", handler);
        var store = new UpdatePackageStore(_root);
        return (new HttpUpdatePackageDownloader(client, store, idle), store, handler);
    }

    private (HttpUpdatePackageDownloader Downloader, UpdatePackageStore Store, Handler Handler) Serving(Func<HttpContent> content) =>
        Downloader((_, _) => Task.FromResult(Response(HttpStatusCode.OK, content())));

    private HttpUpdateManifestSource Manifest(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeSpan? timeout = null) =>
        new(UpdateHttp.CreateClient("0.2.0", new Handler(respond)), ManifestUrl, timeout);

    private static async Task<UpdateTransportException> Fails(Func<Task> action) => await Assert.ThrowsAsync<UpdateTransportException>(action);

    private string[] Files() =>
        Directory.Exists(_root) ? [.. Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(_root, path)).Order(StringComparer.Ordinal)] : [];

    // ---- The manifest ----

    [Fact]
    public async Task TheManifestIsReadAsText()
    {
        var source = Manifest((request, _) => Task.FromResult(Response(HttpStatusCode.OK, new StringContent("{ \"schemaVersion\": 1 }", Encoding.UTF8), request.RequestUri)));

        Assert.Equal("{ \"schemaVersion\": 1 }", await source.FetchAsync(CancellationToken.None));
    }

    [Fact]
    public async Task TheRequestNamesYatAndItsVersionAndCarriesNoCredentials()
    {
        Handler? seen = null;
        var handler = new Handler((request, _) => Task.FromResult(Response(HttpStatusCode.OK, new StringContent("{}"), request.RequestUri)));
        seen = handler;
        await new HttpUpdateManifestSource(UpdateHttp.CreateClient("0.2.0", handler), ManifestUrl).FetchAsync(CancellationToken.None);

        var request = Assert.Single(seen.Requests);
        Assert.Equal("YAT/0.2.0", request.Headers.UserAgent.ToString());
        Assert.Null(request.Headers.Authorization);
        Assert.Equal(ManifestUrl, request.RequestUri);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, UpdateFailure.ManifestNotFound)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFailure.HttpError)]
    [InlineData(HttpStatusCode.ServiceUnavailable, UpdateFailure.HttpError)]
    [InlineData(HttpStatusCode.Forbidden, UpdateFailure.HttpError)]
    public async Task AnErrorStatusIsItsOwnFailure(HttpStatusCode status, UpdateFailure failure)
    {
        var source = Manifest((request, _) => Task.FromResult(Response(status, finalUrl: request.RequestUri)));

        var exception = await Fails(() => source.FetchAsync(CancellationToken.None));

        Assert.Equal(failure, exception.Failure);
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), exception.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableServerIsNetworkUnavailable()
    {
        var source = Manifest((_, _) => throw new HttpRequestException("No such host is known."));

        Assert.Equal(UpdateFailure.NetworkUnavailable, (await Fails(() => source.FetchAsync(CancellationToken.None))).Failure);
    }

    [Fact]
    public async Task AServerThatDoesNotAnswerTimesOut()
    {
        var source = Manifest(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return Response(HttpStatusCode.OK);
        }, timeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(UpdateFailure.Timeout, (await Fails(() => source.FetchAsync(CancellationToken.None))).Failure);
    }

    [Fact]
    public async Task CancellingTheCheckIsNotATimeout()
    {
        using var cancellation = new CancellationTokenSource();
        var source = Manifest(async (_, token) =>
        {
            await cancellation.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return Response(HttpStatusCode.OK);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.FetchAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AManifestOfMoreThanOneMegabyteIsRefused(bool declared)
    {
        var big = new byte[HttpUpdateManifestSource.MaximumBytes + 1];
        var source = Manifest((request, _) => Task.FromResult(Response(HttpStatusCode.OK, Content(big, declared ? big.Length : null), request.RequestUri)));

        var exception = await Fails(() => source.FetchAsync(CancellationToken.None));

        Assert.Equal(UpdateFailure.ManifestInvalid, exception.Failure);
        Assert.Contains("1 MB", exception.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestRedirectedAwayFromHttpsIsRefused()
    {
        var source = Manifest((_, _) => Task.FromResult(Response(HttpStatusCode.OK, new StringContent("{}"), new Uri("http://updates.example.test/yat-update.json"))));

        Assert.Equal(UpdateFailure.HttpError, (await Fails(() => source.FetchAsync(CancellationToken.None))).Failure);
    }

    [Fact]
    public void AManifestAddressMustBeHttps()
    {
        Assert.Throws<ArgumentException>(() => new HttpUpdateManifestSource(UpdateHttp.CreateClient("0.2.0"), new Uri("http://updates.example.test/yat-update.json")));
    }

    // ---- Downloading ----

    [Fact]
    public async Task APackageIsStreamedVerifiedAndKeptWithItsManifest()
    {
        var (downloader, store, handler) = Serving(() => Content(Payload));
        var reports = new List<UpdateDownloadProgress>();

        var package = await downloader.DownloadAsync(Offer(), "win-x64", new Synchronous(reports.Add), CancellationToken.None);

        Assert.Equal(store.PackagePath(Version, "win-x64"), package.LocalPackagePath);
        Assert.Equal((Version, "win-x64", (long)Payload.Length, PayloadSha), (package.Version, package.Rid, package.Size, package.Sha256));
        Assert.Equal(Payload, File.ReadAllBytes(package.LocalPackagePath));
        Assert.Equal([@"v0.3.0\YAT-v0.3.0-win-x64.zip", @"v0.3.0\yat-update.json"], Files());
        var snapshot = ReleaseManifestReader.Read(File.ReadAllText(store.SnapshotPath(Version)));
        Assert.Equal(Offer().Manifest.ToJson(), File.ReadAllBytes(store.SnapshotPath(Version)));
        Assert.True(snapshot.IsValid);
        Assert.Equal(PackageUrl, Assert.Single(handler.Requests).RequestUri);
        Assert.True(reports.Count > 2);
        Assert.True(reports[^1].Verifying);
        Assert.Equal(reports.Select(report => report.BytesReceived).Order(), reports.Select(report => report.BytesReceived));
    }

    [Fact]
    public async Task APackageWithoutAContentLengthIsCheckedByWhatCame()
    {
        var (downloader, _, _) = Serving(() => Content(Payload, declaredLength: null));

        var package = await downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None);

        Assert.Equal(PayloadSha, package.Sha256);
    }

    [Fact]
    public async Task AContentLengthThatIsNotTheManifestsSizeIsRefusedAtOnce()
    {
        var (downloader, _, _) = Serving(() => Content(Payload, declaredLength: Payload.Length + 1));

        var exception = await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None));

        Assert.Equal(UpdateFailure.SizeMismatch, exception.Failure);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task MoreBytesThanTheManifestSaysStopTheDownload()
    {
        var (downloader, _, _) = Serving(() => Content([.. Payload, .. new byte[50_000]], declaredLength: null));

        var exception = await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None));

        Assert.Equal(UpdateFailure.SizeMismatch, exception.Failure);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task FewerBytesThanTheManifestSaysAreAnIncompletePackage()
    {
        var (downloader, _, _) = Serving(() => Content(Payload[..100_000], declaredLength: null));

        var exception = await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None));

        Assert.Equal(UpdateFailure.SizeMismatch, exception.Failure);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task AnotherSha256IsRefusedAndNeverGetsThePackagesName()
    {
        var (downloader, store, _) = Serving(() => Content(Payload));

        var exception = await Fails(() => downloader.DownloadAsync(Offer(sha: new string('a', 64)), "win-x64", null, CancellationToken.None));

        Assert.Equal(UpdateFailure.HashMismatch, exception.Failure);
        Assert.False(File.Exists(store.PackagePath(Version, "win-x64")));
        Assert.Empty(Files());
    }

    [Fact]
    public async Task APackageDeclaredLargerThanTwoGigabytesIsNotDownloaded()
    {
        var (downloader, _, handler) = Serving(() => Content(Payload));

        var exception = await Fails(() => downloader.DownloadAsync(Offer(size: HttpUpdatePackageDownloader.MaximumPackageBytes + 1), "win-x64", null, CancellationToken.None));

        Assert.Equal(UpdateFailure.ManifestInvalid, exception.Failure);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, UpdateFailure.PackageNotFound)]
    [InlineData(HttpStatusCode.InternalServerError, UpdateFailure.HttpError)]
    public async Task APackageThatIsNotServedIsItsOwnFailure(HttpStatusCode status, UpdateFailure failure)
    {
        var (downloader, _, _) = Downloader((_, _) => Task.FromResult(Response(status)));

        Assert.Equal(failure, (await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None))).Failure);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task AnUnreachablePackageServerIsNetworkUnavailable()
    {
        var (downloader, _, _) = Downloader((_, _) => throw new HttpRequestException("No such host is known."));

        Assert.Equal(UpdateFailure.NetworkUnavailable, (await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None))).Failure);
    }

    [Fact]
    public async Task ADownloadThatBreaksOffIsInterruptedAndLeavesNothing()
    {
        var (downloader, _, _) = Serving(() => Content(Payload, breakAfter: 120_000));

        var exception = await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None));

        Assert.Equal(UpdateFailure.DownloadInterrupted, exception.Failure);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task ADownloadThatStopsReceivingTimesOutAndLeavesNothing()
    {
        var (downloader, _, _) = Downloader((_, _) => Task.FromResult(Response(HttpStatusCode.OK, Content(Payload, stallAfter: 50_000))), idle: TimeSpan.FromMilliseconds(200));

        var exception = await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None));

        Assert.Equal(UpdateFailure.Timeout, exception.Failure);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNoPartialFile()
    {
        using var cancellation = new CancellationTokenSource();
        var (downloader, store, _) = Serving(() => Content(Payload));
        string? sawPartial = null;
        downloader.AfterChunkWritten = received =>
        {
            if (received > 100_000 && !cancellation.IsCancellationRequested)
            {
                sawPartial = File.Exists(store.PartialPath(Version, "win-x64")) ? "yes" : "no";
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloader.DownloadAsync(Offer(), "win-x64", null, cancellation.Token));

        Assert.Equal("yes", sawPartial);
        Assert.Empty(Files());
    }

    [Fact]
    public async Task APartialFileLeftByAnEarlierAttemptIsReplaced()
    {
        var (downloader, store, _) = Serving(() => Content(Payload));
        Directory.CreateDirectory(store.Folder(Version));
        File.WriteAllText(store.PartialPath(Version, "win-x64"), "half of an old download");

        var package = await downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None);

        Assert.Equal(Payload, File.ReadAllBytes(package.LocalPackagePath));
        Assert.False(File.Exists(store.PartialPath(Version, "win-x64")));
    }

    // ---- Storage ----

    [Fact]
    public async Task AStoreThatCannotBeMadeIsAStorageFailure()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "a file where the folder should be");
        var (downloader, _, _) = Serving(() => Content(Payload));

        Assert.Equal(UpdateFailure.StorageFailed, (await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None))).Failure);
    }

    [Fact]
    public async Task APackageThatCannotTakeItsNameIsAStorageFailureAndLeavesNoPartialFile()
    {
        var (downloader, store, _) = Serving(() => Content(Payload));
        Directory.CreateDirectory(store.PackagePath(Version, "win-x64"));

        Assert.Equal(UpdateFailure.StorageFailed, (await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None))).Failure);
        Assert.False(File.Exists(store.PartialPath(Version, "win-x64")));
    }

    [Fact]
    public async Task APackageWhoseManifestCannotBeWrittenIsNotReady()
    {
        var (downloader, store, _) = Serving(() => Content(Payload));
        Directory.CreateDirectory(store.SnapshotPath(Version));

        Assert.Equal(UpdateFailure.StorageFailed, (await Fails(() => downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None))).Failure);
    }

    // ---- A package already there ----

    [Fact]
    public async Task AVerifiedPackageIsFoundAgainOnlyAfterItIsVerifiedAgain()
    {
        var (downloader, store, handler) = Serving(() => Content(Payload));
        await downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None);
        File.Delete(store.SnapshotPath(Version));

        var found = await downloader.FindVerifiedAsync(Offer(), "win-x64", null, CancellationToken.None);

        Assert.Equal((PayloadSha, (long)Payload.Length), (found!.Sha256, found.Size));
        Assert.True(File.Exists(store.SnapshotPath(Version)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task ATamperedPackageIsNotFound()
    {
        var (downloader, store, _) = Serving(() => Content(Payload));
        await downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None);
        var path = store.PackagePath(Version, "win-x64");
        var bytes = File.ReadAllBytes(path);
        bytes[1000] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Null(await downloader.FindVerifiedAsync(Offer(), "win-x64", null, CancellationToken.None));
    }

    [Fact]
    public async Task APackageOfAnotherSizeIsNotFound()
    {
        var (downloader, store, _) = Serving(() => Content(Payload));
        Directory.CreateDirectory(store.Folder(Version));
        File.WriteAllBytes(store.PackagePath(Version, "win-x64"), Payload[..1000]);
        File.WriteAllBytes(store.SnapshotPath(Version), Offer().Manifest.ToJson());

        Assert.Null(await downloader.FindVerifiedAsync(Offer(), "win-x64", null, CancellationToken.None));
    }

    [Fact]
    public async Task NoPackageIsNothingFound()
    {
        var (downloader, _, _) = Serving(() => Content(Payload));

        Assert.Null(await downloader.FindVerifiedAsync(Offer(), "win-x64", null, CancellationToken.None));
    }

    [Fact]
    public async Task OtherVersionsAreLeftAsTheyAre()
    {
        var (downloader, store, _) = Serving(() => Content(Payload));
        var older = Path.Combine(store.Folder(ReleaseVersion.Parse("0.2.0")), "YAT-v0.2.0-win-x64.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(older)!);
        File.WriteAllText(older, "an older package");

        await downloader.DownloadAsync(Offer(), "win-x64", null, CancellationToken.None);

        Assert.Equal("an older package", File.ReadAllText(older));
    }

    // ---- Paths ----

    [Fact]
    public async Task LocalNamesComeFromTheVersionAndRuntimeNeverFromTheAddressOrHeaders()
    {
        var hostile = new Uri("https://updates.example.test/a/../../../Windows/evil%2F..%5Cpayload.exe?name=..\\..\\x.dll");
        var (downloader, store, _) = Downloader((_, _) =>
        {
            var content = Content(Payload);
            content.Headers.ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment") { FileName = @"..\..\evil.exe" };
            return Task.FromResult(Response(HttpStatusCode.OK, content, hostile));
        });

        var package = await downloader.DownloadAsync(Offer(url: hostile), "win-x64", null, CancellationToken.None);

        Assert.Equal(Path.Combine(_root, "v0.3.0", "YAT-v0.3.0-win-x64.zip"), package.LocalPackagePath);
        Assert.Equal([@"v0.3.0\YAT-v0.3.0-win-x64.zip", @"v0.3.0\yat-update.json"], Files());
    }

    [Theory]
    [InlineData(@"..\x")]
    [InlineData("win/x64")]
    [InlineData("WIN-X64")]
    [InlineData("")]
    public void ARuntimeIdThatCouldBeAPathIsRefused(string rid)
    {
        Assert.Throws<ArgumentException>(() => new UpdatePackageStore(_root).PackagePath(Version, rid));
    }

    [Fact]
    public void TheDefaultStoreIsInLocalApplicationDataNeverTheRoamingSettings()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YAT", "updates"),
            UpdatePackageStore.DefaultRoot);
        Assert.DoesNotContain(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), UpdatePackageStore.DefaultRoot, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Synchronous(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        public void Report(UpdateDownloadProgress value) => report(value);
    }
}
