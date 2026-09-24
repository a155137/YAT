using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The statistics options (Task #045): whether the panel is shown and which of its statistics, all on one row -
// "Statistics [Auto] [x] Mean [x] StDev [x] N" - which takes the place of the setup's old "Show statistics" check box,
// so the setup grows no taller. The graph setup and the Edit Statistics dialog show the statistics with this one
// editor, bound to a GraphStatisticsEditorViewModel; the statistics cannot be chosen while the panel is hidden, and
// keep their ticks meanwhile.
//
// Each check box is named after the property it edits, and the list after the statistics mode - the legend editor's
// list, beside it in the setup, is SelectedMode - so a dialog can find them and tests can drive them.
internal static class GraphStatisticsEditor
{
    public const string ModeName = "SelectedStatisticsMode";

    public static Control Create(GraphStatisticsEditorViewModel statistics)
    {
        ArgumentNullException.ThrowIfNull(statistics);

        var mode = new ComboBox
        {
            ItemsSource = statistics.ModeChoices,
            MinWidth = 100,
            Margin = new Thickness(0, 3),
            Name = ModeName
        };
        mode.Bind(
            SelectingItemsControl.SelectedItemProperty,
            new Binding(nameof(GraphStatisticsEditorViewModel.SelectedMode))
            {
                Source = statistics,
                Mode = BindingMode.TwoWay
            });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock
        {
            Text = "Statistics",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 10, 3)
        });
        row.Children.Add(mode);
        row.Children.Add(Item(statistics, "Mean", nameof(GraphStatisticsEditorViewModel.ShowMean), 16));
        row.Children.Add(
            Item(statistics, "StDev", nameof(GraphStatisticsEditorViewModel.ShowStandardDeviation), 12));
        row.Children.Add(Item(statistics, "N", nameof(GraphStatisticsEditorViewModel.ShowCount), 12));
        return row;
    }

    private static CheckBox Item(
        GraphStatisticsEditorViewModel statistics,
        string text,
        string property,
        double leftMargin)
    {
        var item = new CheckBox
        {
            Content = text,
            Name = property,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(leftMargin, 0, 0, 0)
        };
        item.Bind(
            ToggleButton.IsCheckedProperty,
            new Binding(property) { Source = statistics, Mode = BindingMode.TwoWay });
        item.Bind(
            InputElement.IsEnabledProperty,
            new Binding(nameof(GraphStatisticsEditorViewModel.AreItemsEnabled)) { Source = statistics });
        return item;
    }
}
