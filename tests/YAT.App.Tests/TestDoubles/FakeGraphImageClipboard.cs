using YAT.app.Graphs.Export;

namespace YAT.App.Tests.TestDoubles;

// Records the images a graph window copies instead of touching the system clipboard. FailWith makes the next copy fail
// the way a busy or unavailable clipboard would.
internal sealed class FakeGraphImageClipboard : IGraphImageClipboard
{
    public List<byte[]> Copied { get; } = [];

    public Exception? FailWith { get; set; }

    public Task CopyPngAsync(byte[] png)
    {
        if (FailWith is { } failure)
        {
            return Task.FromException(failure);
        }

        Copied.Add(png);
        return Task.CompletedTask;
    }
}
