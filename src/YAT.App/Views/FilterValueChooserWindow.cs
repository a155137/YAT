using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The Choose Values dialog of a filter condition (Task #049's value list, a sub-dialog of the Filter dialog since #053):
// the column's values with a check box each, Select All and Clear, (Missing) where the column has rows without a value,
// and Apply or Cancel. Modal to the Filter dialog.
//
// The values are a virtualized list of a fixed height, so the dialog is the same size for 3 values or 1,000 and stays
// well within a 1280 x 720 screen. A column with more values than can be offered shows why, in place of the list.
internal sealed class FilterValueChooserWindow : Window
{
    private const double ListHeight = 260;

    private FilterValueChooserWindow(FilterValueChooserViewModel chooser)
    {
        DataContext = chooser;
        Title = chooser.Title;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var values = new ListBox
        {
            Name = "FilterValues",
            ItemsSource = chooser.Values,
            Height = ListHeight,
            ItemTemplate = new FuncDataTemplate<FilterValueOption>((option, _) =>
            {
                // Across the whole row, so a click anywhere on it toggles the value.
                var check = new CheckBox { Content = option?.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FilterValueOption.IsSelected)) { Mode = BindingMode.TwoWay });
                return check;
            })
        };

        // In place of the list while it is read, when the column has too many values, or when they could not be read.
        var message = new TextBlock
        {
            Name = "FilterMessage",
            Height = ListHeight,
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(4, 8)
        };

        var missing = new CheckBox { Name = "IncludeMissing", Content = FilterValueChooserViewModel.MissingLabel };
        missing.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(FilterValueChooserViewModel.IncludeMissing))
        {
            Source = chooser,
            Mode = BindingMode.TwoWay
        });
        missing.Bind(IsVisibleProperty, new Binding(nameof(FilterValueChooserViewModel.HasMissingOption)) { Source = chooser });

        var selectAll = new Button { Name = "SelectAll", Content = "Select All", Command = chooser.SelectAllCommand, MinWidth = 88 };
        var clear = new Button { Name = "Clear", Content = "Clear", Command = chooser.ClearCommand, MinWidth = 88 };

        var summary = new TextBlock { Name = "FilterSummary", Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(FilterValueChooserViewModel.Summary)) { Source = chooser });

        var apply = new Button
        {
            Name = "Apply",
            Content = "Apply",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        apply.Classes.Add("accent");
        apply.Bind(IsEnabledProperty, new Binding(nameof(FilterValueChooserViewModel.CanApply)) { Source = chooser });
        apply.Click += (_, _) =>
        {
            if (chooser.TryApply(out var choice))
            {
                Close(choice);
            }
        };

        var cancel = new Button
        {
            Name = "Cancel",
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Close(null);

        void ShowValues()
        {
            var text = chooser.IsLoading ? "Reading the values of this column..." : chooser.Message;
            message.Text = text;
            message.IsVisible = text is not null;
            values.IsVisible = text is null;

            // The summary speaks for the values; without them, the message above already says why.
            summary.IsVisible = text is null;
        }

        chooser.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FilterValueChooserViewModel.IsLoading) or nameof(FilterValueChooserViewModel.Message))
            {
                ShowValues();
            }
        };
        ShowValues();

        // A read still running when the dialog closes is of no use any more.
        Closed += (_, _) => chooser.Cancel();

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Values of {chooser.Column.Name}" },
                new Panel { Children = { values, message } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { selectAll, clear }
                },
                missing,
                summary,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0),
                    Children = { apply, cancel }
                }
            }
        };
    }

    // Shows the dialog over its owner. Returns the chosen values, or null when the dialog was cancelled.
    public static Task<FilterValueChoice?> ShowAsync(Window owner, FilterValueChooserViewModel chooser) =>
        new FilterValueChooserWindow(chooser).ShowDialog<FilterValueChoice?>(owner);
}
