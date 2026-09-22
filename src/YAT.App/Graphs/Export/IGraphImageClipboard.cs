namespace YAT.app.Graphs.Export;

// Where Copy Image puts a graph: the system clipboard, given the PNG an export would have written. It is handed an
// image, never a graph, so the clipboard can neither draw nor redraw anything - the picture pasted elsewhere is the
// picture a PNG export writes. The desktop implementation talks to the platform clipboard; tests use a fake.
public interface IGraphImageClipboard
{
    // Replaces the clipboard content with this image. The bytes are owned by the clipboard from then on: what is pasted
    // later does not depend on the window, the renderer or anything else the graph was drawn with.
    Task CopyPngAsync(byte[] png);
}
