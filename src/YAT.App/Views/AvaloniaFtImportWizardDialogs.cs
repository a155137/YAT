using Avalonia.Controls;
using Avalonia.Platform.Storage;
using YAT.Application.FtImport;
using YAT.app.FtImport;
using YAT.app.ViewModels;

namespace YAT.app.Views;

internal sealed class AvaloniaFtImportWizardDialogs : IFtImportWizardDialogs
{
    private static readonly FilePickerFileType DatalogFileType = new("FT Datalog Files")
    {
        Patterns = ["*.csv", "*.txt", "*.tsv", "*.dat", "*.zip", "*.Reg.zip"]
    };

    private readonly Func<Window> _ownerProvider;

    public AvaloniaFtImportWizardDialogs(Func<Window> ownerProvider)
    {
        ArgumentNullException.ThrowIfNull(ownerProvider);
        _ownerProvider = ownerProvider;
    }

    public async Task<IReadOnlyList<string>> PickDatalogFilesAsync()
    {
        var owner = _ownerProvider();
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select FT Datalog Files",
            AllowMultiple = true,
            FileTypeFilter = [DatalogFileType, FilePickerFileTypes.All]
        });

        var paths = new List<string>(files.Count);
        foreach (var file in files)
        {
            if (file.TryGetLocalPath() is { } localPath && !string.IsNullOrWhiteSpace(localPath))
            {
                paths.Add(localPath);
            }
        }

        return paths;
    }

    public async Task<string?> PickDatalogFolderAsync()
    {
        var owner = _ownerProvider();
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select FT Datalog Folder (Non-Recursive)",
            AllowMultiple = false
        });

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}

internal sealed class AvaloniaFtImportWizardPresenter : IFtImportWizardPresenter
{
    private readonly Window _owner;

    public AvaloniaFtImportWizardPresenter(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    internal Window? ActiveWindow { get; private set; }

    public async Task<FtStreamingImportResult?> ShowAsync(FtImportWizardViewModel viewModel)
    {
        var window = new FtImportWizardWindow(viewModel);
        ActiveWindow = window;
        try
        {
            return await window.ShowDialog<FtStreamingImportResult?>(_owner);
        }
        finally
        {
            ActiveWindow = null;
        }
    }
}
