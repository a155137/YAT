using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// One colour, edited (Task #046): a swatch showing it, its "#RRGGBB" text, and a button opening a few swatches to pick
// from - Avalonia's own controls, no colour picker package. Every colour of the appearance is edited with it, and a
// colour chosen Default or Custom adds only the choice in front of it (Choice). Controls are named after the colour
// they edit ("GridColorText", "GridColorSwatches"), so a dialog can find them and tests can drive them.
internal static class GraphColorEditor
{
    private const double SwatchSize = 22;

    // A colour as a brush; a colour that is not one yet shows nothing.
    public static IValueConverter Brush { get; } = new FuncValueConverter<object?, IBrush>(value =>
        value is GraphColor color
            ? new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B))
            : Brushes.Transparent);

    public static Control Create(GraphColorEditorViewModel color, string name)
    {
        ArgumentNullException.ThrowIfNull(color);

        var swatch = Swatch();
        swatch.Bind(
            Border.BackgroundProperty,
            new Binding(nameof(GraphColorEditorViewModel.Color)) { Source = color, Converter = Brush });

        var text = new TextBox
        {
            Name = $"{name}Text",
            Width = 92,
            PlaceholderText = "#RRGGBB",
            VerticalAlignment = VerticalAlignment.Center
        };
        text.Bind(
            TextBox.TextProperty,
            new Binding(nameof(GraphColorEditorViewModel.Text)) { Source = color, Mode = BindingMode.TwoWay });
        text.LostFocus += (_, _) => color.Canonicalize();

        var flyout = new Flyout();
        var swatches = new WrapPanel { MaxWidth = 8 * (SwatchSize + 6) };
        foreach (var offered in GraphColorEditorViewModel.Swatches)
        {
            var pick = new Button
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(3),
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromRgb(offered.R, offered.G, offered.B)),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1)
            };
            ToolTip.SetTip(pick, offered.ToString());
            pick.Click += (_, _) =>
            {
                color.Choose(offered);
                flyout.Hide();
            };
            swatches.Children.Add(pick);
        }

        flyout.Content = swatches;
        var open = new Button
        {
            Name = $"{name}Swatches",
            Content = "▾",
            Padding = new Thickness(8, 2),
            Flyout = flyout
        };

        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { swatch, text, open }
        };
    }

    // A colour that is the theme's or the user's: Default or Custom in front of the colour, which is editable only once
    // Custom is chosen.
    public static Control Choice(GraphColorChoiceViewModel choice, string name)
    {
        ArgumentNullException.ThrowIfNull(choice);

        var mode = new ComboBox
        {
            Name = $"{name}Mode",
            ItemsSource = choice.ModeChoices,
            MinWidth = 100,
            VerticalAlignment = VerticalAlignment.Center
        };
        mode.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(GraphColorChoiceViewModel.SelectedMode)) { Source = choice, Mode = BindingMode.TwoWay });

        var editor = Create(choice.Editor, name);
        editor.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(GraphColorChoiceViewModel.IsCustom)) { Source = choice });

        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { mode, editor } };
    }

    // A swatch the size of the text beside it, framed so a colour like the background still shows.
    public static Border Swatch() => new()
    {
        Width = SwatchSize,
        Height = SwatchSize,
        BorderBrush = Brushes.Gray,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(2),
        VerticalAlignment = VerticalAlignment.Center
    };
}
