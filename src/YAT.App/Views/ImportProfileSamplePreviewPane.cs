using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using YAT.app.ViewModels;

namespace YAT.app.Views;

internal sealed class ImportProfileSamplePreviewPane
{
    private const double RowHeaderWidth = 92;
    private const double CellWidth = 116;
    private const double RowHeight = 26;

    private static readonly FilePickerFileType SampleDatalogFileType = new("FT Datalog Files")
    {
        Patterns = ["*.zip", "*.csv", "*.txt", "*.dat", "*.log", "*.*"]
    };

    private readonly Window _owner;
    private readonly ImportProfileSamplePreviewViewModel _preview;
    private readonly StackPanel _headerRowPanel = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _rowHeadersPanel = new();
    private readonly StackPanel _cellRowsPanel = new();

    public ImportProfileSamplePreviewPane(Window owner, ImportProfileSamplePreviewViewModel preview)
    {
        _owner = owner;
        _preview = preview;
        _preview.PropertyChanged += (_, e) =>
        {
            if (string.Equals(e.PropertyName, nameof(ImportProfileSamplePreviewViewModel.TableRevision), StringComparison.Ordinal))
            {
                RebuildTableVisuals();
            }
        };
    }

    public Control BuildTopSampleBar()
    {
        var pathBox = new TextBox
        {
            Name = "SampleFilePathTextBox",
            IsReadOnly = true,
            PlaceholderText = "Select a representative FT datalog (.csv, .txt, .Reg.zip) to preview & validate..."
        };
        pathBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.SampleFilePath)) { Source = _preview });

        var browseBtn = new Button
        {
            Name = "BrowseSampleButton",
            Content = "Browse...",
            MinWidth = 84
        };
        browseBtn.Click += async (_, _) =>
        {
            var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Sample FT Datalog File",
                AllowMultiple = false,
                FileTypeFilter = [SampleDatalogFileType]
            });

            if (files.Count > 0 && files[0].TryGetLocalPath() is { Length: > 0 } localPath)
            {
                _preview.SetSampleFile(localPath);
            }
        };

        var clearBtn = new Button
        {
            Name = "ClearSampleButton",
            Content = "Clear",
            MinWidth = 64
        };
        clearBtn.Bind(Button.IsEnabledProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.HasSampleFile)) { Source = _preview });
        clearBtn.Click += (_, _) => _preview.ClearSampleFile();

        var fileRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 8
        };
        fileRow.Children.Add(new TextBlock { Text = "Sample File:", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold });
        Grid.SetColumn(pathBox, 1);
        fileRow.Children.Add(pathBox);
        Grid.SetColumn(browseBtn, 2);
        fileRow.Children.Add(browseBtn);
        Grid.SetColumn(clearBtn, 3);
        fileRow.Children.Add(clearBtn);

        var filenameSummary = new TextBlock
        {
            Name = "FilenameSummaryText",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap
        };
        filenameSummary.Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.FilenameSummaryText)) { Source = _preview });

        var filenameDiag = new TextBlock
        {
            Name = "FilenameDiagnosticText",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x7E, 0x00)),
            TextWrapping = TextWrapping.Wrap
        };
        filenameDiag.Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.FilenameDiagnosticText)) { Source = _preview });
        filenameDiag.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.HasFilenameDiagnostic)) { Source = _preview });

        var bar = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 8),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { fileRow, filenameSummary, filenameDiag }
            }
        };
        bar.Bind(Border.BorderBrushProperty, _owner.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        return bar;
    }

    public Control BuildPreviewPanel()
    {
        var tableBtn = new ToggleButton { Name = "TableViewToggleButton", Content = "Table View", MinWidth = 88 };
        tableBtn.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.IsTableView)) { Source = _preview, Mode = BindingMode.OneWay });
        tableBtn.Click += (_, _) => _preview.ViewMode = PreviewViewMode.Table;

        var rawBtn = new ToggleButton { Name = "RawViewToggleButton", Content = "Raw View", MinWidth = 84 };
        rawBtn.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.IsRawView)) { Source = _preview, Mode = BindingMode.OneWay });
        rawBtn.Click += (_, _) => _preview.ViewMode = PreviewViewMode.Raw;

        var loadingText = new TextBlock
        {
            Name = "PreviewLoadingText",
            Text = "Scanning sample...",
            FontSize = 12,
            Opacity = 0.72,
            VerticalAlignment = VerticalAlignment.Center
        };
        loadingText.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.IsLoading)) { Source = _preview });

        var headerBar = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            ColumnSpacing = 8
        };
        headerBar.Children.Add(new TextBlock { Text = "Sample Preview", FontWeight = FontWeight.SemiBold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(tableBtn, 1);
        headerBar.Children.Add(tableBtn);
        Grid.SetColumn(rawBtn, 2);
        headerBar.Children.Add(rawBtn);
        Grid.SetColumn(loadingText, 3);
        headerBar.Children.Add(loadingText);

        var truncationBanner = new TextBlock
        {
            Name = "PreviewTruncationText",
            FontSize = 11.5,
            Opacity = 0.78,
            TextWrapping = TextWrapping.Wrap
        };
        truncationBanner.Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.TruncationText)) { Source = _preview });
        truncationBanner.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.HasTruncation)) { Source = _preview });

        var tableView = BuildTableView();
        tableView.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.IsTableView)) { Source = _preview });

        var rawView = BuildRawView();
        rawView.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.IsRawView)) { Source = _preview });

        var emptyHint = new TextBlock
        {
            Name = "NoSampleLoadedHint",
            Text = "No sample datalog loaded. Click Browse... above to inspect a file and right-click row/column headers to assign rules.",
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24)
        };
        emptyHint.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.HasSampleFile))
        {
            Source = _preview,
            Converter = BoolConverters.Not
        });

        var previewHost = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = new Grid { Children = { tableView, rawView, emptyHint } }
        };
        previewHost.Bind(Border.BorderBrushProperty, _owner.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var statusPanel = BuildStatusFooter();

        var layout = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            RowSpacing = 8
        };
        layout.Children.Add(headerBar);
        Grid.SetRow(truncationBanner, 1);
        layout.Children.Add(truncationBanner);
        Grid.SetRow(previewHost, 2);
        layout.Children.Add(previewHost);
        Grid.SetRow(statusPanel, 3);
        layout.Children.Add(statusPanel);

        RebuildTableVisuals();
        return layout;
    }

    private Control BuildTableView()
    {
        var corner = new Border
        {
            Width = RowHeaderWidth,
            Height = 32,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Padding = new Thickness(6, 0),
            Child = new TextBlock
            {
                Text = "Row",
                FontWeight = FontWeight.SemiBold,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        corner.Bind(Border.BorderBrushProperty, _owner.GetResourceObservable("SystemControlForegroundBaseLowBrush"));

        var headerScroller = new ScrollViewer
        {
            Name = "TableHeaderHorizontalScroller",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _headerRowPanel
        };

        var headerRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{RowHeaderWidth},*")
        };
        headerRow.Children.Add(corner);
        Grid.SetColumn(headerScroller, 1);
        headerRow.Children.Add(headerScroller);

        var bodyScroller = new ScrollViewer
        {
            Name = "TableHorizontalScroller",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _cellRowsPanel
        };
        bodyScroller.ScrollChanged += (_, _) =>
        {
            headerScroller.Offset = new Vector(bodyScroller.Offset.X, 0);
        };

        var bodyGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{RowHeaderWidth},*")
        };
        bodyGrid.Children.Add(_rowHeadersPanel);
        Grid.SetColumn(bodyScroller, 1);
        bodyGrid.Children.Add(bodyScroller);

        var verticalScroller = new ScrollViewer
        {
            Name = "TableVerticalScroller",
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = bodyGrid
        };

        var tableGrid = new Grid
        {
            Name = "TablePreviewContainer",
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        tableGrid.Children.Add(headerRow);
        Grid.SetRow(verticalScroller, 1);
        tableGrid.Children.Add(verticalScroller);
        return tableGrid;
    }

    private Control BuildRawView()
    {
        var rawBox = new TextBox
        {
            Name = "RawPreviewTextBox",
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New, monospace"),
            FontSize = 12,
            BorderThickness = new Thickness(0)
        };
        rawBox.Bind(TextBox.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.RawPreviewText)) { Source = _preview });
        ScrollViewer.SetHorizontalScrollBarVisibility(rawBox, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(rawBox, ScrollBarVisibility.Auto);
        return rawBox;
    }

    private Control BuildStatusFooter()
    {
        var structureStatus = new TextBlock
        {
            Name = "SampleStructureStatusText",
            FontSize = 12,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap
        };
        structureStatus.Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.StructureStatusText)) { Source = _preview });

        var structureDiag = new TextBlock
        {
            Name = "SampleStructureDiagnosticsText",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(0xC6, 0x7E, 0x00)),
            TextWrapping = TextWrapping.Wrap
        };
        structureDiag.Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.StructureDiagnosticsText)) { Source = _preview });
        structureDiag.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.HasStructureDiagnostics)) { Source = _preview });

        var measStatus = new TextBlock
        {
            Name = "SampleMeasurementStatusText",
            FontSize = 11.5,
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap
        };
        measStatus.Bind(TextBlock.TextProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.MeasurementValidationText)) { Source = _preview });
        measStatus.Bind(Visual.IsVisibleProperty, new Binding(nameof(ImportProfileSamplePreviewViewModel.MeasurementValidationText))
        {
            Source = _preview,
            Converter = StringConverters.IsNotNullOrEmpty
        });

        var box = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 7),
            Child = new StackPanel
            {
                Spacing = 3,
                Children = { structureStatus, structureDiag, measStatus }
            }
        };
        box.Bind(Border.BorderBrushProperty, _owner.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
        return box;
    }

    private void RebuildTableVisuals()
    {
        _headerRowPanel.Children.Clear();
        _rowHeadersPanel.Children.Clear();
        _cellRowsPanel.Children.Clear();

        var gridBrush = _owner.FindResource("SystemControlForegroundBaseLowBrush") as IBrush ?? Brushes.Gray;
        var columns = _preview.PreviewColumns;
        var rows = _preview.PreviewTableRows;

        foreach (var col in columns)
        {
            var headerLabel = string.IsNullOrEmpty(col.RoleBadgeText)
                ? col.DisplayHeader
                : $"{col.DisplayHeader} {col.RoleBadgeText}";

            var headerBtn = new Button
            {
                Name = $"PreviewColHeader_{col.Index}",
                Width = CellWidth,
                Height = 32,
                Padding = new Thickness(6, 2),
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(0, 0, 1, 1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = GetColumnRoleBrush(col.AssignedRole),
                Content = new TextBlock
                {
                    Text = headerLabel,
                    FontSize = 11.5,
                    FontWeight = col.AssignedRole != PreviewColumnRole.None ? FontWeight.SemiBold : FontWeight.Medium,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                ContextMenu = BuildColumnContextMenu(col)
            };
            headerBtn.Bind(Button.BorderBrushProperty, _owner.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
            _headerRowPanel.Children.Add(headerBtn);
        }

        foreach (var row in rows)
        {
            var rowLabel = string.IsNullOrEmpty(row.RoleBadgeText)
                ? $"{row.SourceRowNumber}"
                : $"{row.SourceRowNumber} [{row.RoleBadgeText}]";

            var rowBtn = new Button
            {
                Name = $"PreviewRowHeader_{row.SourceRowNumber}",
                Width = RowHeaderWidth,
                Height = RowHeight,
                Padding = new Thickness(6, 0),
                CornerRadius = new CornerRadius(0),
                BorderThickness = new Thickness(0, 0, 1, 1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = GetRowRoleBrush(row.AssignedRole),
                Content = new TextBlock
                {
                    Text = rowLabel,
                    FontSize = 11.5,
                    FontWeight = row.AssignedRole != PreviewRowRole.None ? FontWeight.SemiBold : FontWeight.Normal,
                    TextTrimming = TextTrimming.CharacterEllipsis
                },
                ContextMenu = BuildRowContextMenu(row.SourceRowNumber)
            };
            rowBtn.Bind(Button.BorderBrushProperty, _owner.GetResourceObservable("SystemControlForegroundBaseLowBrush"));
            _rowHeadersPanel.Children.Add(rowBtn);

            var cellsRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Height = RowHeight,
                Background = GetRowRoleBrush(row.AssignedRole)
            };

            var count = Math.Max(columns.Count, row.Cells.Count);
            for (var c = 0; c < count; c++)
            {
                var cellText = c < row.Cells.Count ? row.Cells[c] : string.Empty;
                var colBg = c < columns.Count ? GetColumnRoleBrush(columns[c].AssignedRole) : Brushes.Transparent;
                var cellBorder = new Border
                {
                    Width = CellWidth,
                    Height = RowHeight,
                    Padding = new Thickness(6, 0),
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    BorderBrush = gridBrush,
                    Background = row.AssignedRole != PreviewRowRole.None ? Brushes.Transparent : colBg,
                    Child = new TextBlock
                    {
                        Text = cellText,
                        FontSize = 11.5,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    }
                };
                cellsRow.Children.Add(cellBorder);
            }

            _cellRowsPanel.Children.Add(cellsRow);
        }
    }

    private ContextMenu BuildRowContextMenu(int sourceRowNumber)
    {
        MenuItem MakeItem(string name, string baseLabel, PreviewRowRole role)
        {
            var suffix = _preview.WillReplaceKeywordRule(role) ? " (replaces Keyword rule)" : string.Empty;
            var item = new MenuItem { Name = name, Header = baseLabel + suffix };
            item.Click += (_, _) => _preview.AssignRowRole(sourceRowNumber, role);
            return item;
        }

        var clearItem = new MenuItem
        {
            Name = $"ClearRowRole_{sourceRowNumber}",
            Header = "Clear Assigned Optional Role",
            IsEnabled = _preview.CanClearRowRole(sourceRowNumber)
        };
        clearItem.Click += (_, _) => _preview.ClearRowRole(sourceRowNumber);

        return new ContextMenu
        {
            Items =
            {
                MakeItem($"SetHeaderRow_{sourceRowNumber}", "Set as Header Row", PreviewRowRole.Header),
                MakeItem($"SetDataStartRow_{sourceRowNumber}", "Set as Data Start Row", PreviewRowRole.DataStart),
                new Separator(),
                MakeItem($"SetUnitRow_{sourceRowNumber}", "Set as Unit Row", PreviewRowRole.Unit),
                MakeItem($"SetTargetRow_{sourceRowNumber}", "Set as Target Row", PreviewRowRole.Target),
                MakeItem($"SetLslRow_{sourceRowNumber}", "Set as LSL Row", PreviewRowRole.Lsl),
                MakeItem($"SetUslRow_{sourceRowNumber}", "Set as USL Row", PreviewRowRole.Usl),
                new Separator(),
                clearItem
            }
        };
    }

    private ContextMenu BuildColumnContextMenu(PreviewColumnHeaderModel col)
    {
        MenuItem MakeItem(string name, string label, PreviewColumnAction action)
        {
            var item = new MenuItem { Name = name, Header = label };
            item.Click += (_, _) => _preview.AssignColumnRole(col, action);
            return item;
        }

        var removeIgnore = MakeItem($"RemoveIgnoreCol_{col.Index}", "Remove from Ignored Columns", PreviewColumnAction.RemoveFromIgnored);
        removeIgnore.IsEnabled = col.AssignedRole == PreviewColumnRole.Ignored;

        var clearAssign = MakeItem($"ClearCol_{col.Index}", "Clear Assignment", PreviewColumnAction.ClearAssignment);
        clearAssign.IsEnabled = col.AssignedRole is PreviewColumnRole.Site or PreviewColumnRole.Ignored or PreviewColumnRole.Conflict;

        return new ContextMenu
        {
            Items =
            {
                MakeItem($"SetBinCol_{col.Index}", "Set as Bin", PreviewColumnAction.SetAsBin),
                MakeItem($"SetSiteCol_{col.Index}", "Set as Site", PreviewColumnAction.SetAsSite),
                new Separator(),
                MakeItem($"AddIgnoreCol_{col.Index}", "Add to Ignored Columns", PreviewColumnAction.AddToIgnored),
                removeIgnore,
                new Separator(),
                clearAssign
            }
        };
    }

    private static IBrush GetRowRoleBrush(PreviewRowRole role) => role switch
    {
        PreviewRowRole.Header => new SolidColorBrush(Color.FromArgb(0x28, 0x00, 0x78, 0xD4)),
        PreviewRowRole.DataStart => new SolidColorBrush(Color.FromArgb(0x26, 0x10, 0x7C, 0x10)),
        PreviewRowRole.Unit or PreviewRowRole.Target or PreviewRowRole.Lsl or PreviewRowRole.Usl =>
            new SolidColorBrush(Color.FromArgb(0x24, 0xC6, 0x7E, 0x00)),
        _ => Brushes.Transparent
    };

    private static IBrush GetColumnRoleBrush(PreviewColumnRole role) => role switch
    {
        PreviewColumnRole.Bin => new SolidColorBrush(Color.FromArgb(0x24, 0x10, 0x7C, 0x10)),
        PreviewColumnRole.Site => new SolidColorBrush(Color.FromArgb(0x24, 0x00, 0x78, 0xD4)),
        PreviewColumnRole.Ignored => new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)),
        PreviewColumnRole.Conflict => new SolidColorBrush(Color.FromArgb(0x30, 0xD1, 0x34, 0x38)),
        _ => Brushes.Transparent
    };
}
