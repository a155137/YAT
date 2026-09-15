using Avalonia.Controls;
using Avalonia.Platform.Storage;
using YAT.app.Lifecycle;

namespace YAT.app.Views;

// Project lifecycle dialogs for the desktop window: Avalonia storage-provider file pickers and the lifecycle message window.
public sealed class AvaloniaProjectLifecycleDialogs : IProjectLifecycleDialogs
{
    private static readonly FilePickerFileType ProjectFileType = new("YAT Project")
    {
        Patterns = ["*.yat"]
    };

    private readonly Window _owner;

    public AvaloniaProjectLifecycleDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public async Task<string?> PickProjectToOpenAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open Project",
            AllowMultiple = false,
            FileTypeFilter = [ProjectFileType]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    // The picker's own overwrite prompt is turned off: YAT never overwrites an existing file, so choosing one is reported
    // as an error instead of being confirmed as a replacement.
    public async Task<string?> PickSaveLocationAsync(string suggestedFileName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Project As",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "yat",
            FileTypeChoices = [ProjectFileType],
            ShowOverwritePrompt = false
        });

        return file?.TryGetLocalPath();
    }

    public Task<SaveChangesChoice> AskSaveChangesAsync(string projectName, bool closing) =>
        LifecycleMessageWindow.AskSaveChangesAsync(_owner, projectName, closing);

    public Task ShowErrorAsync(string message) => LifecycleMessageWindow.ShowErrorAsync(_owner, message);
}
