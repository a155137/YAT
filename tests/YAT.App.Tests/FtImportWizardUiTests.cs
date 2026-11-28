using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.Clipboard;
using YAT.app.Composition;
using YAT.app.FtImport;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;
using YAT.app.Views;
using AvaloniaApplication = Avalonia.Application;

namespace YAT.App.Tests;

public sealed class FtImportWizardUiTests : IDisposable
{
    private readonly string _tempDir;

    public FtImportWizardUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft04-ui-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
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

    private sealed class StubClipboard : IClipboardTextReader, IClipboardTextWriter
    {
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubLifecycleDialogs : IProjectLifecycleDialogs
    {
        public Task<SaveChangesChoice> AskSaveChangesAsync(string projectName, bool closing) => Task.FromResult(SaveChangesChoice.Discard);
        public Task<string?> PickProjectToOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveLocationAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmOverwriteAsync(string destinationPath) => Task.FromResult(true);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    private sealed class StubWizardDialogs : IFtImportWizardDialogs
    {
        public Task<IReadOnlyList<string>> PickDatalogFilesAsync() => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string?> PickDatalogFolderAsync() => Task.FromResult<string?>(null);
    }

    private sealed class StubProfileStore(params ImportProfile[] profiles) : IImportProfileStore
    {
        public ImportProfileStoreSnapshot Enumerate() => new(profiles, []);
        public ImportProfile? Load(Guid id) => profiles.FirstOrDefault(p => p.Id == id);
        public void Create(ImportProfile profile) { }
        public void Update(ImportProfile profile) { }
        public void Delete(Guid id) { }
    }

    [Fact(Explicit = true)]
    public void WizardNavigatesThreeStepsInLightAndDarkThemesAndCapturesRequiredScreenshots()
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
                var screenshotDir = @"C:\Users\Y X\.gemini\antigravity\brain\f6cf2311-535e-478e-b4a7-2b9711a5ba8f\scratch";
                Directory.CreateDirectory(screenshotDir);

                var dataDir = Path.Combine(_tempDir, "data");
                Directory.CreateDirectory(dataDir);
                File.WriteAllText(
                    Path.Combine(dataDir, "E320BD-262.20261123111219.LOT001_TQB01RXAA1_F1_1_20261122171516_20261123110911.csv"),
                    "UNIT_ID,SOFT_BIN,SITE_NUM,IDD_STANDBY,ALS_DARK,VTH_CORE\n1,01,0,1.2,0.5,0.75\n2,05,1,2.3,0.6,0.76\n",
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(dataDir, "E320BD-262.20261123111219.LOT001_TQB01RXAA1_F1B45678R1_1_20261122171516_20261123110911.csv"),
                    "UNIT_ID,SOFT_BIN,SITE_NUM,IDD_STANDBY,PS_LEAK\n3,01,0,1.1,0.02\n",
                    new UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(dataDir, "E320BD-262.20261123111219.LOT002_TQB01RXAA1_F1_1_20261122171516_20261123110911.csv"),
                    "UNIT_ID,SOFT_BIN,SITE_NUM,IDD_STANDBY,ALS_DARK,IR_OUT\n4,01,0,1.3,0.4,0.88\n",
                    new UTF8Encoding(false));

                var profile = new ImportProfile
                {
                    Id = Guid.NewGuid(),
                    Name = "OSAT_FT_Standard",
                    SchemaVersion = ImportProfile.CurrentSchemaVersion,
                    FilenameRules = new FilenameRuleSet
                    {
                        AcceptedExtensions = [".csv"],
                        Mode = FilenameRuleMode.Structured,
                        SegmentDelimiters = ['.', '_'],
                        LotId = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                        TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.FixedRange, SegmentIndex = 4, StartIndex = 0, Length = 2 },
                        Retest = new FilenameRetestRule
                        {
                            RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 4 },
                            RetestMarker = "R",
                            RetestRoundField = new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 4, Marker = "R" }
                        },
                        FilePart = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 5 }
                    },
                    StructureRules = new DatalogStructureRuleSet
                    {
                        Delimiter = ",",
                        CompositeFields = [],
                        HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 1 },
                        DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 2 },
                        BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SOFT_BIN" },
                        SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" },
                        IgnoredColumns = [new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "UNIT_ID" }]
                    },
                    BinRules = new BinRuleSet { GoodBins = ["01"], NonRetestFailBins = ["99"] },
                    MissingValueRules = new MissingValueRuleSet { Markers = ["", "N/A"], Policy = MissingValuePolicy.UseMissing }
                };

                var composition = new CompositionRoot(TimeProvider.System, Path.Combine(_tempDir, "projects"));
                using var workspace = composition.CreateProjectWorkspace();
                var clipboard = new StubClipboard();
                var lifecycle = composition.CreateProjectLifecycle(workspace, clipboard, clipboard, new StubLifecycleDialogs());
                RunAndPump(lifecycle.StartAsync());

                try
                {
                    // 0. Verify MainWindow File menu order & renamed FT Profile Manager window
                    var mainWin = new MainWindow { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
                    var analysisResults = new FakeAnalysisResultPresenter();
                    var serializer = new YAT.Infrastructure.ImportProfiles.JsonImportProfileSerializer();
                    var profileStore = new StubProfileStore(profile);
                    var shell = composition.CreateMainWindowShellViewModel(
                        lifecycle,
                        composition.CreateGraphSetup(new FakeGraphSetupDialogs(), new FakeGraphWindowPresenter()),
                        composition.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), analysisResults),
                        composition.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), analysisResults),
                        importProfiles: composition.CreateImportProfileManager(
                            () => new AvaloniaImportProfileManagerDialogs(mainWin),
                            new AvaloniaImportProfileManagerPresenter(mainWin),
                            store: profileStore,
                            serializer: serializer,
                            editor: new AvaloniaImportProfileEditor(() => mainWin)),
                        ftImportWizard: composition.CreateFtImportWizard(
                            lifecycle,
                            () => new StubWizardDialogs(),
                            new AvaloniaFtImportWizardPresenter(mainWin),
                            store: profileStore));
                    mainWin.DataContext = shell;
                    mainWin.Show();
                    PumpDispatcher(80);
                    var importItem = mainWin.FindControl<MenuItem>("FtImportDatalogMenuItem");
                    var profileItem = mainWin.FindControl<MenuItem>("FtImportProfilesMenuItem");
                    Assert.NotNull(importItem);
                    Assert.NotNull(profileItem);
                    Assert.Equal("_Import FT Datalog...", importItem!.Header);
                    Assert.Equal("_FT Profile Manager...", profileItem!.Header);
                    Assert.NotNull(importItem.Command);
                    Assert.True(importItem.Command.CanExecute(null));
                    Assert.NotNull(profileItem.Command);
                    Assert.True(profileItem.Command.CanExecute(null));
                    var fileMenu = Assert.IsType<MenuItem>(importItem.Parent);
                    var items = fileMenu.Items.Cast<object?>().ToList();
                    Assert.Equal(items.IndexOf(importItem) + 1, items.IndexOf(profileItem));
                    fileMenu.Open();
                    PumpDispatcher(80);
                    CaptureWindow(mainWin, Path.Combine(screenshotDir, "ft04_0_file_menu.png"));
                    fileMenu.Close();
                    mainWin.Close();
                    PumpDispatcher(50);

                    var pmVm = new ImportProfileManagerViewModel(
                        profileStore,
                        serializer,
                        new AvaloniaImportProfileManagerDialogs(mainWin),
                        new AvaloniaImportProfileEditor(() => mainWin));
                    var pmWin = new ImportProfileManagerWindow(pmVm) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-20000, -20000) };
                    Assert.Equal("FT Profile Manager", pmWin.Title);
                    pmWin.Show();
                    PumpDispatcher(100);
                    CaptureWindow(pmWin, Path.Combine(screenshotDir, "ft04_0_profile_manager.png"));
                    pmWin.Close();
                    PumpDispatcher(50);

                    app.RequestedThemeVariant = ThemeVariant.Light;
                    var vm = new FtImportWizardViewModel(
                        new StubProfileStore(profile),
                        new StubWizardDialogs(),
                        lifecycle.CurrentSession!,
                        ["Sheet1"]);

                    var win = new FtImportWizardWindow(vm)
                    {
                        ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(-20000, -20000)
                    };
                    win.Show();
                    PumpDispatcher(120);

                    // 1. Step 1 file selection
                    RunAndPump(vm.AddFolderFromPathAsync(dataDir));
                    PumpDispatcher(80);
                    Assert.True(vm.IsStep1);
                    Assert.True(vm.CanGoNext);
                    CaptureWindow(win, Path.Combine(screenshotDir, "ft04_1_step1_files.png"));

                    // 2. Step 2 Test Item selection
                    RunAndPump(vm.NextCommand.ExecuteAsync(null));
                    PumpDispatcher(80);
                    Assert.True(vm.IsStep2);
                    vm.SelectedAvailableHighlights.Add("IDD_STANDBY");
                    vm.SelectedAvailableHighlights.Add("ALS_DARK");
                    vm.SelectedAvailableHighlights.Add("PS_LEAK");
                    vm.AddSelectedItemsCommand.Execute(null);
                    PumpDispatcher(80);
                    Assert.Equal(3, vm.SelectedCount);
                    CaptureWindow(win, Path.Combine(screenshotDir, "ft04_2_step2_test_items.png"));

                    // 3. Step 3 import confirmation
                    RunAndPump(vm.NextCommand.ExecuteAsync(null));
                    PumpDispatcher(80);
                    Assert.True(vm.IsStep3);
                    CaptureWindow(win, Path.Combine(screenshotDir, "ft04_3_step3_confirm.png"));

                    // 4. Import progress visual state
                    vm.IsImporting = true;
                    vm.ProgressPhaseText = "Streaming datalog rows...";
                    vm.ProgressFileText = "File 2 of 3: E320BD-262...LOT001...F1B45678R1_1.csv";
                    vm.ProcessedRowCount = 12_500;
                    vm.RetainedRowCount = 10_200;
                    vm.FilteredRowCount = 2_300;
                    PumpDispatcher(80);
                    CaptureWindow(win, Path.Combine(screenshotDir, "ft04_4_import_progress.png"));
                    vm.IsImporting = false;

                    // 5. Error diagnostics visual state
                    vm.ProgressPhaseText = "Import failed. No Worksheet was created.";
                    vm.Step3Diagnostics.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Error,
                        FtImportDiagnosticCode.SourceFileStaleOrMissing,
                        "Source 'LOT001_F1_1.csv' changed on disk since schema preparation. Rescan before importing."));
                    vm.WorksheetName = "Sheet1"; // triggers duplicate worksheet name validation error
                    PumpDispatcher(80);
                    CaptureWindow(win, Path.Combine(screenshotDir, "ft04_5_error_diagnostics.png"));

                    // 6. Dark theme
                    app.RequestedThemeVariant = ThemeVariant.Dark;
                    vm.Step3Diagnostics.Clear();
                    vm.WorksheetName = "FT_F1";
                    PumpDispatcher(100);
                    CaptureWindow(win, Path.Combine(screenshotDir, "ft04_6_dark_theme.png"));

                    win.Close();
                    PumpDispatcher(50);
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
            throw new Exception($"FT-04 UI test failed: {failure}", failure);
        }
    }

    private static void RunAndPump(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for async UI task while pumping dispatcher.");
            }

            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static void PumpDispatcher(int sleepMs)
    {
        Dispatcher.UIThread.RunJobs();
        Thread.Sleep(sleepMs);
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
}
