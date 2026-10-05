using SkiaSharp;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs.Export;

// Draws a graph again, off screen, and encodes it as a PNG.
//
// Nothing here captures the screen: the graph is rendered into a Skia surface of its own with the same renderers the
// window uses, so the export does not depend on the window's size, its scaling, or on whether it is visible, covered or
// minimised at all. A graph exported from a window shrunk to its minimum is the same image as one exported full screen.
//
// Producing the bytes is separate from writing them, so a PowerPoint export embeds exactly the same image a PNG export
// would have written, without a temporary file.
public sealed class GraphExportService
{
    // The export canvas, fixed for V1: large enough for a report or a slide, and the same on every machine because it
    // is measured in pixels rather than in the window's layout units.
    public const int ExportWidth = 1600;

    public const int ExportHeight = 1000;

    // The image is drawn at twice the size it is laid out at, the way a high-DPI screen draws a window: the graph keeps
    // the proportions it has on screen - the same text next to the same plot area - and every line and glyph is drawn
    // with twice the pixels. Exporting at 1600x1000 layout units instead would give a wider graph with text so small
    // relative to it that a slide could not be read.
    public const float ExportScale = 2f;

    public byte[] RenderPng(GraphExportSnapshot snapshot, int width = ExportWidth, int height = ExportHeight)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul))
            ?? throw new InvalidOperationException($"A drawing surface of {width}x{height} could not be created.");

        // The renderer paints the graph background itself; clearing first leaves nothing of the surface undefined, so
        // the PNG is fully opaque wherever the graph is.
        surface.Canvas.Clear(snapshot.Theme.Background);
        surface.Canvas.Scale(ExportScale);
        // The one way the graph is drawn, on screen and here (GraphDrawing): a graph in panels with every panel.
        GraphDrawing.Render(
            new SkiaGraphRenderer(),
            surface.Canvas,
            snapshot.Frame,
            snapshot.Plot,
            snapshot.Panels,
            new SKRect(0, 0, width / ExportScale, height / ExportScale),
            snapshot.Theme);
        surface.Canvas.Flush();

        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("The graph image could not be encoded as a PNG.");

        return encoded.ToArray();
    }

    // The bytes, written where the user chose them. Kept apart from rendering so the same bytes can go into a
    // presentation instead of a file.
    public void Write(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(bytes);
        File.WriteAllBytes(path, bytes);
    }
}
