using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.ViewModels;

namespace YAT.app.Views;

internal sealed class FtImportWizardWindow : Window
{
    private readonly FtImportWizardViewModel _viewModel;
    private FtStreamingImportResult? _resultToReturn;

    public FtImportWizardWindow(FtImportWizardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;

        Title = "FT Datalog Import Wizard";
        Width = 940;
        Height = 640;
        MinWidth = 780;
        MinHeight = 520;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var stepHeader = CreateStepHeader(viewModel);
        var step1 = CreateStep1View(viewModel);
        var step2 = CreateStep2View(viewModel);
        var step3 = CreateStep3View(viewModel);

        step1.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.IsStep1)) { Source = viewModel });
        step2.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.IsStep2)) { Source = viewModel });
        step3.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.IsStep3)) { Source = viewModel });

        var bodyHost = new Grid { Children = { step1, step2, step3 } };
        var footer = CreateFooter(viewModel);

        var root = new Grid
        {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12
        };
        root.Children.Add(stepHeader);
        Grid.SetRow(bodyHost, 1);
        root.Children.Add(bodyHost);
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        Content = root;

        viewModel.RequestClose += result =>
        {
            _resultToReturn = result;
            Close(result);
        };

        Closing += (_, e) =>
        {
            if (viewModel.IsImporting)
            {
                e.Cancel = true;
            }
        };

        Closed += (_, _) => viewModel.Dispose();
        Opened += async (_, _) => await viewModel.InitializeAsync();
    }

    private Control CreateStepHeader(FtImportWizardViewModel viewModel)
    {
        var title = new TextBlock
        {
            Name = "WizardStepTitle",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold
        };
        title.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.StepTitle)) { Source = viewModel });

        var status = new TextBlock
        {
            Name = "WizardStatusText",
            FontSize = 12,
            Opacity = 0.75,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        status.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.StatusMessage)) { Source = viewModel });

        var header = new Border
        {
            Padding = new Thickness(12, 10),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children = { title, status }
            }
        };
        header.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        header.Bind(Border.BackgroundProperty, this.GetResourceObservable("SystemControlBackgroundChromeMediumLowBrush"));
        return header;
    }

    private Control CreateStep1View(FtImportWizardViewModel viewModel)
    {
        var profileCombo = new ComboBox
        {
            Name = "ProfileComboBox",
            MinWidth = 240,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = viewModel.Profiles,
            ItemTemplate = new FuncDataTemplate<ImportProfile>((p, _) => new TextBlock
            {
                Text = p?.Name ?? string.Empty,
                TextTrimming = TextTrimming.CharacterEllipsis
            })
        };
        profileCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(FtImportWizardViewModel.SelectedProfile))
        {
            Source = viewModel,
            Mode = BindingMode.TwoWay
        });

        var stageCombo = new ComboBox
        {
            Name = "StageComboBox",
            MinWidth = 140,
            PlaceholderText = "Select stage",
            ItemsSource = viewModel.AvailableStages
        };
        stageCombo.Bind(ComboBox.SelectedItemProperty, new Binding(nameof(FtImportWizardViewModel.SelectedStage))
        {
            Source = viewModel,
            Mode = BindingMode.TwoWay
        });

        var addFilesBtn = new Button { Name = "AddFilesButton", Content = "Add Files...", Command = viewModel.AddFilesCommand };
        var addFolderBtn = new Button { Name = "AddFolderButton", Content = "Add Folder...", Command = viewModel.AddFolderCommand };
        var removeBtn = new Button { Name = "RemoveSourcesButton", Content = "Remove", Command = viewModel.RemoveSelectedSourcesCommand };
        var clearBtn = new Button { Name = "ClearSourcesButton", Content = "Clear", Command = viewModel.ClearSourcesCommand };

        var controlsBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto,Auto,Auto"),
            ColumnSpacing = 8,
            Children =
            {
                new TextBlock { Text = "Import Profile:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium },
            }
        };
        Grid.SetColumn(profileCombo, 1);
        controlsBar.Children.Add(profileCombo);

        var stageLabel = new TextBlock { Text = "Stage:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.Medium, Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(stageLabel, 2);
        controlsBar.Children.Add(stageLabel);
        Grid.SetColumn(stageCombo, 3);
        controlsBar.Children.Add(stageCombo);
        Grid.SetColumn(addFilesBtn, 4);
        controlsBar.Children.Add(addFilesBtn);
        Grid.SetColumn(addFolderBtn, 5);
        controlsBar.Children.Add(addFolderBtn);
        Grid.SetColumn(removeBtn, 6);
        controlsBar.Children.Add(removeBtn);
        Grid.SetColumn(clearBtn, 7);
        controlsBar.Children.Add(clearBtn);

        var lotItemsControl = new ItemsControl
        {
            Name = "LotGroupsItemsControl",
            ItemsSource = viewModel.LotGroups,
            ItemTemplate = new FuncDataTemplate<FtWizardLotGroupItem>((lot, _) =>
            {
                var lotCheck = new CheckBox
                {
                    IsThreeState = true,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lotCheck.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(FtWizardLotGroupItem.SelectionState)) { Mode = BindingMode.TwoWay });

                var lotName = new TextBlock
                {
                    FontWeight = FontWeight.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lotName.Bind(TextBlock.TextProperty, new Binding(nameof(FtWizardLotGroupItem.LotId)));

                var countText = new TextBlock
                {
                    FontSize = 11,
                    Opacity = 0.7,
                    VerticalAlignment = VerticalAlignment.Center
                };
                countText.Bind(TextBlock.TextProperty, new Binding(nameof(FtWizardLotGroupItem.FileCountText)));

                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { lotCheck, lotName, countText }
                };

                var filesList = new ListBox
                {
                    ItemsSource = lot?.Files,
                    SelectionMode = SelectionMode.Single,
                    ItemTemplate = new FuncDataTemplate<FtWizardFileItem>((f, _) =>
                    {
                        var fileCheck = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
                        fileCheck.Bind(CheckBox.IsCheckedProperty, new Binding(nameof(FtWizardFileItem.IsSelected)) { Mode = BindingMode.TwoWay });

                        var nameText = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                        nameText.Bind(TextBlock.TextProperty, new Binding(nameof(FtWizardFileItem.FileName)));

                        var stageText = new TextBlock { FontSize = 11, Opacity = 0.75, Width = 54, VerticalAlignment = VerticalAlignment.Center };
                        stageText.Bind(TextBlock.TextProperty, new Binding(nameof(FtWizardFileItem.TestStage)));

                        var roleText = new TextBlock { FontSize = 11, Opacity = 0.85, Width = 68, VerticalAlignment = VerticalAlignment.Center };
                        roleText.Bind(TextBlock.TextProperty, new Binding(nameof(FtWizardFileItem.RoleDisplay)));

                        var roundText = new TextBlock { FontSize = 11, Opacity = 0.75, Width = 40, VerticalAlignment = VerticalAlignment.Center };
                        roundText.Bind(TextBlock.TextProperty, new Binding(nameof(FtWizardFileItem.RoundDisplay)));

                        var partText = new TextBlock { FontSize = 11, Opacity = 0.75, Width = 54, VerticalAlignment = VerticalAlignment.Center };
                        partText.Bind(TextBlock.TextProperty, new Binding(nameof(FtWizardFileItem.PartDisplay)));

                        var rowGrid = new Grid
                        {
                            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto"),
                            ColumnSpacing = 8
                        };
                        rowGrid.Children.Add(fileCheck);
                        Grid.SetColumn(nameText, 1);
                        rowGrid.Children.Add(nameText);
                        Grid.SetColumn(stageText, 2);
                        rowGrid.Children.Add(stageText);
                        Grid.SetColumn(roleText, 3);
                        rowGrid.Children.Add(roleText);
                        Grid.SetColumn(roundText, 4);
                        rowGrid.Children.Add(roundText);
                        Grid.SetColumn(partText, 5);
                        rowGrid.Children.Add(partText);
                        return rowGrid;
                    })
                };
                filesList.SelectionChanged += (_, _) =>
                {
                    viewModel.SelectedFileItem = filesList.SelectedItem as FtWizardFileItem;
                    viewModel.SelectedLotGroup = lot;
                    viewModel.NotifyStep1SelectionHighlightChanged();
                };

                var expander = new Expander
                {
                    Header = headerPanel,
                    Content = filesList,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 4)
                };
                expander.Bind(Expander.IsExpandedProperty, new Binding(nameof(FtWizardLotGroupItem.IsExpanded)) { Mode = BindingMode.TwoWay });
                return expander;
            })
        };

        var filesHost = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8),
            Child = new ScrollViewer { Content = lotItemsControl }
        };
        filesHost.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var diagnosticsPanel = CreateDiagnosticsList("Step1DiagnosticsList", viewModel.Step1Diagnostics, nameof(FtImportWizardViewModel.HasStep1Diagnostics), viewModel);

        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 10
        };
        grid.Children.Add(controlsBar);
        Grid.SetRow(filesHost, 1);
        grid.Children.Add(filesHost);
        Grid.SetRow(diagnosticsPanel, 2);
        grid.Children.Add(diagnosticsPanel);
        return grid;
    }

    private Control CreateStep2View(FtImportWizardViewModel viewModel)
    {
        var availCountText = new TextBlock { FontWeight = FontWeight.SemiBold };
        availCountText.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.AvailableCount))
        {
            Source = viewModel,
            StringFormat = "Available Test Items ({0})"
        });

        var availSearch = new TextBox
        {
            Name = "AvailableSearchBox",
            PlaceholderText = "Search available items..."
        };
        availSearch.Bind(TextBox.TextProperty, new Binding(nameof(FtImportWizardViewModel.AvailableSearchText))
        {
            Source = viewModel,
            Mode = BindingMode.TwoWay
        });

        var availList = new ListBox
        {
            Name = "AvailableItemsList",
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = viewModel.FilteredAvailableHeaders
        };
        availList.SelectionChanged += (_, _) =>
        {
            viewModel.SelectedAvailableHighlights.Clear();
            if (availList.SelectedItems is not null)
            {
                foreach (var item in availList.SelectedItems)
                {
                    if (item is string header)
                    {
                        viewModel.SelectedAvailableHighlights.Add(header);
                    }
                }
            }

            viewModel.NotifySelectionHighlightsChanged();
        };

        var availHost = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = availList
        };
        availHost.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var leftGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 6 };
        leftGrid.Children.Add(availCountText);
        Grid.SetRow(availSearch, 1);
        leftGrid.Children.Add(availSearch);
        Grid.SetRow(availHost, 2);
        leftGrid.Children.Add(availHost);

        var addBtn = new Button { Name = "AddItemsButton", Content = "Add >", Command = viewModel.AddSelectedItemsCommand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var removeBtn = new Button { Name = "RemoveItemsButton", Content = "< Remove", Command = viewModel.RemoveSelectedItemsCommand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var selectAllBtn = new Button { Name = "SelectAllItemsButton", Content = "Select All >>", Command = viewModel.SelectAllItemsCommand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var clearAllBtn = new Button { Name = "ClearAllItemsButton", Content = "<< Clear All", Command = viewModel.ClearAllItemsCommand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
        var rescanBtn = new Button { Name = "RescanSchemaButton", Content = "Rescan", Command = viewModel.RescanSchemaCommand, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };

        var transferPanel = new StackPanel
        {
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Width = 120,
            Children = { addBtn, removeBtn, selectAllBtn, clearAllBtn, rescanBtn }
        };

        var selCountText = new TextBlock { FontWeight = FontWeight.SemiBold };
        selCountText.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.SelectedCount))
        {
            Source = viewModel,
            StringFormat = "Selected Test Items ({0})"
        });

        var selSearch = new TextBox
        {
            Name = "SelectedSearchBox",
            PlaceholderText = "Search selected items..."
        };
        selSearch.Bind(TextBox.TextProperty, new Binding(nameof(FtImportWizardViewModel.SelectedSearchText))
        {
            Source = viewModel,
            Mode = BindingMode.TwoWay
        });

        var selList = new ListBox
        {
            Name = "SelectedItemsList",
            SelectionMode = SelectionMode.Multiple,
            ItemsSource = viewModel.FilteredSelectedHeaders
        };
        selList.SelectionChanged += (_, _) =>
        {
            viewModel.SelectedChosenHighlights.Clear();
            if (selList.SelectedItems is not null)
            {
                foreach (var item in selList.SelectedItems)
                {
                    if (item is string header)
                    {
                        viewModel.SelectedChosenHighlights.Add(header);
                    }
                }
            }

            viewModel.NotifySelectionHighlightsChanged();
        };

        var selHost = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = selList
        };
        selHost.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var rightGrid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 6 };
        rightGrid.Children.Add(selCountText);
        Grid.SetRow(selSearch, 1);
        rightGrid.Children.Add(selSearch);
        Grid.SetRow(selHost, 2);
        rightGrid.Children.Add(selHost);

        var dualGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,*"),
            ColumnSpacing = 12
        };
        dualGrid.Children.Add(leftGrid);
        Grid.SetColumn(transferPanel, 1);
        dualGrid.Children.Add(transferPanel);
        Grid.SetColumn(rightGrid, 2);
        dualGrid.Children.Add(rightGrid);

        var diagnosticsPanel = CreateDiagnosticsList("Step2DiagnosticsList", viewModel.Step2Diagnostics, nameof(FtImportWizardViewModel.HasStep2Diagnostics), viewModel);

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), RowSpacing = 10 };
        root.Children.Add(dualGrid);
        Grid.SetRow(diagnosticsPanel, 1);
        root.Children.Add(diagnosticsPanel);
        return root;
    }

    private Control CreateStep3View(FtImportWizardViewModel viewModel)
    {
        var nameBox = new TextBox
        {
            Name = "WorksheetNameTextBox",
            MinWidth = 280
        };
        nameBox.Bind(TextBox.TextProperty, new Binding(nameof(FtImportWizardViewModel.WorksheetName))
        {
            Source = viewModel,
            Mode = BindingMode.TwoWay
        });
        nameBox.Bind(IsEnabledProperty, new Binding(nameof(FtImportWizardViewModel.IsImporting))
        {
            Source = viewModel,
            Converter = Avalonia.Data.Converters.BoolConverters.Not
        });

        var nameError = new TextBlock
        {
            Name = "WorksheetNameErrorText",
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            FontSize = 12
        };
        nameError.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.WorksheetNameError)) { Source = viewModel });
        nameError.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.WorksheetNameError))
        {
            Source = viewModel,
            Converter = Avalonia.Data.Converters.StringConverters.IsNotNullOrEmpty
        });

        var summaryGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("180,*"),
            ColumnSpacing = 12,
            RowSpacing = 6
        };
        AddSummaryRow(summaryGrid, 0, "Import Profile", nameof(FtImportWizardViewModel.SummaryProfileName), viewModel);
        AddSummaryRow(summaryGrid, 1, "Test Stage", nameof(FtImportWizardViewModel.SummaryStage), viewModel);
        AddSummaryRow(summaryGrid, 2, "Selected Lots", nameof(FtImportWizardViewModel.SummaryLotCount), viewModel);
        AddSummaryRow(summaryGrid, 3, "Selected Files", nameof(FtImportWizardViewModel.SummaryFileCount), viewModel);
        AddSummaryRow(summaryGrid, 4, "Selected Measurements", nameof(FtImportWizardViewModel.SummarySelectedMeasurementCount), viewModel);

        var summaryCard = new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = summaryGrid
        };
        summaryCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var phaseText = new TextBlock { Name = "ProgressPhaseText", FontWeight = FontWeight.SemiBold };
        phaseText.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.ProgressPhaseText)) { Source = viewModel });

        var fileText = new TextBlock { Name = "ProgressFileText", FontSize = 12, Opacity = 0.85, TextTrimming = TextTrimming.CharacterEllipsis };
        fileText.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.ProgressFileText)) { Source = viewModel });

        var progressBar = new ProgressBar
        {
            Name = "ImportProgressBar",
            Height = 6,
            IsIndeterminate = true
        };
        progressBar.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.IsImporting)) { Source = viewModel });

        var processedText = new TextBlock { Name = "ProcessedRowsText" };
        processedText.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.ProcessedRowCount)) { Source = viewModel, StringFormat = "Processed: {0:N0}" });

        var retainedText = new TextBlock { Name = "RetainedRowsText" };
        retainedText.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.RetainedRowCount)) { Source = viewModel, StringFormat = "Retained: {0:N0}" });

        var filteredText = new TextBlock { Name = "FilteredRowsText" };
        filteredText.Bind(TextBlock.TextProperty, new Binding(nameof(FtImportWizardViewModel.FilteredRowCount)) { Source = viewModel, StringFormat = "Filtered Out: {0:N0}" });

        var countsRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 24,
            Children = { processedText, retainedText, filteredText }
        };

        var cancelImportBtn = new Button
        {
            Name = "CancelImportButton",
            Content = "Cancel Import",
            Command = viewModel.CancelImportCommand,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        cancelImportBtn.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.IsImporting)) { Source = viewModel });

        var progressCard = new Border
        {
            Padding = new Thickness(14, 12),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 8,
                Children = { phaseText, fileText, progressBar, countsRow, cancelImportBtn }
            }
        };
        progressCard.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var diagnosticsPanel = CreateDiagnosticsList("Step3DiagnosticsList", viewModel.Step3Diagnostics, nameof(FtImportWizardViewModel.HasStep3Diagnostics), viewModel);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*"), RowSpacing = 10 };
        root.Children.Add(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = "Target Worksheet Name", FontWeight = FontWeight.Medium },
                nameBox,
                nameError
            }
        });
        Grid.SetRow(summaryCard, 1);
        root.Children.Add(summaryCard);
        Grid.SetRow(progressCard, 2);
        root.Children.Add(progressCard);
        Grid.SetRow(diagnosticsPanel, 3);
        root.Children.Add(diagnosticsPanel);
        return root;
    }

    private Control CreateFooter(FtImportWizardViewModel viewModel)
    {
        var backBtn = new Button { Name = "BackButton", Content = "< Back", Command = viewModel.BackCommand, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        var nextBtn = new Button { Name = "NextButton", Content = "Next >", Command = viewModel.NextCommand, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        nextBtn.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.ShowNextButton)) { Source = viewModel });

        var importBtn = new Button { Name = "ImportButton", Content = "Import", Command = viewModel.StartImportCommand, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
        importBtn.Bind(IsVisibleProperty, new Binding(nameof(FtImportWizardViewModel.ShowImportButton)) { Source = viewModel });

        var closeBtn = new Button { Name = "CloseButton", Content = "Close", MinWidth = 88, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        closeBtn.Bind(IsEnabledProperty, new Binding(nameof(FtImportWizardViewModel.CanCloseDialog)) { Source = viewModel });
        closeBtn.Click += (_, _) => Close(_resultToReturn);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { backBtn, nextBtn, importBtn, closeBtn }
        };

        return buttons;
    }

    private Control CreateDiagnosticsList(
        string name,
        System.Collections.IEnumerable itemsSource,
        string visibilityProperty,
        FtImportWizardViewModel viewModel)
    {
        var list = new ItemsControl
        {
            Name = name,
            ItemsSource = itemsSource,
            ItemTemplate = new FuncDataTemplate<FtImportDiagnostic>((d, _) =>
            {
                var isErr = d?.Severity == FtImportDiagnosticSeverity.Error;
                var prefix = d is null ? string.Empty : $"[{d.Severity}] ";
                return new TextBlock
                {
                    Text = prefix + (d?.Message ?? string.Empty),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = isErr
                        ? new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38))
                        : new SolidColorBrush(Color.FromRgb(0xC6, 0x7E, 0x00))
                };
            })
        };

        var border = new Border
        {
            MaxHeight = 110,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Child = new ScrollViewer { Content = list }
        };
        border.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        border.Bind(IsVisibleProperty, new Binding(visibilityProperty) { Source = viewModel });
        return border;
    }

    private static void AddSummaryRow(
        Grid grid,
        int rowIndex,
        string label,
        string propertyName,
        FtImportWizardViewModel viewModel)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        var labelText = new TextBlock { Text = label, Opacity = 0.75 };
        var valueText = new TextBlock { FontWeight = FontWeight.Medium, TextTrimming = TextTrimming.CharacterEllipsis };
        valueText.Bind(TextBlock.TextProperty, new Binding(propertyName) { Source = viewModel });

        Grid.SetRow(labelText, rowIndex);
        grid.Children.Add(labelText);
        Grid.SetRow(valueText, rowIndex);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(valueText);
    }
}
