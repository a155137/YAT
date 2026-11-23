using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.Analyses;
using YAT.app.Analyses.Export;

namespace YAT.app.Views;

// An analysis result in its own window: a title, an Export Excel action, and the result table, shown in a grid the
// user can scroll, resize and select in. It shares no state with the main window, so a result stays on screen while
// the user keeps working.
//
// It is the shared result window of every analysis, not of descriptive statistics: it is given an AnalysisResultTable
// and shows exactly that.
internal sealed class AnalysisResultWindow : Window
{
    // Wide enough for the widest table this version produces (Variable, Group and nine statistics) without scrolling.
    private const int DefaultWidth = 1000;
    private const int DefaultHeight = 460;

    // Initial column widths, from the header text: long enough to read the header, within a range per alignment.
    private const double CharacterWidth = 7.5;
    private const double ColumnChrome = 22;
    private const double NumberMinimumWidth = 84;
    private const double NumberMaximumWidth = 140;
    private const double LabelMinimumWidth = 110;
    private const double LabelMaximumWidth = 240;

    public AnalysisResultWindow(AnalysisResultTable table, IAnalysisExportDialogs? exportDialogs = null)
    {
        ArgumentNullException.ThrowIfNull(table);

        Title = string.IsNullOrWhiteSpace(table.Title) ? "Analysis" : table.Title;
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var exportController = new AnalysisExportController(
            exportDialogs ?? new AvaloniaAnalysisExportDialogs(this),
            new OpenXmlAnalysisExcelExporter());

        var heading = new TextBlock
        {
            Text = table.Title,
            FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        var headerText = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(12, 10, 12, 8),
            Children = { heading }
        };

        if (table.Note is { } note)
        {
            // What the result is over, when it is not every row (a filter, Task #053).
            headerText.Children.Add(new TextBlock
            {
                Name = "ResultNote",
                Text = note,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap
            });
        }

        var exportButton = new Button
        {
            Name = "ExportExcelButton",
            Content = "Export Excel",
            IsEnabled = table.Rows.Count > 0,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 12, 8)
        };
        exportButton.Click += async (_, _) =>
        {
            exportButton.IsEnabled = false;
            try
            {
                await exportController.ExportExcelAsync(table);
            }
            finally
            {
                exportButton.IsEnabled = table.Rows.Count > 0;
            }
        };
        DockPanel.SetDock(exportButton, Dock.Right);

        var headerBar = new DockPanel
        {
            Children = { exportButton, headerText }
        };
        DockPanel.SetDock(headerBar, Dock.Top);

        var content = new DockPanel { Children = { headerBar, Grid(table) } };
        Content = content;
    }

    private static Control Grid(AnalysisResultTable table)
    {
        var grid = new TableView
        {
            ItemsSource = table.Rows,
            CanUserResizeColumns = true,
            // The table is read, not edited: its cells are the text the analysis produced.
            Margin = new Thickness(12, 0, 12, 12)
        };

        for (var position = 0; position < table.Columns.Count; position++)
        {
            var column = table.Columns[position];
            grid.Columns.Add(new TableViewColumn
            {
                Header = column.Name,
                Binding = new ReflectionBinding($"{nameof(AnalysisResultRow.Cells)}[{position}]"),
                Width = new GridLength(ColumnWidth(column)),
                HorizontalContentAlignment = column.Alignment == AnalysisResultAlignment.Right
                    ? HorizontalAlignment.Right
                    : HorizontalAlignment.Left
            });
        }

        return grid;
    }

    private static double ColumnWidth(AnalysisResultColumn column)
    {
        var (minimum, maximum) = column.Alignment == AnalysisResultAlignment.Right
            ? (NumberMinimumWidth, NumberMaximumWidth)
            : (LabelMinimumWidth, LabelMaximumWidth);

        return Math.Clamp(Math.Ceiling((column.Name.Length * CharacterWidth) + ColumnChrome), minimum, maximum);
    }
}
