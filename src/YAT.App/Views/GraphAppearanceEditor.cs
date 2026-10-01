using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The appearance of a graph (Tasks #046 and #050), edited through a GraphAppearanceEditorViewModel: the palette - YAT
// Default, a palette of the user's library, or this graph's own colours, with the row of colours to pick, add, remove and
// move - Manage... for the Palette Manager when the dialog offers it, the grid, its colour, and the two backgrounds. The
// setup's Appearance... dialog and a graph window's Edit Appearance dialog both show this one editor.
//
// Each list is named after the property it edits and each colour after the colour it is, so a dialog can find them and
// tests can drive them.
internal static class GraphAppearanceEditor
{
    // manage: opens the Palette Manager; without it there is no Manage... button.
    public static Control Create(GraphAppearanceEditorViewModel appearance, Func<Task>? manage = null)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,Auto")
        };

        Add(grid, 0, "Palette", PaletteChoice(appearance, manage));
        var notice = Notice(appearance);
        Grid.SetRow(notice, 1);
        Grid.SetColumn(notice, 1);
        grid.Children.Add(notice);
        var palette = Palette(appearance);
        Grid.SetRow(palette, 2);
        Grid.SetColumn(palette, 1);
        grid.Children.Add(palette);

        var gridMode = nameof(GraphAppearanceEditorViewModel.SelectedGridMode);
        Add(grid, 3, "Grid", Choice(appearance, appearance.GridModeChoices, gridMode));
        Add(grid, 4, "Grid color", GraphColorEditor.Choice(appearance.GridColor, "GridColor"));
        Add(grid, 5, "Plot background", GraphColorEditor.Choice(appearance.PlotBackground, "PlotBackground"));
        Add(grid, 6, "Graph background", GraphColorEditor.Choice(appearance.GraphBackground, "GraphBackground"));
        return grid;
    }

    // The palette's colours, shown only while the series are drawn in a palette (a library palette's copied colours, or
    // this graph's own): the shared palette editor.
    private static Control Palette(GraphAppearanceEditorViewModel appearance)
    {
        var panel = GraphPaletteColorsEditor.Create(appearance.PaletteEditor);
        panel.Margin = new Thickness(0, 2, 0, 6);
        panel.Bind(
            Visual.IsVisibleProperty,
            new Binding(nameof(GraphAppearanceEditorViewModel.IsCustomPalette)) { Source = appearance });
        return panel;
    }

    // Where the series colours come from - YAT Default, a library palette, or this graph's own - and Manage... beside
    // it when the dialog can open the Palette Manager.
    private static Control PaletteChoice(GraphAppearanceEditorViewModel appearance, Func<Task>? manage)
    {
        var selector = new ComboBox { ItemsSource = appearance.PaletteOptions, MinWidth = 220, Name = nameof(GraphAppearanceEditorViewModel.SelectedPalette) };
        selector.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(GraphAppearanceEditorViewModel.SelectedPalette)) { Source = appearance, Mode = BindingMode.TwoWay });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { selector } };
        if (manage is not null)
        {
            var button = new Button { Name = "ManagePalettes", Content = "Manage...", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += async (_, _) => await manage();
            row.Children.Add(button);
        }

        return row;
    }

    // Anything the user should know about the palette settings, beside the palettes, until the Palette Manager - which
    // always says it - has been opened. YAT never stops to say it.
    private static Control Notice(GraphAppearanceEditorViewModel appearance)
    {
        var notice = new TextBlock
        {
            Name = "PaletteNotice",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            MaxWidth = 380,
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 4)
        };

        void Show()
        {
            notice.Text = appearance.PaletteNotice;
            notice.IsVisible = appearance.PaletteNotice is not null;
        }

        appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphAppearanceEditorViewModel.PaletteNotice))
            {
                Show();
            }
        };
        Show();
        return notice;
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
