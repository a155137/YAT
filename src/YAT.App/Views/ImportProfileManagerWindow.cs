using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.Application.ImportProfiles;
using YAT.app.ViewModels;

namespace YAT.app.Views;

internal sealed class ImportProfileManagerWindow : Window
{
    public ImportProfileManagerWindow(ImportProfileManagerViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;

        Title = "FT Profile Manager";
        Width = 840;
        Height = 540;
        MinWidth = 760;
        MinHeight = 480;
        CanResize = true;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var issuesBanner = new Border
        {
            Name = "IssuesBanner",
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xC6, 0x7E, 0x00)),
            Background = new SolidColorBrush(Color.FromArgb(0x1E, 0xD9, 0x82, 0x00)),
            Child = new TextBlock
            {
                Name = "IssuesSummaryText",
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12
            }
        };
        issuesBanner.Bind(IsVisibleProperty, new Binding(nameof(ImportProfileManagerViewModel.HasIssues)) { Source = viewModel });
        ((TextBlock)issuesBanner.Child).Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfileManagerViewModel.IssuesSummary)) { Source = viewModel });

        var list = new ListBox
        {
            Name = "ProfileList",
            ItemsSource = viewModel.Profiles,
            ItemTemplate = new FuncDataTemplate<ImportProfile>((_, _) =>
            {
                var name = new TextBlock
                {
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontWeight = FontWeight.Medium
                };
                name.Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfile.Name)));

                var meta = new TextBlock
                {
                    FontSize = 11,
                    Opacity = 0.7,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                meta.Bind(TextBlock.TextProperty, new Binding("FilenameRules.Mode"));

                return new StackPanel { Spacing = 2, Margin = new Thickness(2, 3), Children = { name, meta } };
            })
        };
        list.Bind(ListBox.SelectedItemProperty, new Binding(nameof(ImportProfileManagerViewModel.Selected)) { Source = viewModel, Mode = BindingMode.TwoWay });

        var emptyMessage = new TextBlock
        {
            Name = "EmptyProfilesMessage",
            Text = viewModel.EmptyStateText,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(16)
        };
        emptyMessage.Bind(IsVisibleProperty, new Binding(nameof(ImportProfileManagerViewModel.IsEmpty)) { Source = viewModel });

        var listHost = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new Grid { Children = { list, emptyMessage } }
        };
        listHost.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var leftPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8
        };
        leftPanel.Children.Add(new TextBlock { Text = "Import Profiles", FontWeight = FontWeight.SemiBold, FontSize = 14 });
        Grid.SetRow(listHost, 1);
        leftPanel.Children.Add(listHost);

        var detailsGrid = CreateDetailsGrid(viewModel);
        detailsGrid.Bind(IsVisibleProperty, new Binding(nameof(ImportProfileManagerViewModel.HasSelection)) { Source = viewModel });

        var noSelectionText = new TextBlock
        {
            Name = "NoSelectionMessage",
            Text = "Select an import profile on the left to inspect its rules.",
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(20)
        };
        noSelectionText.Bind(IsVisibleProperty, new Binding(nameof(ImportProfileManagerViewModel.HasSelection))
        {
            Source = viewModel,
            Converter = Avalonia.Data.Converters.BoolConverters.Not
        });

        var detailsHost = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(14, 12),
            Child = new Grid
            {
                Children =
                {
                    new ScrollViewer { Content = detailsGrid },
                    noSelectionText
                }
            }
        };
        detailsHost.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var rightPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8
        };
        rightPanel.Children.Add(new TextBlock { Text = "Profile Details", FontWeight = FontWeight.SemiBold, FontSize = 14 });
        Grid.SetRow(detailsHost, 1);
        rightPanel.Children.Add(detailsHost);

        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("240,*"),
            ColumnSpacing = 14
        };
        bodyGrid.Children.Add(leftPanel);
        Grid.SetColumn(rightPanel, 1);
        bodyGrid.Children.Add(rightPanel);

        var editorNotice = new TextBlock
        {
            Name = "EditorUnavailableNotice",
            Text = viewModel.EditorUnavailableNotice,
            IsVisible = !viewModel.IsEditorAvailable,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap
        };

        var newButton = new Button { Name = "NewProfileButton", Content = "New", Command = viewModel.NewCommand, MinWidth = 72 };
        var editButton = new Button { Name = "EditProfileButton", Content = "Edit", Command = viewModel.EditCommand, MinWidth = 72 };
        var duplicateButton = new Button { Name = "DuplicateProfileButton", Content = "Duplicate", Command = viewModel.DuplicateCommand, MinWidth = 84 };
        var deleteButton = new Button { Name = "DeleteProfileButton", Content = "Delete", Command = viewModel.DeleteCommand, MinWidth = 76 };
        var importButton = new Button { Name = "ImportProfileButton", Content = "Import", Command = viewModel.ImportCommand, MinWidth = 76 };
        var exportButton = new Button { Name = "ExportProfileButton", Content = "Export", Command = viewModel.ExportCommand, MinWidth = 76 };
        var closeButton = new Button { Name = "CloseButton", Content = "Close", MinWidth = 84, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        closeButton.Bind(IsEnabledProperty, new Binding(nameof(ImportProfileManagerViewModel.IsBusy))
        {
            Source = viewModel,
            Converter = Avalonia.Data.Converters.BoolConverters.Not
        });
        closeButton.Click += (_, _) => Close();
        Closing += (_, e) =>
        {
            if (viewModel.IsBusy)
            {
                e.Cancel = true;
            }
        };

        var actionButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { newButton, editButton, duplicateButton, deleteButton, importButton, exportButton }
        };

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            ColumnSpacing = 12
        };
        footerGrid.Children.Add(actionButtons);
        Grid.SetColumn(closeButton, 1);
        footerGrid.Children.Add(closeButton);

        var root = new Grid
        {
            Margin = new Thickness(18, 16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            RowSpacing = 12
        };
        root.Children.Add(issuesBanner);
        Grid.SetRow(bodyGrid, 1);
        root.Children.Add(bodyGrid);
        Grid.SetRow(editorNotice, 2);
        root.Children.Add(editorNotice);
        Grid.SetRow(footerGrid, 3);
        root.Children.Add(footerGrid);

        Content = root;
    }

    public static Task ShowAsync(Window owner, ImportProfileManagerViewModel viewModel) =>
        new ImportProfileManagerWindow(viewModel).ShowDialog(owner);

    private static Grid CreateDetailsGrid(ImportProfileManagerViewModel viewModel)
    {
        var grid = new Grid
        {
            Name = "ProfileDetailsPanel",
            ColumnDefinitions = new ColumnDefinitions("150,*"),
            ColumnSpacing = 12,
            RowSpacing = 8
        };

        AddDetailRow(grid, 0, "Name", nameof(ImportProfileManagerViewModel.SelectedName), viewModel, "DetailNameValue", bold: true);
        AddDetailRow(grid, 1, "Profile ID", nameof(ImportProfileManagerViewModel.SelectedIdText), viewModel, "DetailIdValue");
        AddDetailRow(grid, 2, "Schema Version", nameof(ImportProfileManagerViewModel.SelectedSchemaVersionText), viewModel, "DetailSchemaValue");
        AddDetailRow(grid, 3, "Extensions", nameof(ImportProfileManagerViewModel.SelectedExtensionsText), viewModel, "DetailExtensionsValue");
        AddDetailRow(grid, 4, "Filename Mode", nameof(ImportProfileManagerViewModel.SelectedFilenameModeText), viewModel, "DetailFilenameModeValue");
        AddDetailRow(grid, 5, "Validation", nameof(ImportProfileManagerViewModel.SelectedValidationStatus), viewModel, "DetailValidationValue");
        AddDetailRow(grid, 6, "Structure Rules", nameof(ImportProfileManagerViewModel.SelectedStructureSummary), viewModel, "DetailStructureValue");
        AddDetailRow(grid, 7, "Bin Rules", nameof(ImportProfileManagerViewModel.SelectedBinRulesSummary), viewModel, "DetailBinRulesValue");
        AddDetailRow(grid, 8, "Missing Rules", nameof(ImportProfileManagerViewModel.SelectedMissingRulesSummary), viewModel, "DetailMissingRulesValue");

        return grid;
    }

    private static void AddDetailRow(
        Grid grid,
        int rowIndex,
        string label,
        string propertyName,
        ImportProfileManagerViewModel viewModel,
        string valueControlName,
        bool bold = false)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var labelBlock = new TextBlock
        {
            Text = label,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Top
        };
        Grid.SetRow(labelBlock, rowIndex);
        grid.Children.Add(labelBlock);

        var valueBlock = new TextBlock
        {
            Name = valueControlName,
            TextWrapping = TextWrapping.Wrap,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal
        };
        valueBlock.Bind(TextBlock.TextProperty, new Binding(propertyName) { Source = viewModel });
        Grid.SetRow(valueBlock, rowIndex);
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);
    }
}
