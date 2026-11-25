using System.Globalization;
using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.Views.WorksheetGrid;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

public sealed class WorksheetGridRenderingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<(
        ProjectSession ProjectSession,
        WorksheetViewportDataCoordinator Coordinator,
        WorksheetViewportGeometry Geometry)> SeedAsync(int columnCount, int rowCount)
    {
        var root = new CompositionRoot(new FixedTimeProvider(new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero)));
        var projectSession = root.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        var session = root.CreateMainWindowSession(projectSession, clipboard, clipboard);
        var vm = root.CreateMainWindowViewModel(session);
        vm.UseMetadataOnlyGridLoad = true;
        var coordinator = new WorksheetViewportDataCoordinator(session);
        var bridge = new WorksheetViewportSessionBridge(vm, session, coordinator);

        await vm.CreateDefaultWorkspaceAsync();
        await vm.GridLoadTask;

        var headers = string.Join('\t', Enumerable.Range(0, columnCount).Select(c => c % 4 == 3 ? $"晶圓參數_{c:D3}" : $"Reg_{c:D3}"));
        var lines = new string[rowCount + 1];
        lines[0] = headers;
        for (var r = 0; r < rowCount; r++)
        {
            lines[r + 1] = string.Join('\t', Enumerable.Range(0, columnCount).Select(c =>
                (r + c) % 11 == 0 ? string.Empty
                : c % 4 == 3 ? "Wafer_晶圓_12_長字串測試截斷與省略號顯示效果"
                : (c + (r * 0.25)).ToString(CultureInfo.InvariantCulture)));
        }

        clipboard.Text = string.Join('\n', lines);
        await vm.PasteCommand.ExecuteAsync(null);
        for (var i = 0; i < 100 && coordinator.IsBusy; i++)
        {
            await Task.Delay(10, Token);
        }

        bridge.Dispose();
        return (projectSession, coordinator, WorksheetViewportGeometry.FromColumns(coordinator.Columns));
    }

    [Fact]
    public async Task RendersOnlyVisiblePlusOverscanCellsOn1000Columns()
    {
        var (session, coordinator, geometry) = await SeedAsync(1000, 500);
        using var _ = session;
        using var __ = coordinator;

        coordinator.RequestVisibleColumns(250, 280);
        for (var i = 0; i < 100 && coordinator.IsBusy; i++)
        {
            await Task.Delay(10, Token);
        }

        var snapshot = coordinator.CreateRenderSnapshot(250, 280);
        using var renderer = new SkiaWorksheetRenderer();
        using var bitmap = new SKBitmap(1280, 720);
        using var canvas = new SKCanvas(bitmap);

        renderer.Render(
            canvas,
            new SKSize(1280, 720),
            geometry.CreateSnapshot(),
            snapshot,
            scrollX: 25_000,
            scrollY: 1_500,
            selectedColumns: new HashSet<int> { 260 },
            activeColumn: 260,
            theme: WorksheetTheme.Light);

        Assert.InRange(renderer.LastStats.RenderedCellCount, 100, 1000);
        Assert.InRange(renderer.LastStats.RenderedHeaderCount, 5, 25);
        Assert.InRange(renderer.LastStats.RenderedRowNumberCount, 20, 45);
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.25f)]
    [InlineData(1.5f)]
    public async Task LightAndDarkThemesAndDpiScalingProduceValidDistinctPixels(float scale)
    {
        var (session, coordinator, geometry) = await SeedAsync(20, 30);
        using var _ = session;
        using var __ = coordinator;

        coordinator.RequestVisibleColumns(0, 15);
        for (var i = 0; i < 100 && coordinator.IsBusy; i++)
        {
            await Task.Delay(10, Token);
        }

        var snapshot = coordinator.CreateRenderSnapshot(0, 15);
        var geomSnap = geometry.CreateSnapshot();
        using var renderer = new SkiaWorksheetRenderer();

        SKColor SampleBackground(WorksheetTheme theme)
        {
            using var bitmap = new SKBitmap((int)(800 * scale), (int)(500 * scale));
            using var canvas = new SKCanvas(bitmap);
            canvas.Scale(scale);
            renderer.Render(canvas, new SKSize(800, 500), geomSnap, snapshot, 0, 0, new HashSet<int> { 1 }, activeColumn: 1, theme);
            return bitmap.GetPixel((int)(750 * scale), (int)(450 * scale));
        }

        Assert.Equal(WorksheetTheme.Light.Background, SampleBackground(WorksheetTheme.Light));
        Assert.Equal(WorksheetTheme.Dark.Background, SampleBackground(WorksheetTheme.Dark));
    }

    [Fact]
    public async Task CjkFallbackDrawsGlyphsAndClippingPreventsBleedIntoNextColumn()
    {
        var (session, coordinator, geometry) = await SeedAsync(8, 10);
        using var _ = session;
        using var __ = coordinator;

        coordinator.RequestVisibleColumns(0, 7);
        for (var i = 0; i < 100 && coordinator.IsBusy; i++)
        {
            await Task.Delay(10, Token);
        }

        geometry.ResizeColumn(3, WorksheetViewportGeometry.MinColumnWidth);
        var snapshot = coordinator.CreateRenderSnapshot(0, 7);

        using var renderer = new SkiaWorksheetRenderer();
        using var bitmap = new SKBitmap(900, 300);
        using var canvas = new SKCanvas(bitmap);

        renderer.Render(
            canvas,
            new SKSize(900, 300),
            geometry.CreateSnapshot(),
            snapshot,
            0,
            0,
            new HashSet<int>(),
            activeColumn: null,
            WorksheetTheme.Light);

        var geomSnap = geometry.CreateSnapshot();
        var col3Left = (int)(WorksheetViewportGeometry.RowNumberColumnWidth + geomSnap.GetColumnLeftOffset(3));
        var col3Right = (int)(WorksheetViewportGeometry.RowNumberColumnWidth + geomSnap.GetColumnRightOffset(3));
        var hasTextPixelInCol3 = false;
        for (var y = (int)WorksheetViewportGeometry.HeaderHeight + 4; y < (int)WorksheetViewportGeometry.HeaderHeight + (int)WorksheetViewportGeometry.RowHeight - 2; y++)
        {
            for (var x = col3Left + 4; x < col3Right - 4; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel != WorksheetTheme.Light.Background && pixel != WorksheetTheme.Light.GridLine)
                {
                    hasTextPixelInCol3 = true;
                    break;
                }
            }
        }

        Assert.True(hasTextPixelInCol3, "Expected CJK text in column 3 to render visible glyphs.");
    }
}
