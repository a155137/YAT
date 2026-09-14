using YAT.app.Clipboard;

namespace YAT.App.Tests.TestDoubles;

internal sealed class FakeClipboard : IClipboardTextReader, IClipboardTextWriter
{
    // What a read returns; a write replaces it, as the system clipboard does.
    public string? Text { get; set; }

    public int ReadCount { get; private set; }

    public List<string> Writes { get; } = [];

    // When set, a read waits for this task (or for cancellation) before returning Text.
    public Task? Gate { get; set; }

    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken)
    {
        ReadCount++;

        if (Gate is not null)
        {
            await Gate.WaitAsync(cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Text;
    }

    public Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add(text);
        Text = text;
        return Task.CompletedTask;
    }
}
