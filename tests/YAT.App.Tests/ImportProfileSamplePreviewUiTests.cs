using System.IO.Compression;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;
using YAT.app.ViewModels;
using YAT.app.Views;
using AvaloniaApplication = Avalonia.Application;

namespace YAT.App.Tests;

public sealed class ImportProfileSamplePreviewUiTests : IDisposable
{
    private readonly string _tempDir;

    public ImportProfileSamplePreviewUiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft02-3-ui-tests", Guid.NewGuid().ToString("N"));
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

    [Fact(Explicit = true)]
    public void InteractivePreviewContextMenusTableRawAndEightScreenshotsWorkInLightAndDarkThemes()
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
                var screenshotDir = @"C:\Users\Y X\.gemini\antigravity\brain\2c89ef7e-0064-40ee-8b87-8744b8c77d4f\scratch";
                Directory.CreateDirectory(screenshotDir);

                var sampleZip = Path.Combine(
                    _tempDir,
                    "E320BD-262.20261123111219.XXX123.1-6846_TQB01RXAA1_F1_1_20261122171516_20261123110911.Reg.zip");
                var sampleCsv = """
                    LOT_HEADER,FT_LINE_A
                    Test_Name,HARD_BIN,SITE_NUM,TEST_TIME,IDD_mA,VTH_V,VOUT_V,FREQ_MHz
                    Unit,bin,site,ms,mA,V,V,MHz
                    DATA_START,01,1,15.2,12.45,0.652,1.801,24.0
                    DATA_START,01,2,14.8,12.61,0.648,1.798,24.1
                    DATA_START,02,1,16.1,18.90,0.715,1.750,23.9
                    DATA_START,01,2,15.0,N/A,0.650,1.800,24.0
                    """;
                using (var archive = ZipFile.Open(sampleZip, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(archive.CreateEntry("lot.csv").Open(), new UTF8Encoding(false)))
                {
                    writer.Write(sampleCsv);
                }

                try
                {
                    app.RequestedThemeVariant = ThemeVariant.Light;

                    // 6. Incomplete Profile preview screenshot
                    var incompleteVm = new ImportProfileBuilderViewModel(
                        new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null),
                        previewUiDispatch: a => Dispatcher.UIThread.Invoke(a),
                        previewDebounce: TimeSpan.Zero);
                    incompleteVm.ProfileName = "";
                    var incompleteWin = CreateOffscreenWindow(incompleteVm);
                    try
                    {
                        incompleteWin.Show();
                        incompleteVm.Preview.SetSampleFile(sampleZip);
                        WaitRefresh(incompleteVm);
                        incompleteVm.Validate();
                        PumpDispatcher();

                        Assert.True(incompleteVm.Preview.PreviewTableRows.Count == 7);
                        Assert.False(incompleteVm.Preview.IsFilenameMatched);
                        CaptureWindow(incompleteWin, Path.Combine(screenshotDir, "ft02_3_6_incomplete_profile_preview.png"));
                    }
                    finally
                    {
                        incompleteWin.Close();
                        PumpDispatcher();
                    }

                    // Full interactive session on representative sample
                    var vm = new ImportProfileBuilderViewModel(
                        new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null),
                        previewUiDispatch: a => Dispatcher.UIThread.Invoke(a),
                        previewDebounce: TimeSpan.Zero);
                    vm.ProfileName = "FT_Preview_Validated";
                    vm.AcceptedExtensions.Clear();
                    vm.AcceptedExtensions.Add(".Reg.zip");
                    vm.SegmentDelimitersText = "_";
                    vm.LotIdField.Kind = FilenameFieldKind.MarkerAfter;
                    vm.LotIdField.SegmentPosition = 1;
                    vm.LotIdField.Marker = ".";
                    vm.LotIdField.MarkerOccurrence = 2;
                    vm.TestStageField.Kind = FilenameFieldKind.FixedRange;
                    vm.TestStageField.SegmentPosition = 3;
                    vm.TestStageField.StartPosition = 1;
                    vm.TestStageField.Length = 2;
                    vm.RetestRoleField.IsEnabled = true;
                    vm.RetestRoleField.SegmentPosition = 3;
                    vm.RetestMarker = "R";
                    vm.FilePartField.IsEnabled = true;
                    vm.FilePartField.SegmentPosition = 4;
                    vm.AddPresetMissingMarkerCommand.Execute("N/A");

                    var win = CreateOffscreenWindow(vm);
                    try
                    {
                        win.Show();
                        vm.Preview.SetSampleFile(sampleZip);
                        WaitRefresh(vm);
                        PumpDispatcher();

                        // 1. Sample loaded — Table View
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_3_1_table_view.png"));

                        // 2. Raw View
                        FindNamed<Control>(win, "RawViewToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpDispatcher();
                        Assert.True(vm.Preview.IsRawView);
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_3_2_raw_view.png"));

                        // Back to Table View
                        FindNamed<Control>(win, "TableViewToggleButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpDispatcher();
                        Assert.True(vm.Preview.IsTableView);

                        // 3. Row-role context menu + execute Set as Header Row (row 2), Unit (row 3), Data Start (row 4)
                        var row2Btn = FindNamed<Button>(win, "PreviewRowHeader_2");
                        row2Btn.ContextMenu!.Open(row2Btn);
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_3_3_row_context_menu.png"));
                        row2Btn.ContextMenu.Close();

                        ClickMenuItem(row2Btn.ContextMenu, "SetHeaderRow_2");
                        WaitRefresh(vm);
                        PumpDispatcher();
                        Assert.Equal(2, vm.HeaderRow.RowNumber);

                        ClickMenuItem(FindNamed<Button>(win, "PreviewRowHeader_3").ContextMenu!, "SetUnitRow_3");
                        WaitRefresh(vm);
                        PumpDispatcher();
                        Assert.True(vm.UnitRow.IsEnabled);
                        Assert.Equal(3, vm.UnitRow.RowNumber);

                        ClickMenuItem(FindNamed<Button>(win, "PreviewRowHeader_4").ContextMenu!, "SetDataStartRow_4");
                        WaitRefresh(vm);
                        PumpDispatcher();
                        Assert.Equal(4, vm.DataStartRow.RowNumber);

                        // 4. Column-role context menu + execute Set as Bin (HARD_BIN), Set as Site (SITE_NUM), Add Ignore (TEST_TIME)
                        var col1Btn = FindNamed<Button>(win, "PreviewColHeader_1");
                        col1Btn.ContextMenu!.Open(col1Btn);
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_3_4_column_context_menu.png"));
                        col1Btn.ContextMenu.Close();

                        ClickMenuItem(col1Btn.ContextMenu, "SetBinCol_1");
                        WaitRefresh(vm);
                        PumpDispatcher();
                        Assert.Equal(ColumnSelectorKind.Index, vm.BinColumn.Kind);
                        Assert.Equal(2, vm.BinColumn.ColumnPosition);
                        Assert.True(vm.Preview.IsStructureResolved);

                        ClickMenuItem(FindNamed<Button>(win, "PreviewColHeader_2").ContextMenu!, "SetSiteCol_2");
                        WaitRefresh(vm);
                        PumpDispatcher();
                        Assert.Equal(ColumnSelectorKind.HeaderName, vm.SiteColumn.Kind);
                        Assert.Equal("SITE_NUM", vm.SiteColumn.HeaderName);

                        ClickMenuItem(FindNamed<Button>(win, "PreviewColHeader_0").ContextMenu!, "AddIgnoreCol_0");
                        WaitRefresh(vm);
                        PumpDispatcher();

                        ClickMenuItem(FindNamed<Button>(win, "PreviewColHeader_3").ContextMenu!, "AddIgnoreCol_3");
                        WaitRefresh(vm);
                        PumpDispatcher();
                        Assert.Equal(2, vm.IgnoredColumns.Count);

                        // 5. Assigned row and column highlights
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_3_5_assigned_highlights.png"));

                        // 7. Successful bounded sample validation
                        Assert.True(vm.Preview.IsFilenameMatched);
                        Assert.True(vm.Preview.IsStructureResolved);
                        Assert.True(vm.Preview.IsMeasurementValidationSuccess);
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_3_7_bounded_validation_success.png"));

                        // 8. Dark theme + verify minimum size keeps Save/Cancel accessible
                        app.RequestedThemeVariant = ThemeVariant.Dark;
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_3_8_dark_theme.png"));

                        win.Width = win.MinWidth;
                        win.Height = win.MinHeight;
                        PumpDispatcher();
                        Assert.True(win.MinWidth >= 960);
                        Assert.True(win.MinHeight >= 580);
                        var saveBtn = FindNamed<Button>(win, "SaveProfileButton");
                        var cancelBtn = FindNamed<Button>(win, "CancelProfileButton");
                        Assert.True(saveBtn.IsVisible && cancelBtn.IsVisible);

                        // 512-column x 50-row wide table rendering, horizontal scroll sync, refresh after assignment, and numeric Save safety.
                        var wideCsv = Path.Combine(_tempDir, "LOTW_FT_20261127.csv");
                        var wideHeader = "Test_Name,HARD_BIN,SITE_NUM," + string.Join(',', Enumerable.Range(0, 509).Select(i => $"T_{i:D3}")) + "\n";
                        var wideRow = "DATA_START,01,1," + string.Join(',', Enumerable.Range(0, 509).Select(i => $"{i}.25")) + "\n";
                        using (var sw = new StreamWriter(wideCsv, false, new UTF8Encoding(false)))
                        {
                            sw.Write(wideHeader);
                            for (var r = 0; r < 60; r++)
                            {
                                sw.Write(wideRow);
                            }
                        }

                        var swWide = System.Diagnostics.Stopwatch.StartNew();
                        vm.Preview.SetSampleFile(wideCsv);
                        WaitRefresh(vm);
                        PumpDispatcher();
                        swWide.Stop();

                        Assert.Equal(512, vm.Preview.PreviewColumns.Count);
                        Assert.Equal(50, vm.Preview.PreviewTableRows.Count);
                        Assert.True(swWide.ElapsedMilliseconds < 5_000, $"Wide 512x50 UI render took {swWide.ElapsedMilliseconds}ms.");

                        var headerScroll = FindNamed<ScrollViewer>(win, "TableHeaderHorizontalScroller");
                        var bodyScroll = FindNamed<ScrollViewer>(win, "TableHorizontalScroller");
                        bodyScroll.Offset = new Vector(240, 0);
                        PumpDispatcher();
                        Assert.Equal(bodyScroll.Offset.X, headerScroll.Offset.X, 1.0);

                        vm.Preview.AssignColumnRole(vm.Preview.PreviewColumns[3], PreviewColumnAction.AddToIgnored);
                        WaitRefresh(vm);
                        PumpDispatcher();
                        Assert.Equal(PreviewColumnRole.Ignored, vm.Preview.PreviewColumns[3].AssignedRole);

                        // Numeric input safety either clamps invalid row numbers (>= 1) or blocks Save.
                        vm.HeaderRow.Kind = RowMatchKind.FixedRowIndex;
                        vm.HeaderRow.RowNumber = 0;
                        PumpDispatcher();
                        Assert.True(vm.HeaderRow.RowNumber >= 1 || !vm.TrySave());
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
            throw new InvalidOperationException("ImportProfileSamplePreview UI test failed.", failure);
        }
    }

    private static void ClickMenuItem(ContextMenu menu, string name)
    {
        var item = menu.Items.OfType<MenuItem>().First(m => string.Equals(m.Name, name, StringComparison.Ordinal));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static void WaitRefresh(ImportProfileBuilderViewModel vm)
    {
        var task = vm.Preview.CurrentRefreshTask;
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }

        task.GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
    }

    private static ImportProfileBuilderWindow CreateOffscreenWindow(ImportProfileBuilderViewModel vm) =>
        new(vm)
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000)
        };

    private static T FindNamed<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(control => string.Equals(control.Name, name, StringComparison.Ordinal));

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
}
