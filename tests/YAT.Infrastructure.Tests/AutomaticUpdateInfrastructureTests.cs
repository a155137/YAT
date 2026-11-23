using System.Net;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using YAT.Infrastructure.Updates;

namespace YAT.Infrastructure.Tests;

public sealed class AutomaticUpdateInfrastructureTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"yat-auto-update-tests-{Guid.NewGuid():N}");

    public AutomaticUpdateInfrastructureTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void StateStoreRoundTripsDateAndToleratesMissingOrCorruptFiles()
    {
        var store = new FileAutomaticUpdateCheckStateStore(_tempDirectory);
        Assert.Null(store.ReadLastCheckedDate());

        var date = new DateOnly(2026, 11, 24);
        store.WriteLastCheckedDate(date);
        Assert.Equal(date, store.ReadLastCheckedDate());

        File.WriteAllText(Path.Combine(_tempDirectory, "auto-check-state.json"), "{ invalid json");
        Assert.Null(store.ReadLastCheckedDate());
    }

    [Fact]
    public async Task ReleaseNotesSourceQueriesExactTagAndHandlesFailuresGracefully()
    {
        Assert.Equal(
            new Uri("https://api.github.com/repos/a155137/YAT/releases/tags/v0.5.0"),
            HttpGitHubReleaseNotesSource.TryBuildApiUri(new Uri("https://github.com/a155137/YAT/releases/tag/v0.5.0")));

        Assert.Null(HttpGitHubReleaseNotesSource.TryBuildApiUri(new Uri("https://github.com/a155137/YAT/releases/latest")));
        Assert.Null(HttpGitHubReleaseNotesSource.TryBuildApiUri(new Uri("http://github.com/a155137/YAT/releases/tag/v0.5.0")));

        var version = ReleaseVersion.Parse("0.5.0");
        var package = new ReleasePackage("win-x64", new Uri("https://example.test/yat.zip"), new string('a', 64), 100);
        var offer = new UpdateOffer(
            new ReleaseManifest(
                version,
                [package],
                DateTimeOffset.UtcNow,
                new Uri("https://github.com/a155137/YAT/releases/tag/v0.5.0")),
            package);

        // Valid GitHub API response
        using var okClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"tag_name":"v0.5.0","body":"Line 1\r\nLine 2"}""", Encoding.UTF8, "application/json")
            }));
        var notes = await new HttpGitHubReleaseNotesSource(okClient).FetchAsync(offer, Token);
        Assert.Equal("Line 1\nLine 2", notes);

        // HTTP error -> returns null silently
        using var errClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(await new HttpGitHubReleaseNotesSource(errClient).FetchAsync(offer, Token));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
