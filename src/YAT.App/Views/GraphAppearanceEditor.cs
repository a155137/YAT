using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The appearance of a graph (Task #046), edited through a GraphAppearanceEditorViewModel: the palette - Default, or a
// row of custom colours to pick, add to and remove from - the grid, its colour, and the two backgrounds. The setup's
// Appearance... dialog and a graph window's Edit Appearance dialog both show this one editor.
//
// Each list is named after the property it edits and each colour after the colour it is, so a dialog can find them and
// tests can drive them.
internal static class GraphAppearanceEditor
{
    public static Control Create(GraphAppearanceEditorViewModel appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto")
        };

        var paletteMode = nameof(GraphAppearanceEditorViewModel.SelectedPaletteMode);
        Add(grid, 0, "Palette", Choice(appearance, appearance.PaletteModeChoices, paletteMode));
        var palette = Palette(appearance);
        Grid.SetRow(palette, 1);
        Grid.SetColumn(palette, 1);
        grid.Children.Add(palette);

        var gridMode = nameof(GraphAppearanceEditorViewModel.SelectedGridMode);
        Add(grid, 2, "Grid", Choice(appearance, appearance.GridModeChoices, gridMode));
        Add(grid, 3, "Grid color", GraphColorEditor.Choice(appearance.GridColor, "GridColor"));
        Add(grid, 4, "Plot background", GraphColorEditor.Choice(appearance.PlotBackground, "PlotBackground"));
        Add(grid, 5, "Graph background", GraphColorEditor.Choice(appearance.GraphBackground, "GraphBackground"));
        return grid;
    }

    // The custom palette, shown only while it is chosen: its colours in a row to pick from, Add and Remove, and the
    // colour picked, edited like any other.
    private static Control Palette(GraphAppearanceEditorViewModel appearance)
    {
        var colors = new ListBox
        {
            Name = nameof(GraphAppearanceEditorViewModel.PaletteColors),
            ItemsSource = appearance.PaletteColors,
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel { MaxWidth = 8 * 38 }),
            ItemTemplate = new FuncDataTemplate<GraphColorEditorViewModel>((_, _) =>
            {
                var swatch = GraphColorEditor.Swatch();
                swatch.Bind(
                    Border.BackgroundProperty,
                    new Binding(nameof(GraphColorEditorViewModel.Color)) { Converter = GraphColorEditor.Brush });
                return swatch;
            }),
            Padding = new Thickness(0)
        };
        colors.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(GraphAppearanceEditorViewModel.SelectedPaletteColor))
            {
                Source = appearance,
                Mode = BindingMode.TwoWay
            });

        var add = new Button { Name = "AddPaletteColor", Content = "Add", Command = appearance.AddPaletteColorCommand };
        var remove = new Button
        {
            Name = "RemovePaletteColor",
            Content = "Remove",
            Command = appearance.RemovePaletteColorCommand
        };

        var selected = new ContentControl
        {
            ContentTemplate = new FuncDataTemplate<GraphColorEditorViewModel>((color, _) =>
                color is null ? null : GraphColorEditor.Create(color, "PaletteColor"))
        };
        selected.Bind(
            ContentControl.ContentProperty,
            new Binding(nameof(GraphAppearanceEditorViewModel.SelectedPaletteColor)) { Source = appearance });

        var panel = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 2, 0, 6),
            Children =
            {
                colors,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { selected, add, remove }
                }
            }
        };
        panel.Bind(
            Visual.IsVisibleProperty,
            new Binding(nameof(GraphAppearanceEditorViewModel.IsCustomPalette)) { Source = appearance });
        return panel;
    }

    private static ComboBox Choice<T>(
        GraphAppearanceEditorViewModel appearance,
        IReadOnlyList<SetupChoice<T>> choices,
        string property)
    {
        var selector = new ComboBox { ItemsSource = choices, MinWidth = 100, Name = property };
        selector.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(property) { Source = appearance, Mode = BindingMode.TwoWay });
        return selector;
    }

    private static void Add(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 14, 4)
        };
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        control.Margin = new Thickness(0, 4);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }
}
