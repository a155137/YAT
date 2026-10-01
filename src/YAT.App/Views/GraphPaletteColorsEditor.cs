using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// A palette's colours, edited (Tasks #046 and #050): the colours in a row to pick from, the colour picked - edited like
// any other colour of YAT - and Add, Remove, and ← / → to move it. The appearance editor and the Palette Manager show
// this one editor. Its parts are named ("PaletteColors", "PaletteColor", "AddPaletteColor", "RemovePaletteColor",
// "MovePaletteColorLeft", "MovePaletteColorRight"), so a dialog can find them and tests can drive them.
internal static class GraphPaletteColorsEditor
{
    public static Control Create(GraphPaletteColorsViewModel palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var colors = new ListBox
        {
            Name = "PaletteColors",
            ItemsSource = palette.Colors,
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
            new Binding(nameof(GraphPaletteColorsViewModel.Selected)) { Source = palette, Mode = BindingMode.TwoWay });

        var selected = new ContentControl
        {
            ContentTemplate = new FuncDataTemplate<GraphColorEditorViewModel>((color, _) =>
                color is null ? null : GraphColorEditor.Create(color, "PaletteColor"))
        };
        selected.Bind(ContentControl.ContentProperty, new Binding(nameof(GraphPaletteColorsViewModel.Selected)) { Source = palette });
        selected.Bind(Avalonia.Input.InputElement.IsEnabledProperty, new Binding(nameof(GraphPaletteColorsViewModel.IsEditable)) { Source = palette });

        return new StackPanel
        {
            Spacing = 6,
            Children =
            {
                colors,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        selected,
                        new Button { Name = "AddPaletteColor", Content = "Add", Command = palette.AddCommand },
                        new Button { Name = "RemovePaletteColor", Content = "Remove", Command = palette.RemoveCommand },
                        new Button { Name = "MovePaletteColorLeft", Content = "←", Command = palette.MoveLeftCommand, MinWidth = 32 },
                        new Button { Name = "MovePaletteColorRight", Content = "→", Command = palette.MoveRightCommand, MinWidth = 32 }
                    }
                }
            }
        };
    }
}
