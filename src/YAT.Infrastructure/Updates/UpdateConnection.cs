using YAT.Application.Updates;

namespace YAT.Infrastructure.Updates;

// YAT's connection for updates (Task #051.B): the one HttpClient, the manifest source at the published address and the
// package downloader into the local update store - made once when the application starts and disposed when it exits.
public sealed class UpdateConnection : IDisposable
{
    public UpdateConnection(Uri manifestUrl, string userAgentVersion, string? updatesRoot = null, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(manifestUrl);
        Client = UpdateHttp.CreateClient(userAgentVersion, handler);
        Store = new UpdatePackageStore(updatesRoot ?? UpdatePackageStore.DefaultRoot);
        Source = new HttpUpdateManifestSource(Client, manifestUrl);
        Downloader = new HttpUpdatePackageDownloader(Client, Store);
    }

    public HttpClient Client { get; }

    public UpdatePackageStore Store { get; }

    public IUpdateManifestSource Source { get; }

    public IUpdatePackageDownloader Downloader { get; }

    public void Dispose() => Client.Dispose();
}
