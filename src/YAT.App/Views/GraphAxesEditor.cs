using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The axis ranges, one row per axis the graph type lets the user choose (Task #043): a minimum and a maximum, blank for
// Auto. The graph setup and the Edit Axes dialog show ranges with this one editor, bound to a GraphAxesEditorViewModel;
// the blank fields say what Auto is ("Auto", or on a drawn graph "Auto (14.88)").
//
// Each text box is named after the property it edits, so a dialog can find it and tests can drive it.
internal static class GraphAxesEditor
{
    public static Control Create(GraphAxesEditorViewModel axes)
    {
        ArgumentNullException.ThrowIfNull(axes);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto,Auto") };
        var row = 0;
        if (axes.SupportsXAxisRange)
        {
            AddRow(grid, row++, axes, axes.XAxisLabel,
                nameof(GraphAxesEditorViewModel.XMinimumText), axes.XMinimumHint,
                nameof(GraphAxesEditorViewModel.XMaximumText), axes.XMaximumHint);
        }

        if (axes.SupportsYAxisRange)
        {
            AddRow(grid, row, axes, axes.YAxisLabel,
                nameof(GraphAxesEditorViewModel.YMinimumText), axes.YMinimumHint,
                nameof(GraphAxesEditorViewModel.YMaximumText), axes.YMaximumHint);
        }

        return grid;
    }

    private static void AddRow(
        Grid grid,
        int row,
        GraphAxesEditorViewModel axes,
        string label,
        string minimumProperty,
        string minimumHint,
        string maximumProperty,
        string maximumHint)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Add(grid, row, 0, new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 12, 3)
        });
        Add(grid, row, 1, Caption("Min"));
        Add(grid, row, 2, Field(axes, minimumProperty, minimumHint));
        Add(grid, row, 3, Caption("Max", leftMargin: 12));
        Add(grid, row, 4, Field(axes, maximumProperty, maximumHint));
    }

    private static TextBlock Caption(string text, double leftMargin = 0) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(leftMargin, 3, 6, 3)
    };

    private static TextBox Field(GraphAxesEditorViewModel axes, string property, string hint)
    {
        var field = new TextBox
        {
            Width = 110,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 3),
            VerticalAlignment = VerticalAlignment.Center,
            PlaceholderText = hint,
            Name = property
        };
        field.Bind(TextBox.TextProperty, new Binding(property) { Source = axes, Mode = BindingMode.TwoWay });
        return field;
    }

    private static void Add(Grid grid, int row, int column, Control control)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}
