namespace YAT.app.Clipboard;

// App-layer view of writing text to the system clipboard. Keeps UI framework clipboard APIs out of sessions.
public interface IClipboardTextWriter
{
    // Replaces the clipboard content with the text.
    Task WriteTextAsync(string text, CancellationToken cancellationToken);
}
