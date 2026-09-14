using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
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
    private MainWindowViewModel? _viewModel;
    private IReadOnlyList<WorksheetGridColumn> _shownGridColumns = [];
    private readonly ColumnHeaderDragSelection _headerDrag = new();

    public MainWindow()
    {
        InitializeComponent();
        WorksheetGrid.AddHandler(PointerPressedEvent, OnWorksheetGridPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        WorksheetGrid.AddHandler(PointerMovedEvent, OnWorksheetGridPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        WorksheetGrid.AddHandler(PointerReleasedEvent, OnWorksheetGridPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        WorksheetGrid.AddHandler(PointerCaptureLostEvent, OnWorksheetGridPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    // Worksheet shortcuts use the bubbling KeyDown event rather than Window.KeyBindings: key bindings are matched before
    // the focused control sees the key, which would take Ctrl+V or Delete away from text boxes. A focused TextBox
    // handles its own keys first, so these only run when no control handled the gesture.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        if (this.GetPlatformSettings()?.HotkeyConfiguration.Paste.Any(gesture => gesture.Matches(e)) == true)
        {
            e.Handled = TryExecute(viewModel.PasteCommand);
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

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        RebuildGridColumns();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainWindowViewModel.GridColumns):
                RebuildGridColumns();
                break;
            case nameof(MainWindowViewModel.ActiveColumn):
            case nameof(MainWindowViewModel.SelectedColumns):
                UpdateHeaderSelection();
                break;
        }
    }

    // View-only mapping of the ViewModel's column descriptors to TableView columns: a row-number column, then one
    // read-only text column per worksheet column in Index order. Cells show the row's preformatted display text.
    private void RebuildGridColumns()
    {
        var gridColumns = _viewModel?.GridColumns ?? [];
        if (gridColumns.SequenceEqual(_shownGridColumns))
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

    // "Delete Column" / "Delete N Columns": the same command the Delete key runs. Bound to the ViewModel explicitly,
    // because header content does not inherit the window's DataContext (it sits outside the window's logical tree).
    private ContextMenu? CreateHeaderContextMenu()
    {
        if (_viewModel is null)
        {
            return null;
        }

        var deleteItem = new MenuItem { Command = _viewModel.DeleteSelectedColumnsCommand };
        deleteItem.Bind(MenuItem.HeaderProperty, new ReflectionBinding(nameof(MainWindowViewModel.DeleteSelectedColumnsMenuText)) { Source = _viewModel });

        var menu = new ContextMenu();
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
