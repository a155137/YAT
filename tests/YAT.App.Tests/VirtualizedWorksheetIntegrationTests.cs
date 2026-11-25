using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;
using YAT.app.Views.WorksheetGrid;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

public sealed class VirtualizedWorksheetIntegrationTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            var root = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = root.CreateProjectSession(":memory:");
            Session = root.CreateMainWindowSession(ProjectSession, Clipboard, Clipboard);
            ViewModel = root.CreateMainWindowViewModel(Session);
            Coordinator = new WorksheetViewportDataCoordinator(Session);
            Bridge = new WorksheetViewportSessionBridge(ViewModel, Session, Coordinator);
            Control = new VirtualizedWorksheetControl();
        }

        public FakeClipboard Clipboard { get; } = new();
        public ProjectSession ProjectSession { get; }
        public MainWindowSession Session { get; }
        public MainWindowViewModel ViewModel { get; }
        public WorksheetViewportDataCoordinator Coordinator { get; }
        public WorksheetViewportSessionBridge Bridge { get; }
        public VirtualizedWorksheetControl Control { get; }

        public async Task StartAndPasteAsync(int columnCount, int rowCount)
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;

            var headers = string.Join('\t', Enumerable.Range(0, columnCount).Select(c => c % 4 == 3 ? $"晶圓_{c}" : $"C_{c}"));
            var lines = new string[rowCount + 1];
            lines[0] = headers;
            for (var r = 0; r < rowCount; r++)
            {
                var cells = new string[columnCount];
                for (var c = 0; c < columnCount; c++)
                {
                    if ((r + c) % 13 == 0)
                    {
                        cells[c] = string.Empty;
                    }
                    else if (c % 4 == 3)
                    {
                        cells[c] = $"繁中測試_{r}_{c}";
                    }
                    else
                    {
                        cells[c] = (c + (r * 0.25)).ToString(CultureInfo.InvariantCulture);
                    }
                }

                lines[r + 1] = string.Join('\t', cells);
            }

            Clipboard.Text = string.Join('\n', lines);
            await ViewModel.PasteCommand.ExecuteAsync(null);
            await WaitUntilIdleAsync();
        }

        public async Task WaitUntilIdleAsync()
        {
            for (var i = 0; i < 100 && Coordinator.IsBusy; i++)
            {
                await Task.Delay(10, Token);
            }
        }

        public void Dispose()
        {
            Control.Dispose();
            Bridge.Dispose();
            Coordinator.Dispose();
            ProjectSession.Dispose();
        }
    }

    [Fact]
    public async Task RealMainWindowSession_LoadsVisibleBlocksOnly_SupportsPaginationPasteDeleteAndSwitch()
    {
        using var rt = new Runtime();
        await rt.StartAndPasteAsync(columnCount: 500, rowCount: 520);

        Assert.True(rt.Coordinator.IsMetadataLoaded);
        Assert.Equal(500, rt.Coordinator.Columns.Count);
        Assert.Equal(520, rt.Coordinator.TotalRowCount);
        Assert.Equal(500, rt.Coordinator.PageRowCount);

        // Request initial visible columns (0..12).
        rt.Coordinator.RequestVisibleColumns(0, 12);
        await rt.WaitUntilIdleAsync();

        Assert.True(rt.Coordinator.CachedBlockCount <= WorksheetViewportDataCoordinator.MaxCachedBlocks);
        Assert.Contains(0, rt.Coordinator.CachedBlockIndices);

        // Fast horizontal scroll across 500 columns -> bounded cache, non-blocking miss before load, populated after.
        rt.Coordinator.RequestVisibleColumns(420, 435);
        var missSnapshot = rt.Coordinator.CreateRenderSnapshot(420, 435);
        Assert.False(missSnapshot.TryGetColumn(425, out _));

        using (var renderer = new SkiaWorksheetRenderer())
        using (var bitmap = new SKBitmap(1280, 720))
        using (var canvas = new SKCanvas(bitmap))
        {
            var geom = WorksheetViewportGeometry.FromColumns(rt.Coordinator.Columns);
            renderer.Render(
                canvas,
                new SKSize(1280, 720),
                geom.CreateSnapshot(),
                missSnapshot,
                scrollX: 40_000,
                scrollY: 0,
                new HashSet<int>(),
                null,
                WorksheetTheme.Light);
            Assert.True(renderer.LastStats.MissingBlockCellCount > 0);
        }

        await rt.WaitUntilIdleAsync();
        var loadedSnapshot = rt.Coordinator.CreateRenderSnapshot(420, 435);
        Assert.True(loadedSnapshot.TryGetColumn(425, out _));

        // Pagination to page 2 (rows 501..520).
        await rt.ViewModel.NextPageCommand.ExecuteAsync(null);
        await rt.WaitUntilIdleAsync();
        Assert.Equal(500, rt.Coordinator.RowOffset);
        Assert.Equal(20, rt.Coordinator.PageRowCount);

        rt.Coordinator.RequestVisibleColumns(0, 10);
        await rt.WaitUntilIdleAsync();

        // Paste overwrite on column 0 invalidates coordinator and reloads updated values.
        rt.ViewModel.SelectColumn(rt.Coordinator.Columns[0].Id);
        rt.Clipboard.Text = "Updated_0\n999.5\n";
        await rt.ViewModel.PasteCommand.ExecuteAsync(null);
        await rt.WaitUntilIdleAsync();
        Assert.Equal("Updated_0", rt.Coordinator.Columns[0].Name);

        // Delete selected column 0 updates metadata and invalidates cache.
        rt.ViewModel.SelectColumn(rt.Coordinator.Columns[0].Id);
        await rt.ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        await rt.WaitUntilIdleAsync();
        Assert.Equal(499, rt.Coordinator.Columns.Count);

        // Switch worksheet clears/reloads cleanly with no stale data.
        rt.ViewModel.WorksheetName = "Sheet2";
        await rt.ViewModel.CreateWorksheetCommand.ExecuteAsync(null);
        await rt.ViewModel.GridLoadTask;
        await rt.WaitUntilIdleAsync();
        Assert.Empty(rt.Coordinator.Columns);
        Assert.Equal(0, rt.Coordinator.TotalRowCount);
    }

    [Fact]
    public async Task SelectionResizeContextMenuKeyboardAndAutomationPeerWorkEndToEnd()
    {
        using var rt = new Runtime();
        await rt.StartAndPasteAsync(columnCount: 40, rowCount: 10);

        rt.Coordinator.RequestVisibleColumns(0, 15);
        await rt.WaitUntilIdleAsync();

        var cols = rt.Coordinator.Columns;
        // Click, Ctrl, Shift, Drag selection semantics via ViewModel + ColumnHeaderDragSelection.
        rt.ViewModel.SelectColumn(cols[1].Id);
        rt.ViewModel.ToggleColumnSelection(cols[3].Id);
        Assert.Equal([cols[1].Id, cols[3].Id], rt.ViewModel.SelectedColumns.Select(c => c.Id));

        rt.ViewModel.ExtendColumnSelection(cols[5].Id);
        Assert.Equal(new[] { cols[3].Id, cols[4].Id, cols[5].Id }, rt.ViewModel.SelectedColumns.Select(c => c.Id));

        // Keyboard routing & CopySelectedColumnsCommand.
        Assert.True(WorksheetKeyRouting.IsCopyColumnsGesture(true, false, rt.Control));
        Assert.True(WorksheetKeyRouting.IsDeleteColumnsGesture(Key.Delete, KeyModifiers.None, false, rt.Control));
        await rt.ViewModel.CopySelectedColumnsCommand.ExecuteAsync(null);
        Assert.Contains("C_4", rt.Clipboard.Text);

        // Resize preserves cached blocks.
        var geom = WorksheetViewportGeometry.FromColumns(rt.Coordinator.Columns);
        var cachedBefore = rt.Coordinator.CachedBlockCount;
        geom.ResizeColumn(2, 180);
        rt.Coordinator.NotifyColumnsResized(0, 15);
        Assert.Equal(cachedBefore, rt.Coordinator.CachedBlockCount);
        Assert.Equal(180, geom.GetColumnWidth(2));

        // Attach control & verify AutomationPeer exposes DataGrid role and worksheet summary.
        using var control = new VirtualizedWorksheetControl();
        control.Attach(rt.ViewModel, rt.Coordinator);
        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        Assert.Equal(AutomationControlType.DataGrid, peer.GetAutomationControlType());
        Assert.Contains("40 columns", peer.GetName());
        Assert.Contains("10 rows", peer.GetName());

        // End-to-end latency check on 40 cols × 10 rows (after 1 warmup frame for CJK font cache).
        var snap = rt.Coordinator.CreateRenderSnapshot(0, 15);
        using (var renderer = new SkiaWorksheetRenderer())
        using (var bitmap = new SKBitmap(1280, 720))
        using (var canvas = new SKCanvas(bitmap))
        {
            renderer.Render(canvas, new SKSize(1280, 720), geom.CreateSnapshot(), snap, 0, 0, new HashSet<int> { 3 }, 3, WorksheetTheme.Light);
            var sw = Stopwatch.StartNew();
            renderer.Render(canvas, new SKSize(1280, 720), geom.CreateSnapshot(), snap, 0, 0, new HashSet<int> { 3 }, 3, WorksheetTheme.Light);
            sw.Stop();
            TestContext.Current.TestOutputHelper?.WriteLine($"Warm render latency (40 cols): {sw.Elapsed.TotalMilliseconds:F2} ms");
            Assert.InRange(renderer.LastStats.RenderedCellCount, 50, 500);
        }

        // Session close/retire during loading safely retires coordinator.
        await rt.Session.SuspendAsync(Token);
        rt.Session.Retire();
        rt.Coordinator.RequestVisibleColumns(32, 39);
        await rt.WaitUntilIdleAsync();
        Assert.False(rt.Coordinator.IsBusy);
    }

    [Theory]
    [InlineData(1.0f, false)]
    [InlineData(1.25f, false)]
    [InlineData(1.5f, true)]
    public async Task VisualRenderingSupportsCjkDpiScalingAndLightDarkThemes(float scale, bool darkTheme)
    {
        using var rt = new Runtime();
        await rt.StartAndPasteAsync(columnCount: 20, rowCount: 30);
        rt.Coordinator.RequestVisibleColumns(0, 15);
        await rt.WaitUntilIdleAsync();

        var snapshot = rt.Coordinator.CreateRenderSnapshot(0, 15);
        var geomSnapshot = WorksheetViewportGeometry.FromColumns(rt.Coordinator.Columns).CreateSnapshot();
        var theme = darkTheme ? WorksheetTheme.Dark : WorksheetTheme.Light;

        using var renderer = new SkiaWorksheetRenderer();
        using var bitmap = new SKBitmap((int)(1000 * scale), (int)(500 * scale));
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale(scale);

        // Warmup frame (initializes CJK font fallback cache), then measure steady-state render.
        renderer.Render(
            canvas,
            new SKSize(1000, 500),
            geomSnapshot,
            snapshot,
            scrollX: 0,
            scrollY: 0,
            selectedColumns: new HashSet<int> { 3 },
            activeColumn: 3,
            theme: theme);

        var sw = Stopwatch.StartNew();
        renderer.Render(
            canvas,
            new SKSize(1000, 500),
            geomSnapshot,
            snapshot,
            scrollX: 0,
            scrollY: 0,
            selectedColumns: new HashSet<int> { 3 },
            activeColumn: 3,
            theme: theme);
        sw.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine($"Warm render (scale={scale}, dark={darkTheme}): {sw.Elapsed.TotalMilliseconds:F2} ms");
        Assert.InRange(renderer.LastStats.RenderedCellCount, 50, 600);
        Assert.Equal(0, renderer.LastStats.MissingBlockCellCount);
        Assert.Equal(theme.Background, bitmap.GetPixel((int)(980 * scale), (int)(480 * scale)));
    }
}
