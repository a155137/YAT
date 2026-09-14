using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace YAT.app.Clipboard;

// Reads and writes text on the clipboard of the given top-level window. Must be called on the UI thread.
internal sealed class AvaloniaClipboard : IClipboardTextReader, IClipboardTextWriter
{
    private readonly TopLevel _topLevel;

    public AvaloniaClipboard(TopLevel topLevel)
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

    public async Task WriteTextAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();

        if (_topLevel.Clipboard is not { } clipboard)
        {
            return;
        }

        await clipboard.SetTextAsync(text);

        // Keep the text on the system clipboard after YAT exits, so it can still be pasted elsewhere.
        await clipboard.FlushAsync();
    }
}
