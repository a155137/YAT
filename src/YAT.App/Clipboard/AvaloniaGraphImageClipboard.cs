using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using YAT.app.Graphs.Export;

namespace YAT.app.Clipboard;

// Puts a graph image on the clipboard of the given top-level window. Must be called on the UI thread.
//
// The image goes on in Avalonia's bitmap format, decoded from the PNG it was given - the same pixels, not drawn again.
// On Windows the platform publishes that one bitmap in every form applications paste from: PNG (which Office prefers,
// lossless and with alpha) as well as the device independent bitmaps (CF_DIB, CF_DIBV5, CF_BITMAP) that Paint, Outlook
// and older applications read. Adding the PNG bytes as a format of our own would only register "PNG" twice.
//
// Everything is in memory; no file is written. The clipboard is then flushed, so the image stays on the system
// clipboard after the graph window, or YAT itself, has closed.
internal sealed class AvaloniaGraphImageClipboard : IGraphImageClipboard
{
    private readonly TopLevel _topLevel;

    public AvaloniaGraphImageClipboard(TopLevel topLevel)
    {
        ArgumentNullException.ThrowIfNull(topLevel);
        _topLevel = topLevel;
    }

    public async Task CopyPngAsync(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);

        var clipboard = _topLevel.Clipboard
            ?? throw new InvalidOperationException("The clipboard is not available.");

        // The bitmap is not disposed here: the clipboard may still read it until the flush below has handed everything
        // to the system.
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new Bitmap(stream);

        var item = new DataTransferItem();
        item.SetBitmap(bitmap);

        var transfer = new DataTransfer();
        transfer.Add(item);

        await clipboard.SetDataAsync(transfer);
        await clipboard.FlushAsync();
    }
}
