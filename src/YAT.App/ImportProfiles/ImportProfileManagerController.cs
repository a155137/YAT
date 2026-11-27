using YAT.Application.Abstractions.ImportProfiles;
using YAT.app.ViewModels;

namespace YAT.app.ImportProfiles;

public interface IImportProfileManagerPresenter
{
    Task ShowAsync(ImportProfileManagerViewModel viewModel);
}

// Opens the FT Import Profile Manager modal window, preventing duplicate windows and respecting busy-state guards.
public sealed class ImportProfileManagerController
{
    private readonly IImportProfileStore _store;
    private readonly IImportProfileSerializer _serializer;
    private readonly Func<IImportProfileManagerDialogs> _dialogsFactory;
    private readonly IImportProfileManagerPresenter _presenter;
    private readonly IImportProfileEditor _editor;
    private readonly Func<bool>? _canOpen;

    public ImportProfileManagerController(
        IImportProfileStore store,
        IImportProfileSerializer serializer,
        Func<IImportProfileManagerDialogs> dialogsFactory,
        IImportProfileManagerPresenter presenter,
        IImportProfileEditor? editor = null,
        Func<bool>? canOpen = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(dialogsFactory);
        ArgumentNullException.ThrowIfNull(presenter);
        _store = store;
        _serializer = serializer;
        _dialogsFactory = dialogsFactory;
        _presenter = presenter;
        _editor = editor ?? UnavailableImportProfileEditor.Instance;
        _canOpen = canOpen;
    }

    public bool IsOpen { get; private set; }

    public bool CanOpen => !IsOpen && (_canOpen?.Invoke() ?? true);

    public async Task OpenAsync()
    {
        if (!CanOpen)
        {
            return;
        }

        IsOpen = true;
        try
        {
            var viewModel = new ImportProfileManagerViewModel(_store, _serializer, _dialogsFactory(), _editor);
            await _presenter.ShowAsync(viewModel);
        }
        finally
        {
            IsOpen = false;
        }
    }
}
