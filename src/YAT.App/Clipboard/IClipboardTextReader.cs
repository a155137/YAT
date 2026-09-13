namespace YAT.app.Clipboard;

// App-layer view of the system clipboard. Keeps UI framework clipboard APIs out of sessions and Application code.
public interface IClipboardTextReader
{
    // Returns the clipboard text, or null when the clipboard holds no text.
    Task<string?> ReadTextAsync(CancellationToken cancellationToken);
}
