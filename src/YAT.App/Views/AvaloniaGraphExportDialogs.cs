using Avalonia.Controls;
using Avalonia.Platform.Storage;
using YAT.app.Graphs.Export;

namespace YAT.app.Views;

// Export dialogs for a graph window: the platform save dialog and the shared message window.
//
// Unlike saving a project - where YAT never replaces an existing .yat file - an export follows the platform's own save
// semantics: the dialog asks before replacing a file, and the user's answer decides.
public sealed class AvaloniaGraphExportDialogs : IGraphExportDialogs
{
    private static readonly FilePickerFileType PngFileType = new("PNG Image")
    {
        Patterns = ["*.png"]
    };

    private static readonly FilePickerFileType PowerPointFileType = new("PowerPoint Presentation")
    {
        Patterns = ["*.pptx"]
    };

    private readonly Window _owner;

    public AvaloniaGraphExportDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<string?> PickPngAsync(string suggestedFileName) =>
        PickAsync("Export PNG", suggestedFileName, GraphExportController.PngExtension, PngFileType);

    public Task<string?> PickPowerPointAsync(string suggestedFileName) =>
        PickAsync("Export PowerPoint", suggestedFileName, GraphExportController.PowerPointExtension, PowerPointFileType);

    public Task ShowErrorAsync(string message) => LifecycleMessageWindow.ShowErrorAsync(_owner, message);

    private async Task<string?> PickAsync(string title, string suggestedFileName, string extension, FilePickerFileType fileType)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedFileName,
            DefaultExtension = extension,
            FileTypeChoices = [fileType]
        });

        return file?.TryGetLocalPath();
    }
}
