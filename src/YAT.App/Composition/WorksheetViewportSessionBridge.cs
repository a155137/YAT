using System.ComponentModel;
using YAT.app.ViewModels;

namespace YAT.app.Composition;

// Isolated bridge connecting MainWindowViewModel + MainWindowSession lifecycle events
// to WorksheetViewportDataCoordinator without modifying MainWindowViewModel or MainWindow.
internal sealed class WorksheetViewportSessionBridge : IDisposable
{
    private readonly MainWindowViewModel _viewModel;
    private readonly MainWindowSession _session;
    private readonly WorksheetViewportDataCoordinator _coordinator;
    private Guid? _lastWorksheetId;
    private long _lastRowOffset;
    private bool _disposed;

    public WorksheetViewportSessionBridge(
        MainWindowViewModel viewModel,
        MainWindowSession session,
        WorksheetViewportDataCoordinator coordinator)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _session.ProjectModified += OnProjectModified;

        SyncFromViewModel();
    }

    public void SyncFromViewModel()
    {
        if (_disposed)
        {
            return;
        }

        var wsId = _viewModel.SelectedWorksheet?.Id;
        var rowOffset = _viewModel.GridRowOffset;

        if (wsId is null)
        {
            _lastWorksheetId = null;
            _lastRowOffset = 0;
            _coordinator.Clear();
            return;
        }

        if (_lastWorksheetId != wsId)
        {
            _lastWorksheetId = wsId;
            _lastRowOffset = rowOffset;
            _coordinator.SetWorksheet(wsId.Value, rowOffset);
        }
        else if (_lastRowOffset != rowOffset)
        {
            _lastRowOffset = rowOffset;
            _coordinator.SetPage(rowOffset);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.SelectedWorksheet) or nameof(MainWindowViewModel.GridRowOffset))
        {
            SyncFromViewModel();
        }
    }

    private void OnProjectModified(object? sender, EventArgs e)
    {
        if (!_disposed && _lastWorksheetId is not null)
        {
            _coordinator.InvalidateData();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _session.ProjectModified -= OnProjectModified;
    }
}
