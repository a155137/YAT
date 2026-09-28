using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.app.Views;

public partial class MainWindow : Window
{
    // Window resource (MainWindow.axaml): cell theme for the cells of selected worksheet columns.
    private const string SelectedCellThemeKey = "SelectedWorksheetCellTheme";

    // Grid column → worksheet column id, for header clicks. Rebuilt with the columns.
    private readonly Dictionary<TableViewColumn, Guid> _gridColumnIds = [];
    private MainWindowShellViewModel? _shell;

    // The shown project's view model (the shell's Project).
    private MainWindowViewModel? _viewModel;

    // The grid columns currently built; null forces a rebuild (e.g. for another project with the same columns).
    private IReadOnlyList<WorksheetGridColumn>? _shownGridColumns = [];
    private readonly ColumnHeaderDragSelection _headerDrag = new();
    private bool _closeApproved;
    private bool _closeDecisionRunning;

    // The explorer column's width and minimum while the Project Explorer is hidden; null while it is shown.
    private (GridLength Width, double MinWidth)? _hiddenProjectExplorerColumn;

    public MainWindow()
    {
        InitializeComponent();
        WorksheetGrid.AddHandler(PointerPressedEvent, OnWorksheetGridPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        WorksheetGrid.AddHandler(PointerMovedEvent, OnWorksheetGridPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        WorksheetGrid.AddHandler(PointerReleasedEvent, OnWorksheetGridPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        WorksheetGrid.AddHandler(PointerCaptureLostEvent, OnWorksheetGridPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

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

        _shell = DataContext as MainWindowShellViewModel;
        if (_shell is not null)
        {
            _shell.PropertyChanged += OnShellPropertyChanged;
        }

        AttachProject(_shell?.Project);
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowShellViewModel.Project))
        {
            AttachProject(_shell?.Project);
        }
    }

    // Shows another project's view model: the grid columns, header menus and selection highlight are rebuilt from it, and
    // nothing of the previous project (drag state, header state) remains. The explorer column keeps its width.
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

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        _headerDrag.End();
        _shownGridColumns = null;
        RebuildGridColumns();
        ApplyProjectExplorerVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.IsProjectPanelVisible):
                ApplyProjectExplorerVisibility();
                break;
            case nameof(MainWindowViewModel.SelectedWorksheet):
                // A rename can change the worksheet title in the same layout pass that hides the error bar. Avalonia then
                // re-measures the title while resizing the workspace, but never re-arranges the header (its bounds did
                // not change), so the title kept its previous width and was clipped. Invalidating the header here
                // makes the layout pass arrange it again.
                WorksheetHeader.InvalidateMeasure();
                break;
            case nameof(MainWindowViewModel.GridColumns):
                RebuildGridColumns();
                break;
            case nameof(MainWindowViewModel.ActiveColumn):
            case nameof(MainWindowViewModel.SelectedColumns):
                UpdateHeaderSelection();
                break;
        }
    }

    // The explorer column keeps its (user-resized) width while shown. Hiding the explorer collapses the column, since a
    // hidden panel would otherwise leave that width empty; showing it again restores the width and its limits.
    private void ApplyProjectExplorerVisibility()
    {
        var column = WorkspaceGrid.ColumnDefinitions[0];
        var isVisible = _viewModel?.IsProjectPanelVisible ?? true;

        if (!isVisible && _hiddenProjectExplorerColumn is null)
        {
            _hiddenProjectExplorerColumn = (column.Width, column.MinWidth);
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
        else if (isVisible && _hiddenProjectExplorerColumn is { } shown)
        {
            _hiddenProjectExplorerColumn = null;
            column.Width = shown.Width;
            column.MinWidth = shown.MinWidth;
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

    // View-only mapping of the ViewModel's column descriptors to TableView columns: a row-number column, then one
    // read-only text column per worksheet column in Index order. Cells show the row's preformatted display text.
    private void RebuildGridColumns()
    {
        var gridColumns = _viewModel?.GridColumns ?? [];
        if (_shownGridColumns is not null && gridColumns.SequenceEqual(_shownGridColumns))
        {
            UpdateHeaderSelection();
            return;
        }

        _shownGridColumns = gridColumns;
        _gridColumnIds.Clear();
        WorksheetGrid.Columns.Clear();

        var rowNumberHeader = WorksheetGridLayout.CreateHeader(null);
        AddGridColumn(
            new TableViewColumn
            {
                Header = rowNumberHeader,
                Binding = new ReflectionBinding(nameof(WorksheetGridRow.RowNumber)),
                Width = new GridLength(WorksheetGridLayout.RowNumberColumnWidth),
                HorizontalContentAlignment = HorizontalAlignment.Right,
                CanUserResize = false
            },
            rowNumberHeader);

        for (var position = 0; position < gridColumns.Count; position++)
        {
            var gridColumn = gridColumns[position];
            var header = WorksheetGridLayout.CreateHeader(gridColumn);
            var column = new TableViewColumn
            {
                Header = header,
                Binding = new ReflectionBinding($"{nameof(WorksheetGridRow.Cells)}[{position}]"),
                Width = new GridLength(WorksheetGridLayout.GetColumnWidth(gridColumn)),
                HorizontalContentAlignment = WorksheetGridLayout.GetCellAlignment(gridColumn.DataType)
            };

            header.ContextMenu = CreateHeaderContextMenu();
            _gridColumnIds[column] = gridColumn.ColumnId;
            AddGridColumn(column, header);
        }

        UpdateHeaderSelection();
    }

    // "Copy Column" / "Copy N Columns" and "Delete Column" / "Delete N Columns": the same commands Ctrl+C and Delete run.
    // Bound to the ViewModel explicitly, because header content does not inherit the window's DataContext (it sits
    // outside the window's logical tree).
    private ContextMenu? CreateHeaderContextMenu()
    {
        if (_viewModel is null)
        {
            return null;
        }

        var copyItem = new MenuItem { Command = _viewModel.CopySelectedColumnsCommand };
        copyItem.Bind(MenuItem.HeaderProperty, new ReflectionBinding(nameof(MainWindowViewModel.CopySelectedColumnsMenuText)) { Source = _viewModel });

        var deleteItem = new MenuItem { Command = _viewModel.DeleteSelectedColumnsCommand };
        deleteItem.Bind(MenuItem.HeaderProperty, new ReflectionBinding(nameof(MainWindowViewModel.DeleteSelectedColumnsMenuText)) { Source = _viewModel });

        var menu = new ContextMenu();
        menu.Items.Add(copyItem);
        menu.Items.Add(deleteItem);
        return menu;
    }

    // The column's content alignment applies to its header as well, so the header element cannot stretch by itself;
    // it follows the column's actual width instead, keeping its background and underline across the full header.
    private void AddGridColumn(TableViewColumn column, Border header)
    {
        header.Bind(WidthProperty, column.GetObservable(TableViewColumn.ActualWidthProperty));
        WorksheetGrid.Columns.Add(column);
    }

    // Marks each column as normal, selected, or active (selected and the Ctrl+V start): its header and its visible cells.
    private void UpdateHeaderSelection()
    {
        var selectedCellTheme = this.TryFindResource(SelectedCellThemeKey, ActualThemeVariant, out var theme) ? theme as ControlTheme : null;
        WorksheetGridLayout.ApplyColumnStates(
            _gridColumnIds,
            _viewModel?.ActiveColumn?.Id,
            _viewModel?.SelectedColumns.Select(column => column.Id) ?? [],
            selectedCellTheme);
    }

    // Header presses:
    // - right button: prepares the selection before the context menu opens (on release) — an unselected column
    //   becomes the only selection, a selected column keeps the whole selection;
    // - plain left button (not on a resize grip): selects that column and starts a drag selection anchored on it.
    // Ctrl/Cmd and Shift clicks are applied on release.
    private void OnWorksheetGridPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(WorksheetGrid).Properties;
        var columnId = GetHeaderColumnId(e.Source as Visual);

        if (properties.IsRightButtonPressed)
        {
            _headerDrag.End();
            if (columnId is { } rightClicked && !IsOnResizeGrip(e.Source as Visual))
            {
                _viewModel!.SelectColumnForContextMenu(rightClicked);
            }

            return;
        }

        if (_headerDrag.TryStart(columnId, properties.IsLeftButtonPressed, IsOnResizeGrip(e.Source as Visual), e.KeyModifiers))
        {
            _viewModel!.SelectColumn(_headerDrag.AnchorColumnId!.Value);
        }
    }

    // During a header drag, the selection follows the header under the pointer (live, in either direction).
    private void OnWorksheetGridPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_headerDrag.IsDragging)
        {
            return;
        }

        if (!e.GetCurrentPoint(WorksheetGrid).Properties.IsLeftButtonPressed)
        {
            _headerDrag.End();
            return;
        }

        var underPointer = WorksheetGrid.InputHitTest(e.GetPosition(WorksheetGrid)) as Visual;
        if (_headerDrag.TryMoveTo(GetHeaderColumnId(underPointer)))
        {
            _viewModel!.SelectColumnRange(_headerDrag.AnchorColumnId!.Value, _headerDrag.CurrentColumnId!.Value);
        }
    }

    // Left release: ends a drag (its selection is already applied), or applies a Ctrl/Cmd (toggle) or Shift (range)
    // click on a header. Presses on a resize grip never select.
    private void OnWorksheetGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_headerDrag.IsDragging)
        {
            _headerDrag.End();
            return;
        }

        if (IsOnResizeGrip(e.Source as Visual) || GetHeaderColumnId(e.Source as Visual) is not { } columnId)
        {
            return;
        }

        switch (WorksheetKeyRouting.GetHeaderClick(e.KeyModifiers))
        {
            case ColumnHeaderClick.Toggle:
                _viewModel!.ToggleColumnSelection(columnId);
                break;
            case ColumnHeaderClick.Extend:
                _viewModel!.ExtendColumnSelection(columnId);
                break;
        }
    }

    private void OnWorksheetGridPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e) => _headerDrag.End();

    // The worksheet column whose header contains the visual, if any (including its resize grip).
    private Guid? GetHeaderColumnId(Visual? visual) =>
        _viewModel is not null
        && visual?.FindAncestorOfType<TableViewColumnHeader>(includeSelf: true)?.Column is { } column
        && _gridColumnIds.TryGetValue(column, out var columnId)
            ? columnId
            : null;

    private static bool IsOnResizeGrip(Visual? visual) => visual?.FindAncestorOfType<Thumb>(includeSelf: true) is not null;
}
