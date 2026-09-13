using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace YAT.app.Clipboard;

// Reads text from the clipboard of the given top-level window. Must be called on the UI thread.
internal sealed class AvaloniaClipboardTextReader : IClipboardTextReader
{
    private readonly TopLevel _topLevel;

    public AvaloniaClipboardTextReader(TopLevel topLevel)
    {
        _topLevel = topLevel;
    }

    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_topLevel.Clipboard is not { } clipboard)
        {
            return null;
        }

        // Avalonia's clipboard API takes no cancellation token; stop waiting for it when cancelled.
        return await clipboard.TryGetTextAsync().WaitAsync(cancellationToken);
    }
}
