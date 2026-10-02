using System.Net;
using System.Net.Http.Headers;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Infrastructure.Updates;

// The one HttpClient YAT checks for and downloads updates with (Task #051.B): made once for the application's lifetime
// and disposed when it exits. Redirects are followed (a release's "latest" address redirects to the release and then to
// its storage), but never from https to http; the system's proxy settings apply. No authentication of any kind is sent.
// Timeouts are per request (see the manifest source and the downloader), so the client itself has none.
public static class UpdateHttp
{
    public static HttpClient CreateClient(string userAgentVersion, HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15)
        };

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("YAT", userAgentVersion));
        return client;
    }

    // Whether a response came, in the end, from an https address.
    internal static bool EndedHttps(HttpResponseMessage response) =>
        response.RequestMessage?.RequestUri is not { } uri || uri.Scheme == Uri.UriSchemeHttps;
}

// The release manifest from its published address (Task #051.B): https only, at most 1 MB, within 30 seconds. What goes
// wrong is said as an UpdateFailure - a 404 is ManifestNotFound (no release published yet), never "no update".
public sealed class HttpUpdateManifestSource : IUpdateManifestSource
{
    public const int MaximumBytes = 1024 * 1024;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _client;
    private readonly Uri _url;
    private readonly TimeSpan _timeout;

    public HttpUpdateManifestSource(HttpClient client, Uri url, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(url);
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException($"The update manifest address '{url}' is not https.", nameof(url));
        }

        _client = client;
        _url = url;
        _timeout = timeout ?? DefaultTimeout;
    }

    public async Task<string> FetchAsync(CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using var response = await _client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new UpdateTransportException(UpdateFailure.ManifestNotFound, "HTTP 404");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new UpdateTransportException(UpdateFailure.HttpError, $"HTTP {(int)response.StatusCode}");
            }

            if (!UpdateHttp.EndedHttps(response))
            {
                throw new UpdateTransportException(UpdateFailure.HttpError, "The update information was redirected away from https.");
            }

            if (response.Content.Headers.ContentLength is > MaximumBytes)
            {
                throw new UpdateTransportException(UpdateFailure.ManifestInvalid, "The update information is larger than 1 MB.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token)) > 0)
            {
                if (buffer.Length + read > MaximumBytes)
                {
                    throw new UpdateTransportException(UpdateFailure.ManifestInvalid, "The update information is larger than 1 MB.");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new UpdateTransportException(UpdateFailure.Timeout, null, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateTransportException(UpdateFailure.NetworkUnavailable, exception.Message, exception);
        }
        catch (IOException exception)
        {
            throw new UpdateTransportException(UpdateFailure.NetworkUnavailable, exception.Message, exception);
        }
    }
}

// Where downloaded update packages are kept (Task #051.B): %LOCALAPPDATA%\YAT\updates by default - never the roaming
// %APPDATA%\YAT of the user's settings. One folder per version, named after it; every name inside is made here from the
// validated version and the fixed runtime id, never from a remote address or header:
//
//     updates\v0.2.0\YAT-v0.2.0-win-x64.zip.partial    while it downloads; removed whenever a download does not finish
//     updates\v0.2.0\YAT-v0.2.0-win-x64.zip            only once its size and SHA-256 match the manifest
//     updates\v0.2.0\yat-update.json                   the manifest it was verified against
//
// Folders of other versions are left as they are.
public sealed class UpdatePackageStore
{
    public UpdatePackageStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        Root = Path.GetFullPath(root);
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YAT", "updates");

    public string Root { get; }

    public string Folder(ReleaseVersion version) => UpdatePackageLayout.Folder(Root, version);

    public string PackagePath(ReleaseVersion version, string rid) => UpdatePackageLayout.PackagePath(Root, version, rid);

    public string PartialPath(ReleaseVersion version, string rid) => PackagePath(version, rid) + ".partial";

    public string SnapshotPath(ReleaseVersion version) => UpdatePackageLayout.SnapshotPath(Root, version);
}
