using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.app.Views;

public partial class MainWindow : Window
{
    private MainWindowShellViewModel? _shell;

    // The shown project's view model (the shell's Project).
    private MainWindowViewModel? _viewModel;

    private bool _closeApproved;
    private bool _closeDecisionRunning;

    // The explorer column's width and minimum while the Project Explorer is hidden; null while it is shown.
    private (GridLength Width, double MinWidth)? _hiddenLeftColumn;
    private GridLength? _hiddenExplorerRow;
    private GridLength? _hiddenGraphsRow;
    private OpenGraphsViewModel? _openGraphs;
    private WorksheetViewportDataCoordinator? _viewportCoordinator;
    private WorksheetViewportSessionBridge? _viewportBridge;

    public MainWindow()
    {
        InitializeComponent();

        // The list marks Enter handled as it selects; the Graphs list (Task #061) still takes it to bring the graph back.
        GraphsList.AddHandler(KeyDownEvent, OnGraphsListKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    internal WorksheetViewportDataCoordinator? ViewportCoordinator => _viewportCoordinator;

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    private async void OnAboutClick(object? sender, RoutedEventArgs e) => await AboutWindow.ShowAsync(this);

    // Worksheet shortcuts use the bubbling KeyDown event rather than Window.KeyBindings: key bindings are matched before
    // the focused control sees the key, which would take Ctrl+V or Delete away from text boxes. A focused TextBox
    // handles its own keys first, so these only run when no control handled the gesture.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || _viewModel is not { } viewModel)
        {
            return;
        }

        var hotkeys = this.GetPlatformSettings()?.HotkeyConfiguration;
        if (hotkeys?.Paste.Any(gesture => gesture.Matches(e)) == true)
        {
            e.Handled = TryExecute(viewModel.PasteCommand);
        }
        else if (WorksheetKeyRouting.IsCopyColumnsGesture(hotkeys?.Copy.Any(gesture => gesture.Matches(e)) == true, e.Handled, e.Source))
        {
            e.Handled = TryExecute(viewModel.CopySelectedColumnsCommand);
        }
        else if (WorksheetKeyRouting.IsDeleteColumnsGesture(e.Key, e.KeyModifiers, e.Handled, e.Source))
        {
            e.Handled = TryExecute(viewModel.DeleteSelectedColumnsCommand);
        }
    }

    private static bool TryExecute(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    // Close requests (File > Exit, title bar, Alt+F4) all run the same lifecycle decision. The first request is cancelled
    // while the asynchronous decision (save prompt, Save As) runs; if closing is approved, the window closes again with
    // the approval set, so the decision runs only once and never loops.
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);

        if (_closeApproved || _shell is null)
        {
            return;
        }

        e.Cancel = true;
        if (!_closeDecisionRunning)
        {
            _ = DecideCloseAsync(_shell);
        }
    }

    // Install Update (Task #051.C): the same close decision as any close request - the save prompt included. When it lets
    // YAT close, the project is closed and the window will close without asking again.
    internal async Task<bool> CloseForUpdateAsync()
    {
        if (_shell is null || _closeDecisionRunning || _closeApproved)
        {
            return _closeApproved;
        }

        _closeDecisionRunning = true;
        try
        {
            if (!await _shell.RequestCloseAsync())
            {
                return false;
            }

            _closeApproved = true;
            return true;
        }
        finally
        {
            _closeDecisionRunning = false;
        }
    }

    private async Task DecideCloseAsync(MainWindowShellViewModel shell)
    {
        _closeDecisionRunning = true;
        try
        {
            if (await shell.RequestCloseAsync())
            {
                _closeApproved = true;
                Close();
            }
        }
        finally
        {
            _closeDecisionRunning = false;
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_shell is not null)
        {
            _shell.PropertyChanged -= OnShellPropertyChanged;
        }

        if (_openGraphs is not null)
        {
            _openGraphs.PropertyChanged -= OnOpenGraphsPropertyChanged;
        }

        _shell = DataContext as MainWindowShellViewModel;
        _openGraphs = _shell?.OpenGraphs;
        if (_shell is not null)
        {
            _shell.UseMetadataOnlyGridLoad = true;
            _shell.PropertyChanged += OnShellPropertyChanged;
        }

        if (_openGraphs is not null)
        {
            _openGraphs.PropertyChanged += OnOpenGraphsPropertyChanged;
        }

        AttachProject(_shell?.Project);
        ApplyLeftPanelVisibility();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowShellViewModel.Project))
        {
            AttachProject(_shell?.Project);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        DetachViewportServices();
        VirtualizedWorksheetGrid.Dispose();
        base.OnClosed(e);
    }

    private void DetachViewportServices()
    {
        if (_viewportCoordinator is not null)
        {
            _viewportCoordinator.ViewportDataChanged -= OnViewportDataChanged;
        }

        _viewportBridge?.Dispose();
        _viewportBridge = null;
        _viewportCoordinator?.Dispose();
        _viewportCoordinator = null;
        VirtualizedWorksheetGrid.Attach(null, null);
    }

    private void OnViewportDataChanged(object? sender, EventArgs e)
    {
        _viewModel?.ReportViewportError(_viewportCoordinator?.LastErrorMessage);
    }

    // Shows another project's view model: the virtualized worksheet coordinator and bridge are rebuilt for it.
    private void AttachProject(MainWindowViewModel? viewModel)
    {
        if (ReferenceEquals(viewModel, _viewModel))
        {
            return;
        }

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        DetachViewportServices();

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            _viewModel.UseMetadataOnlyGridLoad = true;
            _viewportCoordinator = new WorksheetViewportDataCoordinator(_viewModel.Session);
            _viewportCoordinator.ViewportDataChanged += OnViewportDataChanged;
            _viewportBridge = new WorksheetViewportSessionBridge(_viewModel, _viewModel.Session, _viewportCoordinator);
            VirtualizedWorksheetGrid.Attach(_viewModel, _viewportCoordinator);
        }

        ApplyLeftPanelVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsProjectPanelVisible):
                ApplyLeftPanelVisibility();
                break;
            case nameof(MainWindowViewModel.SelectedWorksheet):
                // A rename can change the worksheet title in the same layout pass that hides the error bar. Avalonia then
                // re-measures the title while resizing the workspace, but never re-arranges the header (its bounds did
                // not change), so the title kept its previous width and was clipped. Invalidating the header here
                // makes the layout pass arrange it again.
                WorksheetHeader.InvalidateMeasure();
                break;
        }
    }

    private void OnOpenGraphsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OpenGraphsViewModel.IsPanelVisible))
        {
            ApplyLeftPanelVisibility();
        }
        else if (e.PropertyName == nameof(OpenGraphsViewModel.ActiveItem) && _openGraphs?.ActiveItem is { } active)
        {
            // The selection follows the graph last activated; selecting never activates anything (only a click or Enter).
            GraphsList.SelectedItem = active;
        }
    }

    // The left column holds the Project Explorer over the Graphs list (Task #061), each shown or hidden on its own. A
    // hidden one gives its row to the other (and the splitter between them goes), keeping its own (user-resized) height
    // for when it is shown again. Only with both hidden does the column collapse, since it would otherwise leave its width
    // empty; showing either again restores the width and its limits.
    private void ApplyLeftPanelVisibility()
    {
        var explorer = _viewModel?.IsProjectPanelVisible ?? true;
        var graphs = _openGraphs?.IsPanelVisible ?? true;

        _hiddenExplorerRow = Collapse(WorkspaceGrid.RowDefinitions[0], explorer, _hiddenExplorerRow);
        _hiddenGraphsRow = Collapse(WorkspaceGrid.RowDefinitions[2], graphs, _hiddenGraphsRow);
        LeftPanelSplitter.IsVisible = explorer && graphs;
        WorkspaceSplitter.IsVisible = explorer || graphs;
        ApplyLeftColumnVisibility(explorer || graphs);
    }

    // A row of the left column hidden (height 0, its height kept) or shown again (its height back); what is kept now.
    private static GridLength? Collapse(RowDefinition row, bool isVisible, GridLength? hidden)
    {
        if (!isVisible && hidden is null)
        {
            var height = row.Height;
            row.Height = new GridLength(0);
            return height;
        }

        if (isVisible && hidden is { } shown)
        {
            row.Height = shown;
            return null;
        }

        return hidden;
    }

    private void ApplyLeftColumnVisibility(bool isVisible)
    {
        var column = WorkspaceGrid.ColumnDefinitions[0];

        if (!isVisible && _hiddenLeftColumn is null)
        {
            _hiddenLeftColumn = (column.Width, column.MinWidth);
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
        else if (isVisible && _hiddenLeftColumn is { } shown)
        {
            _hiddenLeftColumn = null;
            column.Width = shown.Width;
            column.MinWidth = shown.MinWidth;
        }
    }

    // A click on a graph in the Graphs list brings its window back (Task #061); so does Enter on the selected one.
    private void OnGraphsListTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source
            && source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: OpenGraphItem item })
        {
            _openGraphs?.BringToFrontCommand.Execute(item);
        }
    }

    private void OnGraphsListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && GraphsList.SelectedItem is OpenGraphItem item)
        {
            e.Handled = true;
            _openGraphs?.BringToFrontCommand.Execute(item);
        }
    }

    // Double-click on a Project Explorer name starts inline rename. Only the name text starts it: the event is handled
    // here so the tree item does not also expand or collapse, while the expander chevron keeps its own behavior.
    private void OnExplorerNameDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_viewModel is null || sender is not Control { DataContext: ProjectExplorerItem item, Parent: Panel panel })
        {
            return;
        }

        e.Handled = true;
        _viewModel.ProjectExplorer.BeginRename(item);

        if (panel.Children.OfType<TextBox>().FirstOrDefault() is { } editor)
        {
            // The editor becomes visible on the next layout pass; focus it once it can take focus.
            Dispatcher.UIThread.Post(
                () =>
                {
                    editor.Focus();
                    editor.SelectAll();
                },
                DispatcherPriority.Loaded);
        }
    }

    // Enter commits (a rejected name stays in edit mode), Esc cancels. Up/Down are kept from the tree while editing, so
    // they cannot move the tree selection to another item.
    private async void OnExplorerEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || sender is not TextBox { DataContext: ProjectExplorerItem item } editor)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                if (await _viewModel.ProjectExplorer.CommitRenameAsync(item) || !item.IsEditing)
                {
                    editor.FindAncestorOfType<TreeViewItem>()?.Focus();
                }

                break;
            case Key.Escape:
                e.Handled = true;
                _viewModel.ProjectExplorer.CancelRename(item);
                editor.FindAncestorOfType<TreeViewItem>()?.Focus();
                break;
            case Key.Up:
            case Key.Down:
                e.Handled = true;
                break;
        }
    }

    // Focus loss commits; a rejected name ends editing and restores the current name (the error stays shown).
    private async void OnExplorerEditorLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is TextBox { DataContext: ProjectExplorerItem item })
        {
            await _viewModel.ProjectExplorer.CommitOrCancelRenameAsync(item);
        }
    }
}
