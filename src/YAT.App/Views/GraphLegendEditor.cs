using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The legend options (Task #044): whether the legend is shown, and on which side of the plot, side by side on one row -
// the setup has no height to spare for two. The graph setup and the Edit Legend dialog show the legend with this one
// editor, bound to a GraphLegendEditorViewModel; the position cannot be chosen while the legend is hidden.
//
// Each list is named after the property it edits, so a dialog can find it and tests can drive it.
internal static class GraphLegendEditor
{
    public static Control Create(GraphLegendEditorViewModel legend)
    {
        ArgumentNullException.ThrowIfNull(legend);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto") };

        Add(grid, 0, "Display", Choice(legend, legend.ModeChoices, nameof(GraphLegendEditorViewModel.SelectedMode)), 0);
        var position = Choice(legend, legend.PositionChoices, nameof(GraphLegendEditorViewModel.SelectedPosition));
        position.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(GraphLegendEditorViewModel.IsPositionEnabled)) { Source = legend });
        Add(grid, 2, "Position", position, 16);
        return grid;
    }

    private static ComboBox Choice<T>(
        GraphLegendEditorViewModel legend,
        IReadOnlyList<SetupChoice<T>> choices,
        string property)
    {
        var selector = new ComboBox { ItemsSource = choices, MinWidth = 100, Name = property };
        selector.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(property) { Source = legend, Mode = BindingMode.TwoWay });
        return selector;
    }

    private static void Add(Grid grid, int column, string label, Control control, double leftMargin)
    {
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(leftMargin, 3, 10, 3)
        };
        Grid.SetColumn(text, column);
        grid.Children.Add(text);

        control.Margin = new Thickness(0, 3);
        Grid.SetColumn(control, column + 1);
        grid.Children.Add(control);
    }
}
