using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.FtImport;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.app.FtImport;

public interface IFtImportWizardDialogs
{
    Task<IReadOnlyList<string>> PickDatalogFilesAsync();

    Task<string?> PickDatalogFolderAsync();
}

public interface IFtImportWizardPresenter
{
    Task<FtStreamingImportResult?> ShowAsync(FtImportWizardViewModel viewModel);
}

// Opens the FT Datalog Import Wizard modal dialog, prevents duplicate concurrent openings,
// and publishes the committed Worksheet to MainWindowViewModel on success.
public sealed class FtImportWizardController
{
    private readonly IImportProfileStore _profileStore;
    private readonly Func<IFtImportWizardDialogs> _dialogsFactory;
    private readonly IFtImportWizardPresenter _presenter;
    private readonly Func<MainWindowSession?> _sessionProvider;
    private readonly Func<MainWindowViewModel?> _projectProvider;
    private readonly Func<bool>? _canOpen;

    public FtImportWizardController(
        IImportProfileStore profileStore,
        Func<IFtImportWizardDialogs> dialogsFactory,
        IFtImportWizardPresenter presenter,
        Func<MainWindowSession?> sessionProvider,
        Func<MainWindowViewModel?> projectProvider,
        Func<bool>? canOpen = null)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(dialogsFactory);
        ArgumentNullException.ThrowIfNull(presenter);
        ArgumentNullException.ThrowIfNull(sessionProvider);
        ArgumentNullException.ThrowIfNull(projectProvider);

        _profileStore = profileStore;
        _dialogsFactory = dialogsFactory;
        _presenter = presenter;
        _sessionProvider = sessionProvider;
        _projectProvider = projectProvider;
        _canOpen = canOpen;
    }

    public bool IsOpen { get; private set; }

    public bool CanOpen =>
        !IsOpen
        && _sessionProvider() is not null
        && _projectProvider()?.CurrentProject is not null
        && (_canOpen?.Invoke() ?? true);

    public async Task<FtStreamingImportResult?> OpenAsync()
    {
        if (!CanOpen)
        {
            return null;
        }

        var session = _sessionProvider();
        var project = _projectProvider();
        if (session is null || project is null || project.CurrentProject is null)
        {
            return null;
        }

        IsOpen = true;
        try
        {
            var existingNames = project.Worksheets.Select(w => w.Name).ToArray();
            var viewModel = new FtImportWizardViewModel(
                _profileStore,
                _dialogsFactory(),
                session,
                existingNames);

            var result = await _presenter.ShowAsync(viewModel);
            if (result is { IsSuccess: true, Worksheet: not null } && ReferenceEquals(_projectProvider(), project))
            {
                await project.PublishImportedWorksheetAsync(result.Worksheet, result.Columns);
            }

            return result;
        }
        finally
        {
            IsOpen = false;
        }
    }
}
