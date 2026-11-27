using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using YAT.Application.ImportProfiles;
using YAT.app.ViewModels;

namespace YAT.app.Views;

internal sealed class ImportProfileBuilderWindow : Window
{
    private static readonly FilenameRuleMode[] FilenameModes = Enum.GetValues<FilenameRuleMode>();
    private static readonly FilenameFieldKind[] FilenameFieldKinds = Enum.GetValues<FilenameFieldKind>();
    private static readonly DelimiterPreset[] DelimiterPresets = Enum.GetValues<DelimiterPreset>();
    private static readonly RowMatchKind[] RowMatchKinds = Enum.GetValues<RowMatchKind>();
    private static readonly KeywordMatchMode[] KeywordMatchModes = Enum.GetValues<KeywordMatchMode>();
    private static readonly ColumnSelectorKind[] ColumnSelectorKinds = Enum.GetValues<ColumnSelectorKind>();
    private static readonly MissingValuePolicy[] MissingPolicies = Enum.GetValues<MissingValuePolicy>();

    private readonly List<NumericUpDown> _numericInputs = [];

    public ImportProfileBuilderWindow(ImportProfileBuilderViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;

        Title = viewModel.WindowTitle;
        Width = 1220;
        Height = 720;
        MinWidth = 960;
        MinHeight = 580;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var previewPane = new ImportProfileSamplePreviewPane(this, viewModel.Preview);
        var topSampleBar = previewPane.BuildTopSampleBar();

        var tabs = new TabControl
        {
            Name = "BuilderTabs",
            Items =
            {
                new TabItem { Name = "GeneralTab", Header = "General", Content = WrapScroll(BuildGeneralTab(viewModel)) },
                new TabItem { Name = "FilenameTab", Header = "Filename", Content = WrapScroll(BuildFilenameTab(viewModel)) },
                new TabItem { Name = "StructureTab", Header = "Structure", Content = WrapScroll(BuildStructureTab(viewModel)) },
                new TabItem { Name = "ColumnsTab", Header = "Columns", Content = WrapScroll(BuildColumnsTab(viewModel)) },
                new TabItem { Name = "BinMissingTab", Header = "Bin & Missing", Content = WrapScroll(BuildBinMissingTab(viewModel)) }
            }
        };
        tabs.Styles.Add(new Style(x => x.OfType<TabItem>())
        {
            Setters =
            {
                new Setter(FontSizeProperty, 13.5),
                new Setter(PaddingProperty, new Thickness(10, 5)),
                new Setter(MinHeightProperty, 32.0)
            }
        });

        var splitter = new GridSplitter
        {
            Width = 6,
            ResizeDirection = GridResizeDirection.Columns
        };

        var splitGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("540,Auto,*"),
            ColumnSpacing = 6
        };
        splitGrid.Children.Add(tabs);
        Grid.SetColumn(splitter, 1);
        splitGrid.Children.Add(splitter);
        var rightPreview = previewPane.BuildPreviewPanel();
        Grid.SetColumn(rightPreview, 2);
        splitGrid.Children.Add(rightPreview);

        var validationBanner = CreateErrorBanner("ValidationSummaryBanner", "ValidationSummaryText", nameof(ImportProfileBuilderViewModel.ValidationSummary), viewModel);

        var scopeNote = new TextBlock
        {
            Text = viewModel.ValidationScopeNote,
            FontSize = 11.5,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        };

        var validateButton = new Button
        {
            Name = "ValidateProfileButton",
            Content = "Validate",
            MinWidth = 84,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        validateButton.Click += (_, _) =>
        {
            if (CommitAndValidateNumericInputs(viewModel))
            {
                viewModel.Validate();
            }
        };

        var saveButton = new Button
        {
            Name = "SaveProfileButton",
            Content = "Save",
            MinWidth = 88,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        saveButton.Classes.Add("accent");
        saveButton.Click += (_, _) =>
        {
            if (CommitAndValidateNumericInputs(viewModel) && viewModel.TrySave())
            {
                Close(viewModel.SavedProfile);
            }
        };

        var cancelButton = new Button
        {
            Name = "CancelProfileButton",
            Content = "Cancel",
            MinWidth = 88,
            IsCancel = true,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        cancelButton.Click += (_, _) => Close(null);

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { validateButton, saveButton, cancelButton }
        };

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        footerGrid.Children.Add(scopeNote);
        Grid.SetColumn(footerButtons, 1);
        footerGrid.Children.Add(footerButtons);

        var root = new Grid
        {
            Margin = new Thickness(14, 12),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 10
        };
        root.Children.Add(topSampleBar);
        Grid.SetRow(splitGrid, 1);
        root.Children.Add(splitGrid);
        Grid.SetRow(validationBanner, 2);
        root.Children.Add(validationBanner);
        Grid.SetRow(footerGrid, 3);
        root.Children.Add(footerGrid);

        Closed += (_, _) => viewModel.Dispose();

        Content = root;
    }

    public static Task<ImportProfile?> ShowAsync(Window owner, ImportProfileBuilderViewModel viewModel) =>
        new ImportProfileBuilderWindow(viewModel).ShowDialog<ImportProfile?>(owner);

    private static ScrollViewer WrapScroll(Control content) => new()
    {
        Content = new Border
        {
            Padding = new Thickness(4, 10, 12, 10),
            Child = content
        }
    };

    private Control BuildGeneralTab(ImportProfileBuilderViewModel vm)
    {
        var nameBox = new TextBox
        {
            Name = "ProfileNameTextBox",
            PlaceholderText = "e.g. FT_LineA_Standard",
            MaxWidth = 420,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 360
        };
        nameBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.ProfileName)) { Source = vm, Mode = BindingMode.TwoWay });

        var extInput = new TextBox
        {
            Name = "NewExtensionTextBox",
            PlaceholderText = ".Reg.zip, .csv, .txt",
            Width = 220
        };
        extInput.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.NewExtensionText)) { Source = vm, Mode = BindingMode.TwoWay });

        var addExtBtn = new Button { Name = "AddExtensionButton", Content = "Add Extension", Command = vm.AddExtensionCommand };

        var extList = new ItemsControl
        {
            Name = "AcceptedExtensionsList",
            ItemsSource = vm.AcceptedExtensions,
            ItemTemplate = new FuncDataTemplate<string>((ext, _) =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 2) };
                row.Children.Add(new TextBlock { Text = ext, VerticalAlignment = VerticalAlignment.Center, MinWidth = 140 });
                row.Children.Add(new Button
                {
                    Content = "Remove",
                    Command = vm.RemoveExtensionCommand,
                    CommandParameter = ext,
                    Padding = new Thickness(8, 2)
                });
                return row;
            })
        };

        var metaPanel = CreateCard(
            "Technical Identity (Read-Only)",
            new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = $"Profile ID: {vm.ProfileIdText}", Opacity = 0.75, FontSize = 12 },
                    new TextBlock { Text = $"Schema Version: {vm.SchemaVersionText}", Opacity = 0.75, FontSize = 12 }
                }
            });

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateErrorBanner("GeneralErrorsBanner", "GeneralErrorsText", nameof(ImportProfileBuilderViewModel.GeneralErrors), vm),
                CreateCard("Profile Name", new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "Display name shown in the Profile Manager and FT Import workflow.", Opacity = 0.72, FontSize = 12 },
                        nameBox
                    }
                }),
                CreateCard("Accepted File Extensions", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Supports simple (.csv, .txt) and compound (.Reg.zip) extensions.", Opacity = 0.72, FontSize = 12 },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { extInput, addExtBtn } },
                        extList
                    }
                }),
                metaPanel
            }
        };
    }

    private Control BuildFilenameTab(ImportProfileBuilderViewModel vm)
    {
        var modeCombo = new ComboBox
        {
            Name = "FilenameModeComboBox",
            ItemsSource = FilenameModes,
            Width = 200
        };
        modeCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(ImportProfileBuilderViewModel.FilenameMode)) { Source = vm, Mode = BindingMode.TwoWay });

        var delimBox = new TextBox { Name = "SegmentDelimitersTextBox", Width = 140, PlaceholderText = "e.g. _ or ._" };
        delimBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.SegmentDelimitersText)) { Source = vm, Mode = BindingMode.TwoWay });

        var origTokenBox = new TextBox { Name = "OriginalTokenTextBox", Width = 140, PlaceholderText = "Optional" };
        origTokenBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.OriginalToken)) { Source = vm, Mode = BindingMode.TwoWay });

        var retestMarkerBox = new TextBox { Name = "RetestMarkerTextBox", Width = 140, PlaceholderText = "e.g. R or RT" };
        retestMarkerBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.RetestMarker)) { Source = vm, Mode = BindingMode.TwoWay });

        var structuredPanel = new StackPanel
        {
            Name = "StructuredFilenamePanel",
            Spacing = 10,
            Children =
            {
                CreateCard("Segment Delimiters", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Delimiter character(s):", VerticalAlignment = VerticalAlignment.Center },
                        delimBox,
                        new TextBlock { Text = "(Each character splits the filename stem; segment positions below are 1-based)", Opacity = 0.7, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center }
                    }
                }),
                CreateFilenameFieldCard("LotIdField", vm.LotIdField),
                CreateFilenameFieldCard("TestStageField", vm.TestStageField),
                CreateCard("Retest Identification", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 12,
                            Children =
                            {
                                new TextBlock { Text = "Original Token:", VerticalAlignment = VerticalAlignment.Center },
                                origTokenBox,
                                new TextBlock { Text = "Retest Marker:", VerticalAlignment = VerticalAlignment.Center },
                                retestMarkerBox
                            }
                        },
                        CreateFilenameFieldEditor("RetestRoleField", vm.RetestRoleField),
                        CreateFilenameFieldEditor("RetestRoundField", vm.RetestRoundField)
                    }
                }),
                CreateFilenameFieldCard("FilePartField", vm.FilePartField)
            }
        };
        structuredPanel.Bind(IsVisibleProperty, new Binding(nameof(ImportProfileBuilderViewModel.IsStructuredMode)) { Source = vm });

        var regexBox = new TextBox
        {
            Name = "AdvancedRegexPatternTextBox",
            PlaceholderText = "Full-filename .NET regular expression with named groups",
            AcceptsReturn = false
        };
        regexBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.AdvancedRegexPattern)) { Source = vm, Mode = BindingMode.TwoWay });

        var regexPanel = CreateCard("Advanced Regex Mode", new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "Secondary mode for non-standard filenames. Structured settings remain preserved if you switch back.", Opacity = 0.72, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                regexBox
            }
        });
        regexPanel.Name = "AdvancedRegexPanel";
        regexPanel.Bind(IsVisibleProperty, new Binding(nameof(ImportProfileBuilderViewModel.IsRegexMode)) { Source = vm });

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateErrorBanner("FilenameErrorsBanner", "FilenameErrorsText", nameof(ImportProfileBuilderViewModel.FilenameErrors), vm),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Filename Matching Mode:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold },
                        modeCombo
                    }
                },
                structuredPanel,
                regexPanel
            }
        };
    }

    private Control BuildStructureTab(ImportProfileBuilderViewModel vm)
    {
        var delimCombo = new ComboBox
        {
            Name = "DelimiterPresetComboBox",
            ItemsSource = DelimiterPresets,
            Width = 160
        };
        delimCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(ImportProfileBuilderViewModel.DelimiterPreset)) { Source = vm, Mode = BindingMode.TwoWay });

        var customDelimBox = new TextBox { Name = "CustomDelimiterTextBox", Width = 120, PlaceholderText = "Custom" };
        customDelimBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.CustomDelimiter)) { Source = vm, Mode = BindingMode.TwoWay });
        customDelimBox.Bind(IsVisibleProperty, new Binding(nameof(ImportProfileBuilderViewModel.ShowCustomDelimiter)) { Source = vm });

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                CreateErrorBanner("StructureErrorsBanner", "StructureErrorsText", nameof(ImportProfileBuilderViewModel.StructureErrors), vm),
                CreateCard("Field Delimiter", new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = "Delimiter:", VerticalAlignment = VerticalAlignment.Center },
                        delimCombo,
                        customDelimBox
                    }
                }),
                CreateRowIdentifierCard("HeaderRow", vm.HeaderRow),
                CreateRowIdentifierCard("DataStartRow", vm.DataStartRow),
                CreateRowIdentifierCard("UnitRow", vm.UnitRow),
                CreateRowIdentifierCard("TargetRow", vm.TargetRow),
                CreateRowIdentifierCard("LslRow", vm.LslRow),
                CreateRowIdentifierCard("UslRow", vm.UslRow)
            }
        };
    }

    private Control BuildColumnsTab(ImportProfileBuilderViewModel vm)
    {
        var ignoredItems = new ItemsControl
        {
            Name = "IgnoredColumnsList",
            ItemsSource = vm.IgnoredColumns,
            ItemTemplate = new FuncDataTemplate<ColumnSelectorDraft>((draft, _) =>
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 3) };
                row.Children.Add(CreateColumnSelectorInline(draft));
                row.Children.Add(new Button
                {
                    Content = "Remove",
                    Command = vm.RemoveIgnoredColumnCommand,
                    CommandParameter = draft,
                    Padding = new Thickness(8, 2)
                });
                return row;
            })
        };

        var compositeItems = new ItemsControl
        {
            Name = "CompositeFieldsList",
            ItemsSource = vm.CompositeFields,
            ItemTemplate = new FuncDataTemplate<CompositeFieldDraft>((draft, _) =>
            {
                var sepBox = new TextBox { Width = 80, PlaceholderText = "/" };
                sepBox.Bind(TextBox.TextProperty, new Binding(nameof(CompositeFieldDraft.Separator)) { Source = draft, Mode = BindingMode.TwoWay });

                var newOutBox = new TextBox { Width = 180, PlaceholderText = "New output column name" };
                newOutBox.Bind(TextBox.TextProperty, new Binding(nameof(CompositeFieldDraft.NewOutputName)) { Source = draft, Mode = BindingMode.TwoWay });

                var outputsList = new ItemsControl
                {
                    ItemsSource = draft.OutputNames,
                    ItemTemplate = new FuncDataTemplate<CompositeOutputNameItem>((item, _) =>
                    {
                        var nameBox = new TextBox { Width = 180 };
                        nameBox.Bind(TextBox.TextProperty, new Binding(nameof(CompositeOutputNameItem.Name)) { Source = item, Mode = BindingMode.TwoWay });
                        return new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Margin = new Thickness(0, 2),
                            Children =
                            {
                                nameBox,
                                new Button { Content = "Up", Command = draft.MoveOutputUpCommand, CommandParameter = item, Padding = new Thickness(6, 2) },
                                new Button { Content = "Down", Command = draft.MoveOutputDownCommand, CommandParameter = item, Padding = new Thickness(6, 2) },
                                new Button { Content = "Remove", Command = draft.RemoveOutputNameCommand, CommandParameter = item, Padding = new Thickness(6, 2) }
                            }
                        };
                    })
                };

                return CreateCard("Composite Field Rule", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children =
                            {
                                CreateColumnSelectorInline(draft.SourceColumn),
                                new TextBlock { Text = "Separator:", VerticalAlignment = VerticalAlignment.Center },
                                sepBox,
                                new Button { Content = "Remove Rule", Command = vm.RemoveCompositeFieldCommand, CommandParameter = draft }
                            }
                        },
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children =
                            {
                                new TextBlock { Text = "Outputs (ordered):", VerticalAlignment = VerticalAlignment.Center },
                                newOutBox,
                                new Button { Content = "Add Output", Command = draft.AddOutputNameCommand }
                            }
                        },
                        outputsList
                    }
                });
            })
        };

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateErrorBanner("ColumnsErrorsBanner", "ColumnsErrorsText", nameof(ImportProfileBuilderViewModel.ColumnsErrors), vm),
                CreateCard("Bin Column (Required)", CreateColumnSelectorInline(vm.BinColumn)),
                CreateCard("Site Column (Optional)", CreateOptionalColumnSelector(vm.SiteColumn)),
                CreateCard("Ignored Columns", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new Button { Name = "AddIgnoredColumnButton", Content = "Add Ignored Column", Command = vm.AddIgnoredColumnCommand, HorizontalAlignment = HorizontalAlignment.Left },
                        ignoredItems
                    }
                }),
                CreateCard("Composite Field Rules", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Splits a single source column into ordered named output columns (non-recursive).", Opacity = 0.72, FontSize = 12 },
                        new Button { Name = "AddCompositeFieldButton", Content = "Add Composite Rule", Command = vm.AddCompositeFieldCommand, HorizontalAlignment = HorizontalAlignment.Left },
                        compositeItems
                    }
                })
            }
        };
    }

    private Control BuildBinMissingTab(ImportProfileBuilderViewModel vm)
    {
        var goodInput = new TextBox { Name = "NewGoodBinTextBox", Width = 140, PlaceholderText = "e.g. 1, 01, 001" };
        goodInput.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.NewGoodBinText)) { Source = vm, Mode = BindingMode.TwoWay });
        var goodList = CreateStringTokenList(vm.GoodBins, vm.RemoveGoodBinCommand);

        var failInput = new TextBox { Name = "NewNonRetestFailBinTextBox", Width = 140, PlaceholderText = "e.g. 2, 05" };
        failInput.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.NewNonRetestFailBinText)) { Source = vm, Mode = BindingMode.TwoWay });
        var failList = CreateStringTokenList(vm.NonRetestFailBins, vm.RemoveNonRetestFailBinCommand);

        var blankCheck = new CheckBox
        {
            Name = "IncludeBlankMissingMarkerCheckBox",
            Content = "Treat Blank (empty cell) as Missing"
        };
        blankCheck.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(ImportProfileBuilderViewModel.IncludeBlankMissingMarker)) { Source = vm, Mode = BindingMode.TwoWay });

        var markerInput = new TextBox { Name = "NewMissingMarkerTextBox", Width = 160, PlaceholderText = "Custom marker" };
        markerInput.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileBuilderViewModel.NewMissingMarkerText)) { Source = vm, Mode = BindingMode.TwoWay });
        var markerList = CreateStringTokenList(vm.CustomMissingMarkers, vm.RemoveMissingMarkerCommand);

        var policyCombo = new ComboBox
        {
            Name = "MissingPolicyComboBox",
            ItemsSource = MissingPolicies,
            Width = 180
        };
        policyCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(ImportProfileBuilderViewModel.MissingPolicy)) { Source = vm, Mode = BindingMode.TwoWay });

        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                CreateErrorBanner("BinMissingErrorsBanner", "BinMissingErrorsText", nameof(ImportProfileBuilderViewModel.BinMissingErrors), vm),
                CreateCard("Good Bins (Exact String; Leading Zeros Preserved)", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Values are matched as exact strings (\"1\", \"01\", and \"001\" remain distinct).", Opacity = 0.72, FontSize = 12 },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { goodInput, new Button { Name = "AddGoodBinButton", Content = "Add Good Bin", Command = vm.AddGoodBinCommand } } },
                        goodList
                    }
                }),
                CreateCard("Non-Retest Fail Bins (Exact String)", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { failInput, new Button { Name = "AddNonRetestFailBinButton", Content = "Add Fail Bin", Command = vm.AddNonRetestFailBinCommand } } },
                        failList
                    }
                }),
                CreateCard("Missing Value Rules", new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 10,
                            Children =
                            {
                                new TextBlock { Text = "Missing Value Policy:", VerticalAlignment = VerticalAlignment.Center },
                                policyCombo
                            }
                        },
                        new TextBlock { Text = vm.MissingPolicyNote, Opacity = 0.72, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                        blankCheck,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 6,
                            Children =
                            {
                                new TextBlock { Text = "Quick Add:", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 },
                                new Button { Content = "N/A", Command = vm.AddPresetMissingMarkerCommand, CommandParameter = "N/A", Padding = new Thickness(8, 2) },
                                new Button { Content = "NULL", Command = vm.AddPresetMissingMarkerCommand, CommandParameter = "NULL", Padding = new Thickness(8, 2) },
                                new Button { Content = "*****", Command = vm.AddPresetMissingMarkerCommand, CommandParameter = "*****", Padding = new Thickness(8, 2) }
                            }
                        },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { markerInput, new Button { Name = "AddMissingMarkerButton", Content = "Add Marker", Command = vm.AddMissingMarkerCommand } } },
                        markerList
                    }
                })
            }
        };
    }

    private Control CreateFilenameFieldCard(string namePrefix, FilenameFieldDraft draft) =>
        CreateCard(draft.Label, CreateFilenameFieldEditor(namePrefix, draft));

    private Control CreateFilenameFieldEditor(string namePrefix, FilenameFieldDraft draft)
    {
        var kindCombo = new ComboBox { Name = $"{namePrefix}KindCombo", ItemsSource = FilenameFieldKinds, Width = 150 };
        kindCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(FilenameFieldDraft.Kind)) { Source = draft, Mode = BindingMode.TwoWay });

        var segBox = CreateIntBox($"{namePrefix}SegmentBox", nameof(FilenameFieldDraft.SegmentPosition), draft);
        var segGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = "Segment # (1-based):", VerticalAlignment = VerticalAlignment.Center }, segBox }
        };
        segGroup.Bind(IsVisibleProperty, new Binding(nameof(FilenameFieldDraft.ShowSegmentPosition)) { Source = draft });

        var markerBox = new TextBox { Name = $"{namePrefix}MarkerBox", Width = 80, PlaceholderText = "e.g. ." };
        markerBox.Bind(TextBox.TextProperty, new Binding(nameof(FilenameFieldDraft.Marker)) { Source = draft, Mode = BindingMode.TwoWay });
        var occBox = CreateIntBox($"{namePrefix}OccurrenceBox", nameof(FilenameFieldDraft.MarkerOccurrence), draft);
        var markerGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Marker:", VerticalAlignment = VerticalAlignment.Center },
                markerBox,
                new TextBlock { Text = "Occurrence #:", VerticalAlignment = VerticalAlignment.Center },
                occBox
            }
        };
        markerGroup.Bind(IsVisibleProperty, new Binding(nameof(FilenameFieldDraft.ShowMarker)) { Source = draft });

        var startBox = CreateIntBox($"{namePrefix}StartBox", nameof(FilenameFieldDraft.StartPosition), draft);
        var lenBox = CreateIntBox($"{namePrefix}LengthBox", nameof(FilenameFieldDraft.Length), draft);
        var rangeGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Start Char # (1-based):", VerticalAlignment = VerticalAlignment.Center },
                startBox,
                new TextBlock { Text = "Length:", VerticalAlignment = VerticalAlignment.Center },
                lenBox
            }
        };
        rangeGroup.Bind(IsVisibleProperty, new Binding(nameof(FilenameFieldDraft.ShowFixedRange)) { Source = draft });

        var patBox = new TextBox { Name = $"{namePrefix}PatternBox", Width = 240, PlaceholderText = "Regex capture pattern" };
        patBox.Bind(TextBox.TextProperty, new Binding(nameof(FilenameFieldDraft.Pattern)) { Source = draft, Mode = BindingMode.TwoWay });
        var regexGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = "Pattern:", VerticalAlignment = VerticalAlignment.Center }, patBox }
        };
        regexGroup.Bind(IsVisibleProperty, new Binding(nameof(FilenameFieldDraft.ShowRegexCapture)) { Source = draft });

        var controlsRow = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 10,
            LineSpacing = 6,
            Children = { kindCombo, segGroup, markerGroup, rangeGroup, regexGroup }
        };

        if (draft.IsRequired)
        {
            return controlsRow;
        }

        var enableCheck = new CheckBox { Name = $"{namePrefix}EnableCheck", Content = $"Enable {draft.Label}" };
        enableCheck.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(FilenameFieldDraft.IsEnabled)) { Source = draft, Mode = BindingMode.TwoWay });
        controlsRow.Bind(IsVisibleProperty, new Binding(nameof(FilenameFieldDraft.IsEnabled)) { Source = draft });
        return new StackPanel { Spacing = 6, Children = { enableCheck, controlsRow } };
    }

    private Control CreateRowIdentifierCard(string namePrefix, RowIdentifierDraft draft)
    {
        var kindCombo = new ComboBox { Name = $"{namePrefix}KindCombo", ItemsSource = RowMatchKinds, Width = 150 };
        kindCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(RowIdentifierDraft.Kind)) { Source = draft, Mode = BindingMode.TwoWay });

        var rowNumBox = CreateIntBox($"{namePrefix}RowNumberBox", nameof(RowIdentifierDraft.RowNumber), draft);
        var fixedGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = "Row # (1-based):", VerticalAlignment = VerticalAlignment.Center }, rowNumBox }
        };
        fixedGroup.Bind(IsVisibleProperty, new Binding(nameof(RowIdentifierDraft.ShowFixedRow)) { Source = draft });

        var keywordBox = new TextBox { Name = $"{namePrefix}KeywordBox", Width = 150, PlaceholderText = "Keyword" };
        keywordBox.Bind(TextBox.TextProperty, new Binding(nameof(RowIdentifierDraft.Keyword)) { Source = draft, Mode = BindingMode.TwoWay });
        var matchCombo = new ComboBox { Name = $"{namePrefix}MatchModeCombo", ItemsSource = KeywordMatchModes, Width = 120 };
        matchCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(RowIdentifierDraft.MatchMode)) { Source = draft, Mode = BindingMode.TwoWay });
        var anyColCheck = new CheckBox { Name = $"{namePrefix}AnyColCheck", Content = "Any Column" };
        anyColCheck.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(RowIdentifierDraft.SearchAnyColumn)) { Source = draft, Mode = BindingMode.TwoWay });
        var colPosBox = CreateIntBox($"{namePrefix}ColPosBox", nameof(RowIdentifierDraft.ColumnPosition), draft);
        var colPosGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = "Col # (1-based):", VerticalAlignment = VerticalAlignment.Center }, colPosBox }
        };
        colPosGroup.Bind(IsVisibleProperty, new Binding(nameof(RowIdentifierDraft.ShowColumnPosition)) { Source = draft });

        var kwGroup = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 8,
            LineSpacing = 6,
            Children = { keywordBox, matchCombo, anyColCheck, colPosGroup }
        };
        kwGroup.Bind(IsVisibleProperty, new Binding(nameof(RowIdentifierDraft.ShowKeyword)) { Source = draft });

        var body = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemSpacing = 10,
            LineSpacing = 6,
            Children = { kindCombo, fixedGroup, kwGroup }
        };

        if (draft.IsRequired)
        {
            return CreateCard(draft.Label, body);
        }

        var enableCheck = new CheckBox { Name = $"{namePrefix}EnableCheck", Content = $"Enable {draft.Label}" };
        enableCheck.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(RowIdentifierDraft.IsEnabled)) { Source = draft, Mode = BindingMode.TwoWay });
        body.Bind(IsVisibleProperty, new Binding(nameof(RowIdentifierDraft.IsEnabled)) { Source = draft });
        return CreateCard(draft.Label, new StackPanel { Spacing = 6, Children = { enableCheck, body } });
    }

    private Control CreateOptionalColumnSelector(ColumnSelectorDraft draft)
    {
        var check = new CheckBox { Name = "SiteColumnEnableCheck", Content = "Enable Site Column" };
        check.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(ColumnSelectorDraft.IsEnabled)) { Source = draft, Mode = BindingMode.TwoWay });
        var inline = CreateColumnSelectorInline(draft);
        inline.Bind(IsVisibleProperty, new Binding(nameof(ColumnSelectorDraft.IsEnabled)) { Source = draft });
        return new StackPanel { Spacing = 6, Children = { check, inline } };
    }

    private Control CreateColumnSelectorInline(ColumnSelectorDraft draft)
    {
        var kindCombo = new ComboBox { ItemsSource = ColumnSelectorKinds, Width = 130 };
        kindCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(ColumnSelectorDraft.Kind)) { Source = draft, Mode = BindingMode.TwoWay });

        var headerBox = new TextBox { Width = 180, PlaceholderText = "Exact Header Name" };
        headerBox.Bind(TextBox.TextProperty, new Binding(nameof(ColumnSelectorDraft.HeaderName)) { Source = draft, Mode = BindingMode.TwoWay });
        headerBox.Bind(IsVisibleProperty, new Binding(nameof(ColumnSelectorDraft.ShowHeaderName)) { Source = draft });

        var colPosBox = CreateIntBox(null, nameof(ColumnSelectorDraft.ColumnPosition), draft);
        var posGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { new TextBlock { Text = "Column # (1-based):", VerticalAlignment = VerticalAlignment.Center }, colPosBox }
        };
        posGroup.Bind(IsVisibleProperty, new Binding(nameof(ColumnSelectorDraft.ShowColumnPosition)) { Source = draft });

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { kindCombo, headerBox, posGroup }
        };
    }

    private static ItemsControl CreateStringTokenList(
        System.Collections.IEnumerable items,
        System.Windows.Input.ICommand removeCommand) => new()
    {
        ItemsSource = items,
        ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 6, LineSpacing = 6 }),
        ItemTemplate = new FuncDataTemplate<string>((val, _) =>
        {
            var chip = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = val, VerticalAlignment = VerticalAlignment.Center },
                        new Button { Content = "×", Command = removeCommand, CommandParameter = val, Padding = new Thickness(4, 0), MinHeight = 0 }
                    }
                }
            };
            chip.Bind(Border.BorderBrushProperty, chip.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
            return chip;
        })
    };

    private NumericUpDown CreateIntBox(string? name, string propertyName, object source)
    {
        var box = new NumericUpDown
        {
            Name = name,
            Minimum = 1,
            Maximum = 100_000,
            Increment = 1,
            FormatString = "0",
            Width = 100
        };
        box.Bind(NumericUpDown.ValueProperty, new Binding(propertyName)
        {
            Source = source,
            Mode = BindingMode.TwoWay,
            Converter = IntDecimalConverter.Instance
        });
        _numericInputs.Add(box);
        return box;
    }

    private bool CommitAndValidateNumericInputs(ImportProfileBuilderViewModel viewModel)
    {
        foreach (var box in _numericInputs)
        {
            if (!IsRuleControlActive(box))
            {
                continue;
            }

            var rawText = box.Text?.Trim();
            if (!int.TryParse(rawText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1
                || parsed > 100_000)
            {
                var label = string.IsNullOrEmpty(box.Name) ? "Numeric position" : box.Name;
                viewModel.SetInputError($"Invalid numeric index input '{rawText ?? string.Empty}' in {label}: enter a whole number >= 1.");
                return false;
            }

            box.Value = parsed;
        }

        return true;
    }

    private static bool IsRuleControlActive(Control control)
    {
        Control? current = control;
        while (current is not null && current is not TabItem)
        {
            if (!current.IsVisible)
            {
                return false;
            }

            current = current.Parent as Control;
        }

        return true;
    }

    private Border CreateCard(string header, Control content)
    {
        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = header, FontWeight = FontWeight.SemiBold, FontSize = 13 },
                content
            }
        };
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(12, 10),
            Child = panel
        };
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        return border;
    }

    private static Border CreateErrorBanner(string bannerName, string textName, string propertyName, object source)
    {
        var text = new TextBlock
        {
            Name = textName,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        text.Bind(TextBlock.TextProperty, new Binding(propertyName) { Source = source });

        var banner = new Border
        {
            Name = bannerName,
            Padding = new Thickness(10, 7),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xD1, 0x34, 0x38)),
            Child = text
        };
        banner.Bind(IsVisibleProperty, new Binding(propertyName)
        {
            Source = source,
            Converter = StringConverters.IsNotNullOrEmpty
        });
        return banner;
    }

    private sealed class IntDecimalConverter : IValueConverter
    {
        public static IntDecimalConverter Instance { get; } = new();

        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is int i ? (decimal?)i : 1m;

        public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) =>
            value is decimal d ? Math.Max(1, (int)decimal.Truncate(d)) : 1;
    }
}
