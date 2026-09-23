using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The graph title and the axis titles, one fixed row each: where the label comes from, and the text used when it is
// Custom. The text box is editable only for Custom and keeps its text when the mode changes. The graph setup and the
// Edit Labels dialog show labels with this one editor, bound to a GraphLabelsEditorViewModel.
//
// Each control is named after the property it edits, so a dialog can find the row of a label and tests can drive it.
internal static class GraphLabelsEditor
{
    public static Control Create(GraphLabelsEditorViewModel labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto")
        };

        AddRow(grid, 0, labels, "Graph title", GraphLabelField.Title,
            nameof(GraphLabelsEditorViewModel.IsGraphTitleTextEnabled));
        AddRow(grid, 1, labels, "X-axis title", GraphLabelField.XAxisTitle,
            nameof(GraphLabelsEditorViewModel.IsXAxisTitleTextEnabled));
        AddRow(grid, 2, labels, "Y-axis title", GraphLabelField.YAxisTitle,
            nameof(GraphLabelsEditorViewModel.IsYAxisTitleTextEnabled));

        return grid;
    }

    // The name of the text box a label's text is typed in.
    public static string TextBoxName(GraphLabelField field) => field switch
    {
        GraphLabelField.XAxisTitle => nameof(GraphLabelsEditorViewModel.XAxisTitleText),
        GraphLabelField.YAxisTitle => nameof(GraphLabelsEditorViewModel.YAxisTitleText),
        _ => nameof(GraphLabelsEditorViewModel.GraphTitleText)
    };

    // The name of the list a label's mode is chosen in.
    public static string ModeBoxName(GraphLabelField field) => field switch
    {
        GraphLabelField.XAxisTitle => nameof(GraphLabelsEditorViewModel.SelectedXAxisTitleMode),
        GraphLabelField.YAxisTitle => nameof(GraphLabelsEditorViewModel.SelectedYAxisTitleMode),
        _ => nameof(GraphLabelsEditorViewModel.SelectedGraphTitleMode)
    };

    private static void AddRow(
        Grid grid,
        int row,
        GraphLabelsEditorViewModel labels,
        string label,
        GraphLabelField field,
        string enabledProperty)
    {
        var modeProperty = ModeBoxName(field);
        var textProperty = TextBoxName(field);

        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 12, 3)
        };
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        var mode = new ComboBox
        {
            ItemsSource = labels.LabelModeChoices,
            MinWidth = 110,
            Name = modeProperty,
            Margin = new Thickness(0, 3)
        };
        mode.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(modeProperty) { Source = labels, Mode = BindingMode.TwoWay });
        Grid.SetRow(mode, row);
        Grid.SetColumn(mode, 1);
        grid.Children.Add(mode);

        var editor = new TextBox
        {
            Width = 220,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(8, 3, 0, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Name = textProperty
        };
        editor.Bind(TextBox.TextProperty, new Binding(textProperty) { Source = labels, Mode = BindingMode.TwoWay });
        editor.Bind(InputElement.IsEnabledProperty, new Binding(enabledProperty) { Source = labels });
        Grid.SetRow(editor, row);
        Grid.SetColumn(editor, 2);
        grid.Children.Add(editor);
    }
}
