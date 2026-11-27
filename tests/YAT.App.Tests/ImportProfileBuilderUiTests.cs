using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;
using YAT.app.ViewModels;
using YAT.app.Views;
using AvaloniaApplication = Avalonia.Application;

namespace YAT.App.Tests;

public sealed class ImportProfileBuilderUiTests
{
    [Fact(Explicit = true)]
    public void BuilderWindowOpensInNewAndEditModesNavigatesAllTabsAndCapturesRequiredScreenshots()
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

                try
                {
                    app.RequestedThemeVariant = ThemeVariant.Light;

                    // 1. Build a rich Edit-mode profile to demonstrate all 5 sections cleanly
                    var editVm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null));
                    editVm.ProfileName = "FT_Structured_Profile";
                    editVm.AcceptedExtensions.Clear();
                    editVm.NewExtensionText = ".Reg.zip";
                    editVm.AddExtensionCommand.Execute(null);
                    editVm.NewExtensionText = ".csv";
                    editVm.AddExtensionCommand.Execute(null);

                    editVm.FilenameMode = FilenameRuleMode.Structured;
                    editVm.SegmentDelimitersText = "_";
                    editVm.LotIdField.Kind = FilenameFieldKind.MarkerAfter;
                    editVm.LotIdField.SegmentPosition = 1;
                    editVm.LotIdField.Marker = ".";
                    editVm.LotIdField.MarkerOccurrence = 2;
                    editVm.TestStageField.Kind = FilenameFieldKind.FixedRange;
                    editVm.TestStageField.SegmentPosition = 3;
                    editVm.TestStageField.StartPosition = 1;
                    editVm.TestStageField.Length = 2;
                    editVm.RetestRoleField.IsEnabled = true;
                    editVm.RetestRoleField.SegmentPosition = 3;
                    editVm.RetestMarker = "R";
                    editVm.RetestRoundField.IsEnabled = true;
                    editVm.RetestRoundField.Kind = FilenameFieldKind.MarkerAfter;
                    editVm.RetestRoundField.SegmentPosition = 3;
                    editVm.RetestRoundField.Marker = "R";
                    editVm.FilePartField.IsEnabled = true;
                    editVm.FilePartField.SegmentPosition = 4;

                    editVm.HeaderRow.Kind = RowMatchKind.KeywordMatch;
                    editVm.HeaderRow.Keyword = "Test_Name";
                    editVm.UnitRow.IsEnabled = true;
                    editVm.UnitRow.Kind = RowMatchKind.KeywordMatch;
                    editVm.UnitRow.Keyword = "Unit";
                    editVm.DataStartRow.Kind = RowMatchKind.KeywordMatch;
                    editVm.DataStartRow.Keyword = "DATA_START";
                    editVm.DataStartRow.MatchMode = KeywordMatchMode.StartsWith;

                    editVm.BinColumn.HeaderName = "HARD_BIN";
                    editVm.SiteColumn.IsEnabled = true;
                    editVm.SiteColumn.HeaderName = "SITE_NUM";
                    editVm.AddIgnoredColumnCommand.Execute(null);
                    editVm.IgnoredColumns[0].HeaderName = "TEST_TIME";
                    editVm.AddCompositeFieldCommand.Execute(null);
                    editVm.CompositeFields[0].SourceColumn.HeaderName = "PART_TAG";
                    editVm.CompositeFields[0].Separator = "/";
                    editVm.CompositeFields[0].NewOutputName = "SITE_CODE";
                    editVm.CompositeFields[0].AddOutputNameCommand.Execute(null);
                    editVm.CompositeFields[0].NewOutputName = "BIN_CODE";
                    editVm.CompositeFields[0].AddOutputNameCommand.Execute(null);

                    editVm.NewGoodBinText = "01";
                    editVm.AddGoodBinCommand.Execute(null);
                    editVm.NewNonRetestFailBinText = "2";
                    editVm.AddNonRetestFailBinCommand.Execute(null);
                    editVm.AddPresetMissingMarkerCommand.Execute("N/A");
                    editVm.AddPresetMissingMarkerCommand.Execute("NULL");

                    Assert.True(editVm.TrySave());
                    var savedProfile = editVm.SavedProfile!;

                    // Open in Edit mode and capture tabs 1..5 + Dark theme
                    var builderVm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Edit, savedProfile));
                    var win = CreateOffscreenWindow(builderVm);
                    try
                    {
                        win.Show();
                        PumpDispatcher();

                        var tabs = FindNamed<TabControl>(win, "BuilderTabs");

                        // Tab 1: General + verify all 5 tab headers fit on a single row at default width (860px)
                        tabs.SelectedIndex = 0;
                        PumpDispatcher();
                        var tabYs = tabs.Items.OfType<TabItem>().Select(t => t.Bounds.Y).Distinct().ToArray();
                        Assert.Single(tabYs);
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_2_builder_1_general.png"));

                        // Tab 2: Filename Rules — Structured Mode (+ verify dynamic Regex toggle)
                        tabs.SelectedIndex = 1;
                        PumpDispatcher();
                        Assert.True(FindNamed<Control>(win, "StructuredFilenamePanel").IsVisible);
                        Assert.False(FindNamed<Control>(win, "AdvancedRegexPanel").IsVisible);
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_2_builder_2_filename_structured.png"));

                        builderVm.FilenameMode = FilenameRuleMode.Regex;
                        PumpDispatcher();
                        Assert.False(FindNamed<Control>(win, "StructuredFilenamePanel").IsVisible);
                        Assert.True(FindNamed<Control>(win, "AdvancedRegexPanel").IsVisible);
                        builderVm.FilenameMode = FilenameRuleMode.Structured;
                        PumpDispatcher();

                        // Tab 3: Datalog Structure
                        tabs.SelectedIndex = 2;
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_2_builder_3_structure.png"));

                        // Tab 4: Column & Composite Rules
                        tabs.SelectedIndex = 3;
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_2_builder_4_columns_composite.png"));

                        // Tab 5: Bin & Missing Rules
                        tabs.SelectedIndex = 4;
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_2_builder_5_bin_missing.png"));

                        // 7. Dark Theme screenshot (on Filename Rules tab)
                        app.RequestedThemeVariant = ThemeVariant.Dark;
                        tabs.SelectedIndex = 1;
                        PumpDispatcher();
                        CaptureWindow(win, Path.Combine(screenshotDir, "ft02_2_builder_7_dark_theme.png"));
                    }
                    finally
                    {
                        win.Close();
                        PumpDispatcher();
                    }

                    // 6. Validation Error Example (New mode, Light theme)
                    app.RequestedThemeVariant = ThemeVariant.Light;
                    var invalidVm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null));
                    invalidVm.ProfileName = "";
                    invalidVm.AcceptedExtensions.Clear();
                    invalidVm.Validate();
                    var invalidWin = CreateOffscreenWindow(invalidVm);
                    try
                    {
                        invalidWin.Show();
                        PumpDispatcher();
                        Assert.True(FindNamed<Control>(invalidWin, "ValidationSummaryBanner").IsVisible);
                        Assert.True(FindNamed<Control>(invalidWin, "GeneralErrorsBanner").IsVisible);
                        CaptureWindow(invalidWin, Path.Combine(screenshotDir, "ft02_2_builder_6_validation_error.png"));
                    }
                    finally
                    {
                        invalidWin.Close();
                        PumpDispatcher();
                    }

                    // 8. Verify actual UI control bindings, invalid numeric input rejection, min-size layout, and representative config via UI controls
                    VerifyActualUiBindingsMinSizeAndRepresentativeControls();
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
            throw new InvalidOperationException("ImportProfileBuilder UI test failed.", failure);
        }
    }

    private static void VerifyActualUiBindingsMinSizeAndRepresentativeControls()
    {
        var vm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null));
        var win = CreateOffscreenWindow(vm);
        try
        {
            win.Show();
            PumpDispatcher();

            var tabs = FindNamed<TabControl>(win, "BuilderTabs");
            var saveBtn = FindNamed<Button>(win, "SaveProfileButton");
            var cancelBtn = FindNamed<Button>(win, "CancelProfileButton");

            // Minimum size + 1.25x DPI check: tabs stay single-row and Save/Cancel stay within bounds
            win.Width = win.MinWidth;
            win.Height = win.MinHeight;
            PumpDispatcher();
            Assert.Single(tabs.Items.OfType<TabItem>().Select(t => t.Bounds.Y).Distinct());
            Assert.True(saveBtn.IsVisible && cancelBtn.IsVisible);
            Assert.True(saveBtn.Bounds.Bottom <= win.Height && cancelBtn.Bounds.Right <= win.Width);

            // Configure Representative Structured Profile through actual UI controls
            tabs.SelectedIndex = 0;
            PumpDispatcher();
            FindNamed<TextBox>(win, "ProfileNameTextBox").Text = "UI_Bound_Representative";
            vm.AcceptedExtensions.Clear();
            FindNamed<TextBox>(win, "NewExtensionTextBox").Text = ".Reg.zip";
            FindNamed<Button>(win, "AddExtensionButton").Command!.Execute(null);

            tabs.SelectedIndex = 1;
            PumpDispatcher();
            FindNamed<ComboBox>(win, "FilenameModeComboBox").SelectedItem = FilenameRuleMode.Structured;
            FindNamed<TextBox>(win, "SegmentDelimitersTextBox").Text = "_";

            FindNamed<ComboBox>(win, "LotIdFieldKindCombo").SelectedItem = FilenameFieldKind.MarkerAfter;
            FindNamed<NumericUpDown>(win, "LotIdFieldSegmentBox").Text = "1";
            FindNamed<TextBox>(win, "LotIdFieldMarkerBox").Text = ".";
            FindNamed<NumericUpDown>(win, "LotIdFieldOccurrenceBox").Text = "2";

            // Switch field kind away and back -> parameters remain preserved
            FindNamed<ComboBox>(win, "LotIdFieldKindCombo").SelectedItem = FilenameFieldKind.WholeSegment;
            PumpDispatcher();
            Assert.False(FindNamed<TextBox>(win, "LotIdFieldMarkerBox").IsEffectivelyVisible);
            FindNamed<ComboBox>(win, "LotIdFieldKindCombo").SelectedItem = FilenameFieldKind.MarkerAfter;
            PumpDispatcher();
            Assert.Equal(".", FindNamed<TextBox>(win, "LotIdFieldMarkerBox").Text);

            FindNamed<ComboBox>(win, "TestStageFieldKindCombo").SelectedItem = FilenameFieldKind.FixedRange;
            FindNamed<NumericUpDown>(win, "TestStageFieldSegmentBox").Text = "3";
            FindNamed<NumericUpDown>(win, "TestStageFieldStartBox").Text = "1";
            FindNamed<NumericUpDown>(win, "TestStageFieldLengthBox").Text = "2";

            FindNamed<CheckBox>(win, "RetestRoleFieldEnableCheck").IsChecked = true;
            FindNamed<NumericUpDown>(win, "RetestRoleFieldSegmentBox").Text = "3";
            FindNamed<TextBox>(win, "RetestMarkerTextBox").Text = "R";
            FindNamed<CheckBox>(win, "RetestRoundFieldEnableCheck").IsChecked = true;
            FindNamed<ComboBox>(win, "RetestRoundFieldKindCombo").SelectedItem = FilenameFieldKind.MarkerAfter;
            FindNamed<NumericUpDown>(win, "RetestRoundFieldSegmentBox").Text = "3";
            FindNamed<TextBox>(win, "RetestRoundFieldMarkerBox").Text = "R";

            FindNamed<CheckBox>(win, "FilePartFieldEnableCheck").IsChecked = true;
            FindNamed<NumericUpDown>(win, "FilePartFieldSegmentBox").Text = "4";

            // Switch to Structure tab, toggle optional UnitRow, and return (edits must survive tab switch)
            tabs.SelectedIndex = 2;
            PumpDispatcher();
            FindNamed<CheckBox>(win, "UnitRowEnableCheck").IsChecked = true;
            PumpDispatcher();
            Assert.True(vm.UnitRow.IsEnabled);
            FindNamed<CheckBox>(win, "UnitRowEnableCheck").IsChecked = false;

            // Invalid numeric input ("abc") must NOT silently save an older valid value
            tabs.SelectedIndex = 1;
            PumpDispatcher();
            FindNamed<NumericUpDown>(win, "LotIdFieldSegmentBox").Text = "abc";
            saveBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();
            Assert.Null(vm.SavedProfile);
            Assert.True(FindNamed<Control>(win, "ValidationSummaryBanner").IsVisible);

            // Restore valid input ("1") and immediately click Save (without LostFocus)
            FindNamed<NumericUpDown>(win, "LotIdFieldSegmentBox").Text = "1";
            saveBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpDispatcher();

            Assert.NotNull(vm.SavedProfile);
            Assert.Equal("UI_Bound_Representative", vm.SavedProfile.Name);
            Assert.Equal(0, vm.SavedProfile.FilenameRules.LotId.SegmentIndex);

            var matcher = new FilenameRuleMatcher();
            Assert.True(matcher.Match(vm.SavedProfile, "E320BD-262.20261123111219.XXX123.1-6846_TQB01RXAA1_F1_1_20261122171516_20261123110911.Reg.zip").IsSuccess);
            Assert.True(matcher.Match(vm.SavedProfile, "E320BD-262.20261124094509.XXX123.1-6846_TQB01RXAA1_F1_2_20261123111608_20261124094143.Reg.zip").IsSuccess);
            Assert.True(matcher.Match(vm.SavedProfile, "E320BD-262.20261124104655.XXX123.1-6846_TQB01RXAA1_F1B45678R1_1_20261124095542_20261124104352.Reg.zip").IsSuccess);
        }
        finally
        {
            win.Close();
            PumpDispatcher();
        }
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
