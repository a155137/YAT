using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The statistics options (Task #045): whether the panel is shown and which of its statistics. In the setup, all on one
// row - "Statistics [Auto] [x] Mean [x] StDev [x] N [More...]" - which takes the place of the setup's old "Show
// statistics" check box, so the setup grows no taller; More... opens every statistic (Task #062). In the Edit
// Statistics dialog, every statistic: Min, Q1, Median, Q3 and Max on a second line. The graph setup and the Edit
// Statistics dialog show the statistics with this one editor, bound to a GraphStatisticsEditorViewModel; the statistics
// cannot be chosen while the panel is hidden, and keep their ticks meanwhile.
//
// Each check box is named after the property it edits, and the list after the statistics mode - the legend editor's
// list, beside it in the setup, is SelectedMode - so a dialog can find them and tests can drive them.
internal static class GraphStatisticsEditor
{
    public const string ModeName = "SelectedStatisticsMode";

    // compact: the setup's row (Task #062) - Mean, StDev and N, and a More... button that opens every statistic in
    // the Edit Statistics dialog - so the setup grows no wider or taller; otherwise every statistic, the five-number
    // summary on a second line under the first three.
    public static Control Create(GraphStatisticsEditorViewModel statistics, bool compact = false)
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

        var label = new TextBlock
        {
            Text = "Statistics",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 3, 10, 3)
        };
        var first = new StackPanel { Orientation = Orientation.Horizontal };
        first.Children.Add(Item(statistics, "Mean", nameof(GraphStatisticsEditorViewModel.ShowMean), 16));
        first.Children.Add(
            Item(statistics, "StDev", nameof(GraphStatisticsEditorViewModel.ShowStandardDeviation), 12));
        first.Children.Add(Item(statistics, "N", nameof(GraphStatisticsEditorViewModel.ShowCount), 12));

        if (compact)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { label, mode, first } };
            var more = new Button
            {
                Name = "MoreStatistics",
                Padding = new Thickness(10, 2),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            more.Bind(
                ContentControl.ContentProperty,
                new Binding(nameof(GraphStatisticsEditorViewModel.MoreSummary)) { Source = statistics });
            more.Bind(
                InputElement.IsEnabledProperty,
                new Binding(nameof(GraphStatisticsEditorViewModel.AreItemsEnabled)) { Source = statistics });
            more.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(more) is Window owner
                    && await GraphStatisticsWindow.ShowAsync(owner, statistics.Copy()) is { } chosen)
                {
                    statistics.Apply(chosen);
                }
            };
            row.Children.Add(more);
            return row;
        }

        // The five-number summary (Task #062) on a second line, in the order a panel shows them, lined up under Mean:
        // the label and the list take the first two columns of both lines.
        var summary = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        summary.Children.Add(Item(statistics, "Min", nameof(GraphStatisticsEditorViewModel.ShowMinimum), 16));
        summary.Children.Add(Item(statistics, "Q1", nameof(GraphStatisticsEditorViewModel.ShowFirstQuartile), 12));
        summary.Children.Add(Item(statistics, "Median", nameof(GraphStatisticsEditorViewModel.ShowMedian), 12));
        summary.Children.Add(Item(statistics, "Q3", nameof(GraphStatisticsEditorViewModel.ShowThirdQuartile), 12));
        summary.Children.Add(Item(statistics, "Max", nameof(GraphStatisticsEditorViewModel.ShowMaximum), 12));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto"),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            Children = { label, mode, first, summary }
        };
        Grid.SetColumn(mode, 1);
        Grid.SetColumn(first, 2);
        Grid.SetColumn(summary, 2);
        Grid.SetRow(summary, 1);
        return grid;
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
