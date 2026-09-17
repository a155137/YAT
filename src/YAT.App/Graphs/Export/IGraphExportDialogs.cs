namespace YAT.app.Graphs.Export;

// The user interaction an export needs: where to save, and what to say when it fails. The desktop implementation shows
// the platform's save dialog; tests use a fake.
public interface IGraphExportDialogs
{
    // Where to write the PNG, or null when the user cancels. The platform dialog asks about replacing an existing file.
    Task<string?> PickPngAsync(string suggestedFileName);

    // Where to write the presentation, or null when the user cancels.
    Task<string?> PickPowerPointAsync(string suggestedFileName);

    Task ShowErrorAsync(string message);
}
