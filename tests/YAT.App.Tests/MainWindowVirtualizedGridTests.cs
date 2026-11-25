using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using YAT.App.Tests.TestDoubles;
using YAT.app;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;
using YAT.app.Views.WorksheetGrid;
using AvaloniaApplication = Avalonia.Application;

namespace YAT.App.Tests;

public sealed class MainWindowVirtualizedGridTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MetadataOnlyGridLoad_DoesNotMaterializeGridRows_AndPreservesSummaryPaginationPasteDeleteAndSwitch()
    {
        var root = new CompositionRoot(new FixedTimeProvider(Now));
        using var projectSession = root.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        var session = root.CreateMainWindowSession(projectSession, clipboard, clipboard);
        var viewModel = root.CreateMainWindowViewModel(session);
        viewModel.UseMetadataOnlyGridLoad = true;

        using var coordinator = new WorksheetViewportDataCoordinator(session);
        using var bridge = new WorksheetViewportSessionBridge(viewModel, session, coordinator);

        await viewModel.CreateDefaultWorkspaceAsync();
        await viewModel.GridLoadTask;

        const int cols = 1000;
        const int rows = 520;
        var headers = string.Join('\t', Enumerable.Range(0, cols).Select(c => $"C_{c}"));
        var lines = new string[rows + 1];
        lines[0] = headers;
        for (var r = 0; r < rows; r++)
        {
            lines[r + 1] = string.Join('\t', Enumerable.Range(0, cols).Select(c => (c + (r * 0.5)).ToString(CultureInfo.InvariantCulture)));
        }

        clipboard.Text = string.Join('\n', lines);
        await viewModel.PasteCommand.ExecuteAsync(null);
        await WaitUntilIdleAsync(coordinator);

        Assert.Empty(viewModel.GridRows);
        Assert.Equal(cols, viewModel.GridColumns.Count);
        Assert.True(viewModel.HasGridColumns);
        Assert.Equal(520, viewModel.TotalRowCount);
        Assert.Equal(500, viewModel.GridPageRowCount);
        Assert.Equal("Rows 1–500 of 520", viewModel.GridPageSummary);
        Assert.Equal("520 rows · 1000 columns", viewModel.SelectedWorksheetSummary);

        await viewModel.NextPageCommand.ExecuteAsync(null);
        await WaitUntilIdleAsync(coordinator);
        Assert.Empty(viewModel.GridRows);
        Assert.Equal(500, viewModel.GridRowOffset);
        Assert.Equal(20, viewModel.GridPageRowCount);
        Assert.Equal("Rows 501–520 of 520", viewModel.GridPageSummary);

        viewModel.SelectColumn(viewModel.GridColumns[0].ColumnId);
        await viewModel.CopySelectedColumnsCommand.ExecuteAsync(null);
        Assert.StartsWith("C_0\r\n", clipboard.Text, StringComparison.Ordinal);

        clipboard.Text = "Renamed_0\n42\n";
        await viewModel.PasteCommand.ExecuteAsync(null);
        await WaitUntilIdleAsync(coordinator);
        Assert.Equal("Renamed_0", coordinator.Columns[0].Name);
        Assert.Empty(viewModel.GridRows);

        viewModel.SelectColumn(viewModel.GridColumns[0].ColumnId);
        await viewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        await WaitUntilIdleAsync(coordinator);
        Assert.Equal(999, coordinator.Columns.Count);
        Assert.Empty(viewModel.GridRows);

        // Viewport error reporting & clearing without clobbering unrelated errors.
        viewModel.ReportViewportError("Viewport load failed");
        Assert.Equal("Viewport load failed", viewModel.ErrorMessage);
        viewModel.ReportViewportError(null);
        Assert.Null(viewModel.ErrorMessage);

        viewModel.WorksheetName = string.Empty;
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);
        var unrelatedError = viewModel.ErrorMessage;
        Assert.False(string.IsNullOrEmpty(unrelatedError));
        viewModel.ReportViewportError(null);
        Assert.Equal(unrelatedError, viewModel.ErrorMessage);

        viewModel.WorksheetName = "Sheet2";
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);
        await viewModel.GridLoadTask;
        await WaitUntilIdleAsync(coordinator);
        Assert.Empty(coordinator.Columns);
        Assert.False(viewModel.HasGridColumns);
    }

    [Fact(Explicit = true)]
    public void RealMainWindowGuiSmokeTest_1000Columns_ScrollLatencyAndMemory()
    {
        Exception? threadError = null;
        var report = new List<string>();

        var thread = new Thread(() =>
        {
            try
            {
                if (AvaloniaApplication.Current is null)
                {
                    AppBuilder.Configure<YAT.app.App>()
                        .UsePlatformDetect()
                        .WithInterFont()
                        .SetupWithoutStarting();
                }

                var root = new CompositionRoot(new FixedTimeProvider(Now));
                using var workspace = root.CreateProjectWorkspace();
                var window = new MainWindow
                {
                    Width = 1280,
                    Height = 720,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Position = new PixelPoint(-20000, -20000)
                };

                var clipboard = new FakeClipboard();
                var lifecycle = root.CreateProjectLifecycle(workspace, clipboard, clipboard, new FakeProjectLifecycleDialogs());
                var openGraphs = new OpenGraphsViewModel();
                var palettes = root.CreateGraphPaletteLibrary();
                var graphs = root.CreateGraphSetup(new FakeGraphSetupDialogs(), new FakeGraphWindowPresenter(), palettes);
                var results = new FakeAnalysisResultPresenter();
                var statistics = root.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), results);
                var capability = root.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), results);
                using var updateConn = root.CreateUpdateConnection();
                var updates = root.CreateUpdateCheck(new TestUpdateDialogs(), updateConn);
                var shell = root.CreateMainWindowShellViewModel(lifecycle, graphs, statistics, capability, updates, openGraphs);

                window.DataContext = shell;
                window.Show();
                PumpUntil(() => true);

                var startTask = shell.StartAsync();
                PumpUntil(() => startTask.IsCompleted);
                startTask.GetAwaiter().GetResult();

                var vm = shell.Project!;
                Assert.True(vm.UseMetadataOnlyGridLoad);

                const int cols = 1000;
                const int rows = 520;
                var headers = string.Join('\t', Enumerable.Range(0, cols).Select(c => c % 4 == 3 ? $"晶圓_{c}" : $"C_{c}"));
                var lines = new string[rows + 1];
                lines[0] = headers;
                for (var r = 0; r < rows; r++)
                {
                    lines[r + 1] = string.Join('\t', Enumerable.Range(0, cols).Select(c =>
                        c % 4 == 3 ? $"繁中_{r}" : (c + (r * 0.25)).ToString(CultureInfo.InvariantCulture)));
                }

                clipboard.Text = string.Join('\n', lines);
                var pasteTask = vm.PasteCommand.ExecuteAsync(null);
                PumpUntil(() => pasteTask.IsCompleted);
                pasteTask.GetAwaiter().GetResult();

                var gridControl = window.FindControl<VirtualizedWorksheetControl>("VirtualizedWorksheetGrid")!;
                Assert.True(gridControl.IsVisible);
                Assert.Empty(vm.GridRows);

                for (var i = 0; i < 100 && window.ViewportCoordinator!.IsBusy; i++)
                {
                    Thread.Sleep(10);
                    Dispatcher.UIThread.RunJobs();
                }

                using var rtb = new RenderTargetBitmap(new PixelSize(1280, 720), new Vector(96, 96));
                rtb.Render(window);

                var vertLatencies = new List<double>(20);
                var horizLatencies = new List<double>(20);
                for (var s = 1; s <= 20; s++)
                {
                    var sw = Stopwatch.StartNew();
                    gridControl.SetScroll(0, s * 180.0);
                    Dispatcher.UIThread.RunJobs();
                    rtb.Render(window);
                    vertLatencies.Add(sw.Elapsed.TotalMilliseconds);
                }

                for (var s = 1; s <= 20; s++)
                {
                    var sw = Stopwatch.StartNew();
                    gridControl.SetScroll(s * 2000.0, 0);
                    Dispatcher.UIThread.RunJobs();
                    rtb.Render(window);
                    horizLatencies.Add(sw.Elapsed.TotalMilliseconds);
                }

                for (var i = 0; i < 100 && window.ViewportCoordinator!.IsBusy; i++)
                {
                    Thread.Sleep(10);
                    Dispatcher.UIThread.RunJobs();
                }

                Assert.True(window.ViewportCoordinator!.CachedBlockCount <= WorksheetViewportDataCoordinator.MaxCachedBlocks);
                var heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);

                static double Pct(List<double> v, double p)
                {
                    var sorted = v.OrderBy(x => x).ToArray();
                    return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
                }

                report.Add($"MainWindow Skia Grid (1000 cols x 520 rows): V-P50={Pct(vertLatencies, 0.50):F2}ms, V-P95={Pct(vertLatencies, 0.95):F2}ms, H-P50={Pct(horizLatencies, 0.50):F2}ms, H-P95={Pct(horizLatencies, 0.95):F2}ms, CachedBlocks={window.ViewportCoordinator.CachedBlockCount}, Heap={heapMb:F1}MB");

                window.Close();
                PumpUntil(() => !window.IsVisible);
            }
            catch (Exception ex)
            {
                threadError = ex;
            }

            static void PumpUntil(Func<bool> condition)
            {
                var sw = Stopwatch.StartNew();
                while (!condition() && sw.ElapsedMilliseconds < 15000)
                {
                    Dispatcher.UIThread.RunJobs();
                    Thread.Sleep(5);
                }

                Dispatcher.UIThread.RunJobs();
            }
        });

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }

        thread.Start();
        thread.Join();

        if (threadError is not null)
        {
            throw new InvalidOperationException("MainWindow GUI smoke test failed.", threadError);
        }

        foreach (var line in report)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(line);
            Console.WriteLine(line);
        }
    }

    private static async Task WaitUntilIdleAsync(WorksheetViewportDataCoordinator coordinator)
    {
        for (var i = 0; i < 100 && coordinator.IsBusy; i++)
        {
            await Task.Delay(10, Token);
        }
    }

    private sealed class TestUpdateDialogs : YAT.app.Updates.IUpdateDialogs
    {
        public YAT.app.Updates.IUpdateLauncher Launcher { get; } = null!;
        public YAT.app.Updates.IUpdatePrompts Prompts { get; } = null!;
        public Task ShowAsync(UpdateViewModel update) => Task.CompletedTask;
        public Task<bool> CloseApplicationForInstallAsync() => Task.FromResult(false);
        public void ExitApplication() { }
        public Task ShowMessageAsync(string message) => Task.CompletedTask;
    }
}
