using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.app.Views;

public partial class MainWindow : Window
{
    private const double RowNumberColumnWidth = 56;
    private const double DataColumnWidth = 110;

    // Grid column → worksheet column id, for header clicks. Rebuilt with the columns.
    private readonly Dictionary<TableViewColumn, Guid> _gridColumnIds = [];
    private MainWindowViewModel? _viewModel;
    private IReadOnlyList<WorksheetGridColumn> _shownGridColumns = [];

    public MainWindow()
    {
        InitializeComponent();
        WorksheetGrid.AddHandler(PointerReleasedEvent, OnWorksheetGridPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    // Worksheet paste uses the bubbling KeyDown event rather than Window.KeyBindings: key bindings are matched before
    // the focused control sees the key, which would take Ctrl+V away from text boxes. A focused TextBox handles its
    // own paste first, so this only runs when no control handled the gesture.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled
            || DataContext is not MainWindowViewModel viewModel
            || this.GetPlatformSettings()?.HotkeyConfiguration.Paste.Any(gesture => gesture.Matches(e)) != true)
        {
            return;
        }

        if (viewModel.PasteCommand.CanExecute(null))
        {
            viewModel.PasteCommand.Execute(null);
            e.Handled = true;
        }
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
            case nameof(MainWindowViewModel.SelectedColumn):
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

        WorksheetGrid.Columns.Add(new TableViewColumn
        {
            Header = string.Empty,
            Binding = new ReflectionBinding(nameof(WorksheetGridRow.RowNumber)),
            Width = new GridLength(RowNumberColumnWidth),
            CanUserResize = false
        });

        for (var position = 0; position < gridColumns.Count; position++)
        {
            var column = new TableViewColumn
            {
                Header = new TextBlock { Text = gridColumns[position].Name },
                Binding = new ReflectionBinding($"{nameof(WorksheetGridRow.Cells)}[{position}]"),
                Width = new GridLength(DataColumnWidth)
            };

            _gridColumnIds[column] = gridColumns[position].ColumnId;
            WorksheetGrid.Columns.Add(column);
        }

        UpdateHeaderSelection();
    }

    // The selected paste-target column has a bold, underlined header.
    private void UpdateHeaderSelection()
    {
        var selectedId = _viewModel?.SelectedColumn?.Id;
        foreach (var (column, columnId) in _gridColumnIds)
        {
            if (column.Header is TextBlock header)
            {
                var isSelected = columnId == selectedId;
                header.FontWeight = isSelected ? FontWeight.Bold : FontWeight.Normal;
                header.TextDecorations = isSelected ? TextDecorations.Underline : null;
            }
        }
    }

    // A left click on a column header (not on its resize gripper) toggles that column as the paste target.
    private void OnWorksheetGridPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_viewModel is null
            || e.InitialPressMouseButton != MouseButton.Left
            || e.Source is not Visual source
            || source.FindAncestorOfType<Thumb>(includeSelf: true) is not null
            || source.FindAncestorOfType<TableViewColumnHeader>(includeSelf: true)?.Column is not { } column
            || !_gridColumnIds.TryGetValue(column, out var columnId))
        {
            return;
        }

        _viewModel.ToggleColumnSelectionCommand.Execute(columnId);
    }
}
