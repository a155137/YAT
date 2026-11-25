using System.ComponentModel;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.app.Views.WorksheetGrid;

// Isolated, production-candidate virtualized worksheet grid control.
// Integrates IWorksheetViewportDataProvider, immutable geometry/data snapshots,
// RefCountedRenderer DrawOperation lifecycle, column selection/resize, context menu,
// keyboard routing, Light/Dark themes, DPI scaling, and AutomationPeer.
internal sealed class VirtualizedWorksheetControl : Grid, IDisposable
{
    private readonly VirtualizedWorksheetCanvas _canvas;
    private readonly ScrollBar _verticalScrollBar;
    private readonly ScrollBar _horizontalScrollBar;
    private bool _syncingScrollBars;

    public VirtualizedWorksheetControl()
    {
        Focusable = true;
        ColumnDefinitions = new ColumnDefinitions("*,Auto");
        RowDefinitions = new RowDefinitions("*,Auto");

        _canvas = new VirtualizedWorksheetCanvas();
        SetColumn(_canvas, 0);
        SetRow(_canvas, 0);

        _verticalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            AllowAutoHide = false,
            SmallChange = WorksheetViewportGeometry.RowHeight,
            LargeChange = WorksheetViewportGeometry.RowHeight * 10
        };
        SetColumn(_verticalScrollBar, 1);
        SetRow(_verticalScrollBar, 0);

        _horizontalScrollBar = new ScrollBar
        {
            Orientation = Orientation.Horizontal,
            AllowAutoHide = false,
            SmallChange = 40,
            LargeChange = 240
        };
        SetColumn(_horizontalScrollBar, 0);
        SetRow(_horizontalScrollBar, 1);

        Children.Add(_canvas);
        Children.Add(_verticalScrollBar);
        Children.Add(_horizontalScrollBar);

        _verticalScrollBar.ValueChanged += (_, e) =>
        {
            if (!_syncingScrollBars)
            {
                _canvas.SetScroll(_canvas.ScrollX, e.NewValue);
            }
        };

        _horizontalScrollBar.ValueChanged += (_, e) =>
        {
            if (!_syncingScrollBars)
            {
                _canvas.SetScroll(e.NewValue, _canvas.ScrollY);
            }
        };

        _canvas.ScrollMetricsChanged += SyncScrollBars;
        SyncScrollBars();
    }

    internal WorksheetViewportGeometry Geometry => _canvas.Geometry;

    internal WorksheetGridRenderStats LastRenderStats => _canvas.LastRenderStats;

    internal double ScrollX => _canvas.ScrollX;

    internal double ScrollY => _canvas.ScrollY;

    internal IWorksheetViewportDataProvider? DataProvider => _canvas.DataProvider;

    public void Attach(MainWindowViewModel? viewModel, IWorksheetViewportDataProvider? dataProvider)
    {
        _canvas.Attach(viewModel, dataProvider);
        SyncScrollBars();
    }

    public void SetScroll(double scrollX, double scrollY) => _canvas.SetScroll(scrollX, scrollY);

    public void Dispose() => _canvas.Dispose();

    protected override AutomationPeer OnCreateAutomationPeer() =>
        new VirtualizedWorksheetAutomationPeer(this);

    private void SyncScrollBars()
    {
        _syncingScrollBars = true;
        try
        {
            var bodyW = Math.Max(0, _canvas.Bounds.Width - WorksheetViewportGeometry.RowNumberColumnWidth);
            var bodyH = Math.Max(0, _canvas.Bounds.Height - WorksheetViewportGeometry.HeaderHeight);
            var maxX = Math.Max(0, _canvas.Geometry.TotalDataWidth - bodyW);
            var rowCount = _canvas.DataProvider?.PageRowCount ?? 0;
            var maxY = Math.Max(0, _canvas.Geometry.GetTotalDataHeight(rowCount) - bodyH);

            _horizontalScrollBar.Minimum = 0;
            _horizontalScrollBar.Maximum = maxX;
            _horizontalScrollBar.ViewportSize = bodyW;
            _horizontalScrollBar.Value = Math.Clamp(_canvas.ScrollX, 0, maxX);

            _verticalScrollBar.Minimum = 0;
            _verticalScrollBar.Maximum = maxY;
            _verticalScrollBar.ViewportSize = bodyH;
            _verticalScrollBar.Value = Math.Clamp(_canvas.ScrollY, 0, maxY);
        }
        finally
        {
            _syncingScrollBars = false;
        }
    }
}

internal sealed class VirtualizedWorksheetAutomationPeer : ControlAutomationPeer
{
    private readonly VirtualizedWorksheetControl _owner;

    public VirtualizedWorksheetAutomationPeer(VirtualizedWorksheetControl owner)
        : base(owner)
    {
        _owner = owner;
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.DataGrid;

    protected override string GetNameCore()
    {
        var provider = _owner.DataProvider;
        return provider is null || !provider.IsMetadataLoaded
            ? "Worksheet grid"
            : $"Worksheet grid: {provider.Columns.Count} columns, {provider.TotalRowCount} rows";
    }

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;
}

internal sealed class VirtualizedWorksheetCanvas : Control, IDisposable
{
    private readonly ColumnHeaderDragSelection _headerDrag = new();
    private readonly Dictionary<Guid, double> _customWidths = [];
    private readonly HashSet<int> _selectedIndices = [];

    private IWorksheetViewportDataProvider? _dataProvider;
    private MainWindowViewModel? _viewModel;
    private WorksheetViewportGeometry _geometry = WorksheetViewportGeometry.Empty;
    private IReadOnlyList<Guid> _builtColumnIds = [];
    private Guid _builtWorksheetId;
    private RefCountedRenderer? _rendererRef;
    private int? _activeIndex;
    private int? _resizingColumn;
    private double _resizeStartX;
    private double _resizeStartWidth;
    private double _scrollX;
    private double _scrollY;
    private bool _disposed;

    public VirtualizedWorksheetCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
        ContextRequested += OnContextRequested;
    }

    public event Action? ScrollMetricsChanged;

    public IWorksheetViewportDataProvider? DataProvider => _dataProvider;

    public WorksheetViewportGeometry Geometry => _geometry;

    public WorksheetGridRenderStats LastRenderStats => _rendererRef?.Renderer.LastStats ?? default;

    public double ScrollX => _scrollX;

    public double ScrollY => _scrollY;

    public void Attach(MainWindowViewModel? viewModel, IWorksheetViewportDataProvider? dataProvider)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (_dataProvider is not null)
        {
            _dataProvider.ViewportDataChanged -= OnViewportDataChanged;
        }

        _headerDrag.End();
        _resizingColumn = null;
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            ContextMenu = null;
        }

        _viewModel = viewModel;
        _dataProvider = dataProvider;

        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (_dataProvider is not null)
        {
            _dataProvider.ViewportDataChanged += OnViewportDataChanged;
        }

        RebuildGeometryFromProvider();
        SyncSelectionFromViewModel();
        RequestVisibleColumnsForCurrentViewport();
        InvalidateVisual();
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_dataProvider is null || _viewModel is null)
        {
            e.Handled = true;
            return;
        }

        if (e.TryGetPosition(this, out var point))
        {
            var hit = _geometry.HitTest(point, _scrollX, _scrollY, _dataProvider.PageRowCount);
            if (hit.Area != WorksheetHitArea.ColumnHeader || hit.ColumnIndex < 0 || hit.ColumnIndex >= _dataProvider.Columns.Count)
            {
                e.Handled = true;
                return;
            }

            ContextMenu ??= CreateHeaderContextMenu();
            return;
        }

        // Keyboard context menu (Menu key / Shift+F10): allow only when columns are selected.
        if (_viewModel.SelectedColumns.Count == 0)
        {
            e.Handled = true;
            return;
        }

        ContextMenu ??= CreateHeaderContextMenu();
    }

    public void SetScroll(double scrollX, double scrollY)
    {
        var bodyW = Math.Max(0, Bounds.Width - WorksheetViewportGeometry.RowNumberColumnWidth);
        var bodyH = Math.Max(0, Bounds.Height - WorksheetViewportGeometry.HeaderHeight);
        var maxX = Math.Max(0, _geometry.TotalDataWidth - bodyW);
        var rowCount = _dataProvider?.PageRowCount ?? 0;
        var maxY = Math.Max(0, _geometry.GetTotalDataHeight(rowCount) - bodyH);

        var nextX = Math.Clamp(scrollX, 0, maxX);
        var nextY = Math.Clamp(scrollY, 0, maxY);
        var changed = nextX != _scrollX || nextY != _scrollY;

        _scrollX = nextX;
        _scrollY = nextY;

        if (changed)
        {
            ScrollMetricsChanged?.Invoke();
            RequestVisibleColumnsForCurrentViewport();
            InvalidateVisual();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SetScroll(_scrollX, _scrollY);
        RequestVisibleColumnsForCurrentViewport();
        ScrollMetricsChanged?.Invoke();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var deltaX = e.Delta.X;
        var deltaY = e.Delta.Y;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift) && deltaX == 0)
        {
            deltaX = deltaY;
            deltaY = 0;
        }

        SetScroll(
            _scrollX - (deltaX * 60.0),
            _scrollY - (deltaY * WorksheetViewportGeometry.RowHeight * 3.0));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Handled || _viewModel is null)
        {
            return;
        }

        var hotkeys = Avalonia.Application.Current?.PlatformSettings?.HotkeyConfiguration;
        if (hotkeys?.Paste.Any(gesture => gesture.Matches(e)) == true)
        {
            e.Handled = TryExecute(_viewModel.PasteCommand);
        }
        else if (WorksheetKeyRouting.IsCopyColumnsGesture(hotkeys?.Copy.Any(gesture => gesture.Matches(e)) == true, e.Handled, e.Source))
        {
            e.Handled = TryExecute(_viewModel.CopySelectedColumnsCommand);
        }
        else if (WorksheetKeyRouting.IsDeleteColumnsGesture(e.Key, e.KeyModifiers, e.Handled, e.Source))
        {
            e.Handled = TryExecute(_viewModel.DeleteSelectedColumnsCommand);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();

        if (_dataProvider is null || _viewModel is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        var props = e.GetCurrentPoint(this).Properties;
        var hit = _geometry.HitTest(point, _scrollX, _scrollY, _dataProvider.PageRowCount);
        var columnId = hit.ColumnIndex >= 0 && hit.ColumnIndex < _dataProvider.Columns.Count
            ? _dataProvider.Columns[hit.ColumnIndex].Id
            : (Guid?)null;

        if (props.IsRightButtonPressed)
        {
            _headerDrag.End();
            if (hit.Area == WorksheetHitArea.ColumnHeader && columnId is { } rightClicked)
            {
                _viewModel.SelectColumnForContextMenu(rightClicked);
            }

            return;
        }

        if (props.IsLeftButtonPressed && hit.Area == WorksheetHitArea.ColumnResizeGrip && hit.ColumnIndex >= 0)
        {
            _resizingColumn = hit.ColumnIndex;
            _resizeStartX = point.X;
            _resizeStartWidth = _geometry.GetColumnWidth(hit.ColumnIndex);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        var isHeader = hit.Area == WorksheetHitArea.ColumnHeader;
        if (_headerDrag.TryStart(isHeader ? columnId : null, props.IsLeftButtonPressed, isOnResizeGrip: false, e.KeyModifiers))
        {
            _viewModel.SelectColumn(_headerDrag.AnchorColumnId!.Value);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dataProvider is null)
        {
            return;
        }

        var point = e.GetPosition(this);
        if (_resizingColumn is { } resizingCol)
        {
            var newWidth = _geometry.ResizeColumn(resizingCol, _resizeStartWidth + (point.X - _resizeStartX));
            if (resizingCol < _dataProvider.Columns.Count)
            {
                _customWidths[_dataProvider.Columns[resizingCol].Id] = newWidth;
            }

            NotifyCoordinatorResized();
            ScrollMetricsChanged?.Invoke();
            InvalidateVisual();
            return;
        }

        if (_headerDrag.IsDragging && _viewModel is not null)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _headerDrag.End();
                return;
            }

            var hit = _geometry.HitTest(
                new Point(point.X, WorksheetViewportGeometry.HeaderHeight / 2.0),
                _scrollX,
                _scrollY,
                _dataProvider.PageRowCount,
                resizeGripHalfWidth: 0);

            var columnId = hit.Area == WorksheetHitArea.ColumnHeader && hit.ColumnIndex >= 0 && hit.ColumnIndex < _dataProvider.Columns.Count
                ? _dataProvider.Columns[hit.ColumnIndex].Id
                : (Guid?)null;

            if (_headerDrag.TryMoveTo(columnId))
            {
                _viewModel.SelectColumnRange(_headerDrag.AnchorColumnId!.Value, _headerDrag.CurrentColumnId!.Value);
            }

            return;
        }

        var hover = _geometry.HitTest(point, _scrollX, _scrollY, _dataProvider.PageRowCount);
        Cursor = hover.Area == WorksheetHitArea.ColumnResizeGrip
            ? new Cursor(StandardCursorType.SizeWestEast)
            : Cursor.Default;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left)
        {
            return;
        }

        if (_resizingColumn is not null)
        {
            _resizingColumn = null;
            e.Pointer.Capture(null);
            return;
        }

        if (_headerDrag.IsDragging)
        {
            _headerDrag.End();
            e.Pointer.Capture(null);
            return;
        }

        if (_dataProvider is null || _viewModel is null)
        {
            return;
        }

        var hit = _geometry.HitTest(e.GetPosition(this), _scrollX, _scrollY, _dataProvider.PageRowCount);
        if (hit.Area != WorksheetHitArea.ColumnHeader || hit.ColumnIndex < 0 || hit.ColumnIndex >= _dataProvider.Columns.Count)
        {
            return;
        }

        var columnId = _dataProvider.Columns[hit.ColumnIndex].Id;
        switch (WorksheetKeyRouting.GetHeaderClick(e.KeyModifiers))
        {
            case ColumnHeaderClick.Toggle:
                _viewModel.ToggleColumnSelection(columnId);
                break;
            case ColumnHeaderClick.Extend:
                _viewModel.ExtendColumnSelection(columnId);
                break;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _headerDrag.End();
        _resizingColumn = null;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ReleaseRenderer();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Attach(null, null);
        ReleaseRenderer();
    }

    private void ReleaseRenderer()
    {
        var current = Interlocked.Exchange(ref _rendererRef, null);
        current?.Release();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0 || _disposed || _dataProvider is null)
        {
            return;
        }

        var rendererRef = _rendererRef;
        if (rendererRef is null || !rendererRef.TryAddRef())
        {
            rendererRef = new RefCountedRenderer(new SkiaWorksheetRenderer());
            _rendererRef = rendererRef;
            if (!rendererRef.TryAddRef())
            {
                return;
            }
        }

        var geometrySnapshot = _geometry.CreateSnapshot();
        var bodyWidth = Math.Max(0, bounds.Width - WorksheetViewportGeometry.RowNumberColumnWidth);
        var visibleCols = geometrySnapshot.GetVisibleColumnRange(_scrollX, bodyWidth);
        var dataSnapshot = visibleCols.Count > 0
            ? _dataProvider.CreateRenderSnapshot(visibleCols.Start, visibleCols.EndExclusive - 1)
            : _dataProvider.CreateRenderSnapshot(0, -1);

        var selectedSnapshot = _selectedIndices.Count == 0
            ? (IReadOnlySet<int>)EmptySet.Instance
            : new HashSet<int>(_selectedIndices);

        context.Custom(new DrawOperation(
            bounds,
            rendererRef,
            geometrySnapshot,
            dataSnapshot,
            _scrollX,
            _scrollY,
            selectedSnapshot,
            _activeIndex,
            WorksheetTheme.For(ActualThemeVariant)));
    }

    private void OnViewportDataChanged(object? sender, EventArgs e)
    {
        if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(ApplyViewportDataChanged);
            return;
        }

        ApplyViewportDataChanged();
    }

    private void ApplyViewportDataChanged()
    {
        if (_disposed)
        {
            return;
        }

        RebuildGeometryFromProvider();
        SyncSelectionFromViewModel();
        RequestVisibleColumnsForCurrentViewport();
        ScrollMetricsChanged?.Invoke();
        InvalidateVisual();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.ActiveColumn) or nameof(MainWindowViewModel.SelectedColumns))
        {
            if (!Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (!_disposed)
                    {
                        SyncSelectionFromViewModel();
                        InvalidateVisual();
                    }
                });
                return;
            }

            SyncSelectionFromViewModel();
            InvalidateVisual();
        }
    }

    private void RebuildGeometryFromProvider()
    {
        if (_dataProvider is null || !_dataProvider.IsMetadataLoaded)
        {
            _geometry = WorksheetViewportGeometry.Empty;
            _builtColumnIds = [];
            return;
        }

        if (_builtWorksheetId != _dataProvider.WorksheetId)
        {
            _builtWorksheetId = _dataProvider.WorksheetId;
            _customWidths.Clear();
            _scrollX = 0;
            _scrollY = 0;
        }

        var columns = _dataProvider.Columns;
        var ids = columns.Select(c => c.Id).ToArray();
        if (ids.SequenceEqual(_builtColumnIds))
        {
            return;
        }

        _builtColumnIds = ids;
        _geometry = WorksheetViewportGeometry.FromColumns(columns, _customWidths);
    }

    private void SyncSelectionFromViewModel()
    {
        _selectedIndices.Clear();
        _activeIndex = null;

        if (_viewModel is null || _dataProvider is null || !_dataProvider.IsMetadataLoaded)
        {
            return;
        }

        var selectedIds = _viewModel.SelectedColumns.Select(c => c.Id).ToHashSet();
        var activeId = _viewModel.ActiveColumn?.Id;
        var columns = _dataProvider.Columns;

        for (var i = 0; i < columns.Count; i++)
        {
            var id = columns[i].Id;
            if (selectedIds.Contains(id))
            {
                _selectedIndices.Add(i);
            }

            if (activeId == id)
            {
                _activeIndex = i;
            }
        }
    }

    private void RequestVisibleColumnsForCurrentViewport()
    {
        if (_dataProvider is null || !_dataProvider.IsMetadataLoaded || _geometry.ColumnCount == 0)
        {
            return;
        }

        var bodyWidth = Bounds.Width > 0
            ? Math.Max(0, Bounds.Width - WorksheetViewportGeometry.RowNumberColumnWidth)
            : 1200.0;
        var visibleCols = _geometry.GetVisibleColumnRange(_scrollX, bodyWidth);
        if (visibleCols.Count > 0)
        {
            _dataProvider.RequestVisibleColumns(visibleCols.Start, visibleCols.EndExclusive - 1);
        }
    }

    private void NotifyCoordinatorResized()
    {
        if (_dataProvider is WorksheetViewportDataCoordinator coordinator && _geometry.ColumnCount > 0)
        {
            var bodyWidth = Bounds.Width > 0
                ? Math.Max(0, Bounds.Width - WorksheetViewportGeometry.RowNumberColumnWidth)
                : 1200.0;
            var visibleCols = _geometry.GetVisibleColumnRange(_scrollX, bodyWidth);
            if (visibleCols.Count > 0)
            {
                coordinator.NotifyColumnsResized(visibleCols.Start, visibleCols.EndExclusive - 1);
            }
        }
        else
        {
            RequestVisibleColumnsForCurrentViewport();
        }
    }

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

    private static bool TryExecute(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }

    private sealed class EmptySet : IReadOnlySet<int>
    {
        public static EmptySet Instance { get; } = new();
        public int Count => 0;
        public bool Contains(int item) => false;
        public IEnumerator<int> GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
        public bool IsProperSubsetOf(IEnumerable<int> other) => false;
        public bool IsProperSupersetOf(IEnumerable<int> other) => false;
        public bool IsSubsetOf(IEnumerable<int> other) => true;
        public bool IsSupersetOf(IEnumerable<int> other) => false;
        public bool Overlaps(IEnumerable<int> other) => false;
        public bool SetEquals(IEnumerable<int> other) => !other.Any();
    }

    internal sealed class RefCountedRenderer
    {
        private int _refCount = 1;

        public RefCountedRenderer(SkiaWorksheetRenderer renderer)
        {
            Renderer = renderer;
        }

        public SkiaWorksheetRenderer Renderer { get; }

        public bool IsDisposed => Volatile.Read(ref _refCount) == 0;

        public bool TryAddRef()
        {
            while (true)
            {
                var current = Volatile.Read(ref _refCount);
                if (current <= 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                {
                    return true;
                }
            }
        }

        public void Release()
        {
            if (Interlocked.Decrement(ref _refCount) == 0)
            {
                Renderer.Dispose();
            }
        }
    }

    internal sealed class DrawOperation : ICustomDrawOperation
    {
        private readonly RefCountedRenderer _rendererRef;
        private readonly WorksheetViewportGeometrySnapshot _geometry;
        private readonly WorksheetViewportRenderSnapshot _snapshot;
        private readonly double _scrollX;
        private readonly double _scrollY;
        private readonly IReadOnlySet<int> _selectedColumns;
        private readonly int? _activeColumn;
        private readonly WorksheetTheme _theme;
        private int _disposed;

        public DrawOperation(
            Rect bounds,
            RefCountedRenderer rendererRef,
            WorksheetViewportGeometrySnapshot geometry,
            WorksheetViewportRenderSnapshot snapshot,
            double scrollX,
            double scrollY,
            IReadOnlySet<int> selectedColumns,
            int? activeColumn,
            WorksheetTheme theme)
        {
            Bounds = bounds;
            _rendererRef = rendererRef;
            _geometry = geometry;
            _snapshot = snapshot;
            _scrollX = scrollX;
            _scrollY = scrollY;
            _selectedColumns = selectedColumns;
            _activeColumn = activeColumn;
            _theme = theme;
        }

        public Rect Bounds { get; }

        public bool HitTest(Point p) => Bounds.Contains(p);

        public void Render(ImmediateDrawingContext context)
        {
            if (Volatile.Read(ref _disposed) != 0 || _rendererRef.IsDisposed)
            {
                return;
            }

            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }

            using var lease = feature.Lease();
            _rendererRef.Renderer.Render(
                lease.SkCanvas,
                new SKSize((float)Bounds.Width, (float)Bounds.Height),
                _geometry,
                _snapshot,
                _scrollX,
                _scrollY,
                _selectedColumns,
                _activeColumn,
                _theme);
        }

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _rendererRef.Release();
            }
        }
    }
}
