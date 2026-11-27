using Avalonia.Controls;
using Avalonia.Platform.Storage;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;

namespace YAT.app.Views;

internal sealed class AvaloniaImportProfileManagerDialogs : IImportProfileManagerDialogs
{
    private static readonly FilePickerFileType JsonProfileFileType = new("FT Import Profile (JSON)")
    {
        Patterns = ["*.json"]
    };

    private readonly Window _owner;

    public AvaloniaImportProfileManagerDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<bool> ConfirmDeleteAsync(ImportProfile profile) =>
        LifecycleMessageWindow.ConfirmAsync(
            _owner,
            $"Delete “{profile.Name}”?",
            $"This will permanently delete the profile file ({profile.Id:D}.json) from UserData/ImportProfiles.",
            "Delete");

    public async Task<string?> PickImportFileAsync()
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import FT Profile JSON",
            AllowMultiple = false,
            FileTypeFilter = [JsonProfileFileType]
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickExportFileAsync(string suggestedFileName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export FT Profile JSON",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [JsonProfileFileType],
            ShowOverwritePrompt = false
        });

        return file?.TryGetLocalPath();
    }

    public Task<bool> ConfirmOverwriteExportAsync(string destinationPath) =>
        LifecycleMessageWindow.ConfirmAsync(
            _owner,
            $"Replace “{Path.GetFileName(destinationPath)}”?",
            $"“{destinationPath}” already exists. Replacing it will overwrite its contents.",
            "Replace");

    public async Task<ImportDuplicateIdChoice> AskDuplicateIdAsync(ImportProfile importedProfile, ImportProfile existingProfile)
    {
        var confirmed = await LifecycleMessageWindow.ConfirmAsync(
            _owner,
            $"Profile ID already exists",
            $"The imported file has the same ID ({importedProfile.Id:D}) as existing profile “{existingProfile.Name}”. Import it as a separate copy with a new ID?",
            "Import as Copy");

        return confirmed ? ImportDuplicateIdChoice.ImportAsCopy : ImportDuplicateIdChoice.Cancel;
    }

    public Task ShowErrorAsync(string message, string? detail = null) =>
        LifecycleMessageWindow.ShowErrorAsync(
            _owner,
            string.IsNullOrWhiteSpace(detail) ? message : $"{message}{Environment.NewLine}{Environment.NewLine}{detail}");
}

internal sealed class AvaloniaImportProfileManagerPresenter : IImportProfileManagerPresenter
{
    private readonly Window _owner;

    public AvaloniaImportProfileManagerPresenter(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    internal Window? ActiveWindow { get; private set; }

    public async Task ShowAsync(ViewModels.ImportProfileManagerViewModel viewModel)
    {
        var window = new ImportProfileManagerWindow(viewModel);
        ActiveWindow = window;
        try
        {
            await window.ShowDialog(_owner);
        }
        finally
        {
            ActiveWindow = null;
        }
    }
}

internal sealed class AvaloniaImportProfileEditor : IImportProfileEditor
{
    private readonly Func<Window> _ownerProvider;

    public AvaloniaImportProfileEditor(Func<Window> ownerProvider)
    {
        ArgumentNullException.ThrowIfNull(ownerProvider);
        _ownerProvider = ownerProvider;
    }

    public bool IsAvailable => true;

    public Task<ImportProfile?> EditAsync(ImportProfileEditorRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var vm = new ViewModels.ImportProfileBuilderViewModel(request);
        return ImportProfileBuilderWindow.ShowAsync(_ownerProvider(), vm);
    }
}
