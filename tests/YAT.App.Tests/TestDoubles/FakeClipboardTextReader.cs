using YAT.app.Clipboard;

namespace YAT.App.Tests.TestDoubles;

internal sealed class FakeClipboardTextReader : IClipboardTextReader
{
    public string? Text { get; set; }

    public int ReadCount { get; private set; }

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
}
