using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YAT.Application.Updates;

namespace YAT.Infrastructure.Updates;

// Fetches release notes from the Git release API for the exact tag named in UpdateOffer.ReleaseNotesUrl
// (/repos/{owner}/{repo}/releases/tags/{tag}). Never queries an unversioned latest endpoint so notes always match the offered version.
public sealed class HttpGitHubReleaseNotesSource : IReleaseNotesSource
{
    public const int MaximumBytes = 256 * 1024;
    public const int MaximumNotesLength = 32_000;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    public HttpGitHubReleaseNotesSource(HttpClient client, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _timeout = timeout ?? DefaultTimeout;
    }

    public static Uri? TryBuildApiUri(Uri? releaseNotesUrl)
    {
        if (releaseNotesUrl is null ||
            releaseNotesUrl.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrWhiteSpace(releaseNotesUrl.Host))
        {
            return null;
        }

        // Expected: /{owner}/{repo}/releases/tag/{tag}
        var segments = releaseNotesUrl.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 5 ||
            !string.Equals(segments[2], "releases", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[3], "tag", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var apiHost = releaseNotesUrl.Host.StartsWith("api.", StringComparison.OrdinalIgnoreCase)
            ? releaseNotesUrl.Host
            : $"api.{releaseNotesUrl.Host}";
        var owner = Uri.EscapeDataString(Uri.UnescapeDataString(segments[0]));
        var repo = Uri.EscapeDataString(Uri.UnescapeDataString(segments[1]));
        var tag = Uri.EscapeDataString(Uri.UnescapeDataString(segments[4]));
        return new Uri($"https://{apiHost}/repos/{owner}/{repo}/releases/tags/{tag}");
    }

    public async Task<string?> FetchAsync(UpdateOffer offer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var apiUri = TryBuildApiUri(offer.ReleaseNotesUrl);
        if (apiUri is null)
        {
            return null;
        }

        using var timeout = new CancellationTokenSource(_timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, apiUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode || !UpdateHttp.EndedHttps(response))
            {
                return null;
            }

            if (response.Content.Headers.ContentLength is > MaximumBytes)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(linked.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, linked.Token)) > 0)
            {
                if (buffer.Length + read > MaximumBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            var json = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("body", out var bodyElement) ||
                bodyElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var body = bodyElement.GetString()?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return body.Length > MaximumNotesLength ? body[..MaximumNotesLength] + "\n..." : body;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
