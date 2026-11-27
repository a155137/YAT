using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.ImportProfiles;
using YAT.app.Composition;
using YAT.app.ImportProfiles;
using YAT.app.ViewModels;
using YAT.app.Views;
using YAT.Infrastructure.ImportProfiles;
using AvaloniaApplication = Avalonia.Application;

namespace YAT.App.Tests;

public sealed class ImportProfileManagerUiTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storeDir;

    public ImportProfileManagerUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft02-1-ui-tests", Guid.NewGuid().ToString("N"));
        _storeDir = Path.Combine(_tempDir, "UserData", "ImportProfiles");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class NoOpDialogs : IImportProfileManagerDialogs
    {
        public Task<bool> ConfirmDeleteAsync(ImportProfile profile) => Task.FromResult(false);
        public Task<string?> PickImportFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickExportFileAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmOverwriteExportAsync(string destinationPath) => Task.FromResult(false);
        public Task<ImportDuplicateIdChoice> AskDuplicateIdAsync(ImportProfile importedProfile, ImportProfile existingProfile) =>
            Task.FromResult(ImportDuplicateIdChoice.Cancel);
        public Task ShowErrorAsync(string message, string? detail = null) => Task.CompletedTask;
    }

    private sealed class BlockingPresenter : IImportProfileManagerPresenter
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ShowCalls { get; private set; }

        public Task ShowAsync(ImportProfileManagerViewModel viewModel)
        {
            ShowCalls++;
            return _tcs.Task;
        }

        public void Release() => _tcs.TrySetResult();
    }

    [Fact]
    public async Task ControllerPreventsDuplicateWindowsAndRespectsBusyGuard()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var serializer = new JsonImportProfileSerializer();
        var presenter = new BlockingPresenter();
        var busy = false;
        var controller = new ImportProfileManagerController(store, serializer, () => new NoOpDialogs(), presenter, canOpen: () => !busy);

        // Busy guard blocks opening
        busy = true;
        await controller.OpenAsync();
        Assert.Equal(0, presenter.ShowCalls);

        // Normal open blocks duplicate concurrent open
        busy = false;
        var first = controller.OpenAsync();
        Assert.True(controller.IsOpen);
        await controller.OpenAsync();
        Assert.Equal(1, presenter.ShowCalls);

        presenter.Release();
        await first;
        Assert.False(controller.IsOpen);
    }

    [Fact(Explicit = true)]
    public void MainWindowMenuWiringAndProfileManagerWindowRenderLightDarkAndCaptureScreenshots()
    {
        Exception? failure = null;
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

                var app = AvaloniaApplication.Current!;
                var originalTheme = app.RequestedThemeVariant;
                try
                {
                    var store = new JsonImportProfileStore(_storeDir);
                    var screenshotDir = Path.Combine(
                        @"C:\Users\Y X\.gemini\antigravity\brain\2c89ef7e-0064-40ee-8b87-8744b8c77d4f\scratch");
                    Directory.CreateDirectory(screenshotDir);

                    // 1. Verify MainWindow menu entry wiring
                    var root = new CompositionRoot(TimeProvider.System);
                    using var workspace = root.CreateProjectWorkspace();
                    var clipboard = new FakeClipboard();
                    var mainWindow = new MainWindow
                    {
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(-20000, -20000)
                    };
                    var lifecycle = root.CreateProjectLifecycle(workspace, clipboard, clipboard, new FakeProjectLifecycleDialogs());
                    var graphs = root.CreateGraphSetup(new FakeGraphSetupDialogs(), new FakeGraphWindowPresenter());
                    var results = new FakeAnalysisResultPresenter();
                    var stats = root.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), results);
                    var cap = root.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), results);
                    var serializer = new JsonImportProfileSerializer();
                    var controller = root.CreateImportProfileManager(() => new NoOpDialogs(), new BlockingPresenter(), store, serializer);
                    var shell = root.CreateMainWindowShellViewModel(lifecycle, graphs, stats, cap, importProfiles: controller);
                    mainWindow.DataContext = shell;

                    var menuItem = mainWindow.FindControl<MenuItem>("FtImportProfilesMenuItem");
                    Assert.NotNull(menuItem);
                    Assert.Equal("_FT Import Profiles...", menuItem.Header);
                    Assert.NotNull(menuItem.Command);
                    Assert.True(menuItem.Command.CanExecute(null));

                    // 2. Empty ImportProfileManagerWindow (Light theme) + screenshot
                    app.RequestedThemeVariant = ThemeVariant.Light;
                    var emptyVm = new ImportProfileManagerViewModel(store, serializer, new NoOpDialogs());
                    var emptyWin = CreateOffscreenManagerWindow(emptyVm);
                    try
                    {
                        emptyWin.Show();
                        PumpDispatcher();
                        Assert.True(FindNamed<TextBlock>(emptyWin, "EmptyProfilesMessage").IsVisible);
                        Assert.False(FindNamed<Button>(emptyWin, "NewProfileButton").IsEffectivelyEnabled);
                        Assert.False(FindNamed<Button>(emptyWin, "EditProfileButton").IsEffectivelyEnabled);
                        Assert.True(FindNamed<TextBlock>(emptyWin, "EditorUnavailableNotice").IsVisible);

                        CaptureWindow(emptyWin, Path.Combine(screenshotDir, "ft02_1_manager_empty.png"));
                    }
                    finally
                    {
                        emptyWin.Close();
                        PumpDispatcher();
                    }

                    // 3. Populated ImportProfileManagerWindow (Light theme) + selection updates details + screenshot
                    var p1 = CreateProfile("KYEC_FT");
                    var p2 = CreateProfile("YTEC_FT_ATE2");
                    store.Create(p1);
                    store.Create(p2);

                    var populatedVm = new ImportProfileManagerViewModel(store, serializer, new NoOpDialogs());
                    var win = CreateOffscreenManagerWindow(populatedVm);
                    try
                    {
                        win.Show();
                        PumpDispatcher();

                        var nameBlock = FindNamed<TextBlock>(win, "DetailNameValue");
                        Assert.Equal("KYEC_FT", nameBlock.Text);

                        populatedVm.Selected = populatedVm.Profiles[1];
                        PumpDispatcher();
                        Assert.Equal("YTEC_FT_ATE2", nameBlock.Text);

                        populatedVm.Selected = populatedVm.Profiles[0];
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_1_manager_selected_light.png"));

                        // 4. Dark theme compatibility + screenshot
                        app.RequestedThemeVariant = ThemeVariant.Dark;
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_1_manager_selected_dark.png"));
                    }
                    finally
                    {
                        win.Close();
                        PumpDispatcher();
                    }
                }
                finally
                {
                    app.RequestedThemeVariant = originalTheme;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (OperatingSystem.IsWindows())
        {
            thread.SetApartmentState(ApartmentState.STA);
        }
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            throw new InvalidOperationException("ImportProfileManager UI test failed.", failure);
        }
    }

    private static T FindNamed<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(control => string.Equals(control.Name, name, StringComparison.Ordinal));

    private static ImportProfileManagerWindow CreateOffscreenManagerWindow(ImportProfileManagerViewModel vm) =>
        new(vm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000)
        };

    private static void PumpDispatcher()
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(50);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CaptureWindow(Window window, string outputPath)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.Width));
        var height = Math.Max(1, (int)Math.Ceiling(window.Height));
        using var rtb = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        rtb.Render(window);
#pragma warning disable CS0618
        rtb.Save(outputPath);
#pragma warning restore CS0618
    }

    private static ImportProfile CreateProfile(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = new FilenameRuleSet
        {
            AcceptedExtensions = [".Reg.zip"],
            Mode = FilenameRuleMode.Structured,
            SegmentDelimiters = ['.', '_'],
            LotId = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 0 },
            TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 1 },
            Retest = new FilenameRetestRule
            {
                RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                OriginalToken = "ORG",
                RetestMarker = "RT"
            }
        },
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = ",",
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Test_Name", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "DATA_START", ColumnIndex = 0, MatchMode = KeywordMatchMode.StartsWith },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "HARD_BIN" },
            SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" }
        },
        BinRules = new BinRuleSet { GoodBins = ["1", "01"], NonRetestFailBins = ["2"] },
        MissingValueRules = new MissingValueRuleSet { Markers = ["", "N/A"], Policy = MissingValuePolicy.UseMissing }
    };
}
