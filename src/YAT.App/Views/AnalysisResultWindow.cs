using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.Analyses;

namespace YAT.app.Views;

// An analysis result in its own window: a title and the result table, shown in a grid the user can scroll, resize and
// select in. It shares no state with the main window, so a result stays on screen while the user keeps working.
//
// It is the shared result window of every analysis, not of descriptive statistics: it is given an AnalysisResultTable
// and shows exactly that. It knows no analysis, no statistic and no worksheet, and it deliberately has no export, no
// graph and no persistence - what it establishes is a place for results to appear.
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

    public AnalysisResultWindow(AnalysisResultTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        Title = string.IsNullOrWhiteSpace(table.Title) ? "Analysis" : table.Title;
        Width = DefaultWidth;
        Height = DefaultHeight;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var heading = new TextBlock
        {
            Text = table.Title,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(12, 10, 12, 8),
            TextWrapping = TextWrapping.Wrap
        };

        DockPanel.SetDock(heading, Dock.Top);
        Content = new DockPanel { Children = { heading, Grid(table) } };
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
