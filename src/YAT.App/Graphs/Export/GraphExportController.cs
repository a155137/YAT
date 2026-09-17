using SkiaSharp;

namespace YAT.app.Graphs.Export;

// The File menu of a graph window: ask where to save, render the graph off screen, write the file.
//
// The snapshot it is given was taken on the UI thread; everything after that runs on a background thread and touches
// nothing but the snapshot, so exporting neither blocks the window nor notices it changing. A PowerPoint export embeds
// the PNG this controller just rendered - the graph is drawn once, for both.
public sealed class GraphExportController
{
    public const string PngExtension = "png";

    public const string PowerPointExtension = "pptx";

    private const string FailureMessage = "Unable to export the graph.";

    private readonly IGraphExportDialogs _dialogs;
    private readonly GraphExportService _service;
    private readonly IPowerPointGraphExporter _powerPoint;

    public GraphExportController(IGraphExportDialogs dialogs, GraphExportService service, IPowerPointGraphExporter powerPoint)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(powerPoint);
        _dialogs = dialogs;
        _service = service;
        _powerPoint = powerPoint;
    }

    // The last file an export wrote, kept for tests and debugging until graphs become documents.
    public string? LastExportedPath { get; private set; }

    public async Task ExportPngAsync(GraphExportSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = await _dialogs.PickPngAsync(ExportFileNames.Suggest(snapshot.Frame.Title, PngExtension));
        if (path is null)
        {
            // Cancelling a save dialog is not a failure: nothing happens.
            return;
        }

        await ExportAsync(path, () => _service.Write(path, _service.RenderPng(snapshot)), cancellationToken);
    }

    public async Task ExportPowerPointAsync(GraphExportSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var path = await _dialogs.PickPowerPointAsync(ExportFileNames.Suggest(snapshot.Frame.Title, PowerPointExtension));
        if (path is null)
        {
            return;
        }

        await ExportAsync(
            path,
            () =>
            {
                var png = _service.RenderPng(snapshot);
                _powerPoint.Save(path, new PowerPointSlideImage(
                    png,
                    GraphExportService.ExportWidth,
                    GraphExportService.ExportHeight,
                    Hex(snapshot.Theme.Background)));
            },
            cancellationToken);
    }

    // The graph background as the six hexadecimal digits a presentation writes colours in.
    private static string Hex(SKColor color) => PowerPointGraphExporter.Hex(color.Red, color.Green, color.Blue);

    // Runs one export off the UI thread. Whatever goes wrong - a locked file, a full disk, a refused folder - the
    // window says so and stays open; only the reasons that are safe to read are shown.
    private async Task ExportAsync(string path, Action export, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Run(export, cancellationToken);
            LastExportedPath = path;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await _dialogs.ShowErrorAsync(Message(exception));
        }
    }

    private static string Message(Exception exception) => exception switch
    {
        UnauthorizedAccessException => $"{FailureMessage} The file could not be written: access was denied.",
        IOException io => $"{FailureMessage} The file could not be written: {io.Message}",
        _ => FailureMessage
    };
}
