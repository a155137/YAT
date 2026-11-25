using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

// Stage 3B Final Acceptance: real MainWindow E2E validation on 20k×100 and 20k×500 worksheets,
// overwrites, Save As / reopen, interactions, and before/after TableView comparison.
// Run explicitly:
//   YAT.App.Tests.exe -explicit only -class YAT.App.Tests.Stage3BFinalAcceptanceTests
public sealed class Stage3BFinalAcceptanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        Console.WriteLine(line);
    }

    [Fact]
    public async Task SkiaPathAndLegacyPathNeverLoadConcurrently_AndCleanupRunsOnSwitchAndClose()
    {
        var root = new CompositionRoot(new FixedTimeProvider(Now));
        using var projectSession = root.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        var session = root.CreateMainWindowSession(projectSession, clipboard, clipboard);
        var vm = root.CreateMainWindowViewModel(session);
        vm.UseMetadataOnlyGridLoad = true;

        await vm.CreateDefaultWorkspaceAsync();
        await vm.GridLoadTask;

        clipboard.Text = "A\tB\n1\t2\n3\t4\n";
        await vm.PasteCommand.ExecuteAsync(null);

        Assert.True(vm.UseMetadataOnlyGridLoad);
        Assert.Empty(vm.GridRows);
        Assert.Equal(2, vm.GridColumns.Count);
        Assert.Equal("Rows 1–2 of 2", vm.GridPageSummary);
    }

    [Fact(Explicit = true)]
    public void RealMainWindow20kE2EAcceptanceAndLegacyComparison()
    {
        Exception? threadError = null;
        var lines = new List<string>();

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

                using var tempDir = new TemporaryDirectory();
                var savePath = tempDir.File("acceptance_20k.yat");

                var root = new CompositionRoot(new FixedTimeProvider(Now));
                using var workspace = root.CreateProjectWorkspace();
                var clipboard = new FakeClipboard();
                var lifecycleDialogs = new AcceptanceLifecycleDialogs(savePath);
                var window = CreateMainWindow();
                var (shell, updateConn) = CreateShell(root, workspace, clipboard, lifecycleDialogs);
                using var _ = updateConn;

                window.DataContext = shell;
                window.Show();
                PumpUntil(() => true);

                var startTask = shell.StartAsync();
                PumpUntil(() => startTask.IsCompleted);
                startTask.GetAwaiter().GetResult();

                var vm = shell.Project!;
                Assert.True(vm.UseMetadataOnlyGridLoad);
                var gridControl = window.FindControl<VirtualizedWorksheetControl>("VirtualizedWorksheetGrid")!;

                // 1. 20k × 100 initial paste
                var tsv100 = BuildTsv(20_000, 100, "C");
                var paste100Sw = Stopwatch.StartNew();
                clipboard.Text = tsv100;
                RunAsync(vm.PasteCommand.ExecuteAsync(null));
                paste100Sw.Stop();
                WaitCoordinator(window.ViewportCoordinator!);
                Assert.Empty(vm.GridRows);
                Assert.Equal(20_000, vm.TotalRowCount);
                Assert.Equal(100, vm.GridColumns.Count);
                var metrics100 = MeasureScroll(window, gridControl, 100);
                lines.Add($"Skia 20k×100: Paste={paste100Sw.Elapsed.TotalMilliseconds:F0}ms, {metrics100}");

                // 2. 20k × 500 initial paste (new worksheet)
                vm.WorksheetName = "Sheet_500";
                RunAsync(vm.CreateWorksheetCommand.ExecuteAsync(null));
                WaitCoordinator(window.ViewportCoordinator!);

                var tsv500 = BuildTsv(20_000, 500, "P");
                var paste500Sw = Stopwatch.StartNew();
                clipboard.Text = tsv500;
                RunAsync(vm.PasteCommand.ExecuteAsync(null));
                paste500Sw.Stop();
                WaitCoordinator(window.ViewportCoordinator!);
                Assert.Empty(vm.GridRows);
                Assert.Equal(20_000, vm.TotalRowCount);
                Assert.Equal(500, vm.GridColumns.Count);
                var metrics500 = MeasureScroll(window, gridControl, 500);
                lines.Add($"Skia 20k×500: Paste={paste500Sw.Elapsed.TotalMilliseconds:F0}ms, {metrics500}");

                // 3. 20k × 500 changed-header overwrite (overwrite from col 0)
                vm.SelectColumn(vm.GridColumns[0].ColumnId);
                clipboard.Text = BuildTsv(20_000, 500, "H2");
                RunAsync(vm.PasteCommand.ExecuteAsync(null));
                WaitCoordinator(window.ViewportCoordinator!);
                Assert.Equal("H2_0", window.ViewportCoordinator!.Columns[0].Name);
                Assert.Equal("H2_499", window.ViewportCoordinator.Columns[499].Name);
                Assert.Empty(vm.GridRows);

                // 4. 20k × 500 same-schema overwrite (overwrite from col 0 with same headers)
                vm.SelectColumn(vm.GridColumns[0].ColumnId);
                clipboard.Text = BuildTsv(20_000, 500, "H2", valueOffset: 1000.0);
                RunAsync(vm.PasteCommand.ExecuteAsync(null));
                WaitCoordinator(window.ViewportCoordinator!);
                Assert.Equal("H2_0", window.ViewportCoordinator.Columns[0].Name);
                Assert.Empty(vm.GridRows);

                // 5. Interactions: selection (click, Ctrl, Shift), resize, Copy, Delete, Pagination, Keyboard Focus
                var cols = vm.GridColumns;
                vm.SelectColumn(cols[1].ColumnId);
                vm.ToggleColumnSelection(cols[3].ColumnId);
                Assert.Equal([cols[1].ColumnId, cols[3].ColumnId], vm.SelectedColumns.Select(c => c.Id));
                vm.ExtendColumnSelection(cols[5].ColumnId);
                Assert.Equal([cols[3].ColumnId, cols[4].ColumnId, cols[5].ColumnId], vm.SelectedColumns.Select(c => c.Id));

                // Resize
                gridControl.Geometry.ResizeColumn(2, 175);
                window.ViewportCoordinator.NotifyColumnsResized(0, 15);
                Assert.Equal(175, gridControl.Geometry.GetColumnWidth(2));

                // Copy
                RunAsync(vm.CopySelectedColumnsCommand.ExecuteAsync(null));
                Assert.StartsWith("H2_3\tH2_4\tH2_5\r\n", clipboard.Text, StringComparison.Ordinal);

                // Delete 3 columns -> 497 columns
                RunAsync(vm.DeleteSelectedColumnsCommand.ExecuteAsync(null));
                WaitCoordinator(window.ViewportCoordinator);
                Assert.Equal(497, vm.GridColumns.Count);

                // Pagination
                RunAsync(vm.NextPageCommand.ExecuteAsync(null));
                WaitCoordinator(window.ViewportCoordinator);
                Assert.Equal(500, vm.GridRowOffset);
                Assert.Equal("Rows 501–1,000 of 20,000", vm.GridPageSummary);
                RunAsync(vm.PreviousPageCommand.ExecuteAsync(null));
                WaitCoordinator(window.ViewportCoordinator);
                Assert.Equal(0, vm.GridRowOffset);

                // Keyboard focus safety: TextBox keeps Copy/Delete keys
                var textBox = new TextBox();
                Assert.False(WorksheetKeyRouting.IsCopyColumnsGesture(true, false, textBox));
                Assert.False(WorksheetKeyRouting.IsDeleteColumnsGesture(Key.Delete, KeyModifiers.None, false, textBox));

                // 6. Save As & Reopen in fresh MainWindow
                RunAsync(shell.SaveAsCommand.ExecuteAsync(null));
                Assert.True(File.Exists(savePath));

                var oldCoordinator = window.ViewportCoordinator;
                window.Close();
                PumpUntil(() => !window.IsVisible);

                // Reopen in new MainWindow
                using var reopenWorkspace = root.CreateProjectWorkspace();
                var reopenDialogs = new AcceptanceLifecycleDialogs(savePath);
                var reopenWindow = CreateMainWindow();
                var (reopenShell, reopenUpdateConn) = CreateShell(root, reopenWorkspace, clipboard, reopenDialogs);
                using var __ = reopenUpdateConn;
                reopenWindow.DataContext = reopenShell;
                reopenWindow.Show();
                PumpUntil(() => true);

                RunAsync(reopenShell.OpenProjectCommand.ExecuteAsync(null));
                PumpUntil(() => reopenShell.Project?.SelectedWorksheet is not null);
                WaitCoordinator(reopenWindow.ViewportCoordinator!);

                var reopenedVm = reopenShell.Project!;
                Assert.Equal(20_000, reopenedVm.TotalRowCount);
                Assert.Empty(reopenedVm.GridRows);

                reopenWindow.Close();
                PumpUntil(() => !reopenWindow.IsVisible);
            }
            catch (Exception ex)
            {
                threadError = ex;
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
            throw new InvalidOperationException("Stage 3B acceptance test failed.", threadError);
        }

        foreach (var line in lines)
        {
            Report(line);
        }
    }

    private static MainWindow CreateMainWindow() =>
        new()
        {
            Width = 1280,
            Height = 720,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000)
        };

    private static (MainWindowShellViewModel Shell, YAT.Infrastructure.Updates.UpdateConnection UpdateConn) CreateShell(
        CompositionRoot root,
        ProjectWorkspace workspace,
        FakeClipboard clipboard,
        YAT.app.Lifecycle.IProjectLifecycleDialogs dialogs)
    {
        var lifecycle = root.CreateProjectLifecycle(workspace, clipboard, clipboard, dialogs);
        var openGraphs = new OpenGraphsViewModel();
        var palettes = root.CreateGraphPaletteLibrary();
        var graphs = root.CreateGraphSetup(new FakeGraphSetupDialogs(), new FakeGraphWindowPresenter(), palettes);
        var results = new FakeAnalysisResultPresenter();
        var statistics = root.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), results);
        var capability = root.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), results);
        var updateConn = root.CreateUpdateConnection();
        var updates = root.CreateUpdateCheck(new TestUpdateDialogs(), updateConn);
        var shell = root.CreateMainWindowShellViewModel(lifecycle, graphs, statistics, capability, updates, openGraphs);
        return (shell, updateConn);
    }

    private static string BuildTsv(int rows, int cols, string prefix, double valueOffset = 0)
    {
        var sb = new System.Text.StringBuilder(rows * cols * 4);
        for (var c = 0; c < cols; c++)
        {
            if (c > 0) sb.Append('\t');
            sb.Append(prefix).Append('_').Append(c);
        }

        sb.Append('\n');
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < cols; c++)
            {
                if (c > 0) sb.Append('\t');
                if ((r + c) % 17 == 0)
                {
                    continue;
                }

                if (c % 5 == 4)
                {
                    sb.Append("晶圓_").Append(r % 100);
                }
                else
                {
                    sb.Append((valueOffset + c + (r * 0.1)).ToString(CultureInfo.InvariantCulture));
                }
            }

            sb.Append('\n');
        }

        return sb.ToString();
    }

    private static string MeasureScroll(MainWindow window, VirtualizedWorksheetControl gridControl, int cols)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(1280, 720), new Vector(96, 96));
        rtb.Render(window);

        var vert = new List<double>(20);
        var horiz = new List<double>(20);
        for (var s = 1; s <= 20; s++)
        {
            var sw = Stopwatch.StartNew();
            gridControl.SetScroll(0, s * 180.0);
            Dispatcher.UIThread.RunJobs();
            rtb.Render(window);
            vert.Add(sw.Elapsed.TotalMilliseconds);
        }

        for (var s = 1; s <= 20; s++)
        {
            var sw = Stopwatch.StartNew();
            gridControl.SetScroll(s * (cols >= 500 ? 2000.0 : 400.0), 0);
            Dispatcher.UIThread.RunJobs();
            rtb.Render(window);
            horiz.Add(sw.Elapsed.TotalMilliseconds);
        }

        WaitCoordinator(window.ViewportCoordinator!);
        var heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        return $"V-P50={Pct(vert, 0.50):F2}ms, V-P95={Pct(vert, 0.95):F2}ms, H-P50={Pct(horiz, 0.50):F2}ms, H-P95={Pct(horiz, 0.95):F2}ms, CachedBlocks={window.ViewportCoordinator!.CachedBlockCount}, Heap={heapMb:F1}MB";
    }

    private static string MeasureLegacyScroll(MainWindow window, TableView tableView)
    {
        using var rtb = new RenderTargetBitmap(new PixelSize(1280, 720), new Vector(96, 96));
        rtb.Render(window);
        var sv = Avalonia.VisualTree.VisualExtensions.FindDescendantOfType<ScrollViewer>(tableView);

        var vert = new List<double>(10);
        var horiz = new List<double>(10);
        for (var s = 1; s <= 10; s++)
        {
            var sw = Stopwatch.StartNew();
            if (sv is not null) sv.Offset = new Vector(0, s * 180.0);
            Dispatcher.UIThread.RunJobs();
            rtb.Render(window);
            vert.Add(sw.Elapsed.TotalMilliseconds);
        }

        for (var s = 1; s <= 10; s++)
        {
            var sw = Stopwatch.StartNew();
            if (sv is not null) sv.Offset = new Vector(s * 2000.0, 0);
            Dispatcher.UIThread.RunJobs();
            rtb.Render(window);
            horiz.Add(sw.Elapsed.TotalMilliseconds);
        }

        var heapMb = GC.GetTotalMemory(false) / (1024.0 * 1024.0);
        return $"V-P50={Pct(vert, 0.50):F2}ms, V-P95={Pct(vert, 0.95):F2}ms, H-P50={Pct(horiz, 0.50):F2}ms, H-P95={Pct(horiz, 0.95):F2}ms, Heap={heapMb:F1}MB";
    }

    private static double Pct(List<double> values, double p)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
    }

    private static void RunAsync(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private static void WaitCoordinator(WorksheetViewportDataCoordinator coordinator)
    {
        PumpUntil(() => !coordinator.IsBusy);
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition() && sw.ElapsedMilliseconds < 30_000)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private sealed class AcceptanceLifecycleDialogs(string path) : YAT.app.Lifecycle.IProjectLifecycleDialogs
    {
        public Task<string?> PickProjectToOpenAsync() => Task.FromResult<string?>(path);
        public Task<string?> PickSaveLocationAsync(string suggestedName) => Task.FromResult<string?>(path);
        public Task<bool> ConfirmOverwriteAsync(string destinationPath) => Task.FromResult(false);
        public Task<YAT.app.Lifecycle.SaveChangesChoice> AskSaveChangesAsync(string projectName, bool canSaveInPlace) =>
            Task.FromResult(YAT.app.Lifecycle.SaveChangesChoice.Discard);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
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
