using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The graph setup's Filter dialog (Task #049): the column the graph's rows are selected by, its values with a check box
// each, Select All and Clear, (Missing) where the column has rows without a value, and Apply or Cancel. Modal to the
// setup. Apply gives the filter - none at all when every row is kept; Cancel changes nothing.
//
// The values are a virtualized list of a fixed height, so the dialog is the same size for 3 values or 1,000 and stays
// well within a 1280 x 720 screen. A column with more values than can be offered shows why, in place of the list.
internal sealed class GraphFilterWindow : Window
{
    private const double ListHeight = 260;

    private GraphFilterWindow(GraphFilterEditorViewModel editor)
    {
        DataContext = editor;
        Title = "Filter Data";
        SizeToContent = SizeToContent.Height;
        Width = 420;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var column = new ComboBox
        {
            Name = "FilterColumn",
            ItemsSource = editor.Columns,
            ItemTemplate = ColumnTemplate(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Select a column"
        };
        column.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(GraphFilterEditorViewModel.SelectedColumn))
        {
            Source = editor,
            Mode = BindingMode.TwoWay
        });

        var values = new ListBox
        {
            Name = "FilterValues",
            ItemsSource = editor.Values,
            Height = ListHeight,
            ItemTemplate = new FuncDataTemplate<GraphFilterValueOption>((option, _) =>
            {
                // Across the whole row, so a click anywhere on it toggles the value.
                var check = new CheckBox { Content = option?.Label, HorizontalAlignment = HorizontalAlignment.Stretch };
                check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(GraphFilterValueOption.IsSelected)) { Mode = BindingMode.TwoWay });
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

        var missing = new CheckBox { Name = "IncludeMissing", Content = GraphFilterEditorViewModel.MissingLabel };
        missing.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(GraphFilterEditorViewModel.IncludeMissing))
        {
            Source = editor,
            Mode = BindingMode.TwoWay
        });
        missing.Bind(IsVisibleProperty, new Binding(nameof(GraphFilterEditorViewModel.HasMissingOption)) { Source = editor });

        var selectAll = new Button { Name = "SelectAll", Content = "Select All", Command = editor.SelectAllCommand, MinWidth = 88 };
        var clear = new Button { Name = "Clear", Content = "Clear", Command = editor.ClearCommand, MinWidth = 88 };

        var summary = new TextBlock { Name = "FilterSummary", Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(GraphFilterEditorViewModel.Summary)) { Source = editor });

        var apply = new Button
        {
            Name = "Apply",
            Content = "Apply",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        apply.Classes.Add("accent");
        apply.Bind(IsEnabledProperty, new Binding(nameof(GraphFilterEditorViewModel.CanApply)) { Source = editor });
        apply.Click += (_, _) =>
        {
            if (editor.TryApply(out var edit))
            {
                Close(edit);
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
            var text = editor.IsLoading ? "Reading the values of this column..." : editor.Message;
            if (editor.SelectedColumn is null)
            {
                text = "Choose the column to select rows by.";
            }

            message.Text = text;
            message.IsVisible = text is not null;
            values.IsVisible = text is null;

            // The summary speaks for the values; without them, the message above already says why.
            summary.IsVisible = text is null;
        }

        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(GraphFilterEditorViewModel.IsLoading) or nameof(GraphFilterEditorViewModel.Message)
                or nameof(GraphFilterEditorViewModel.SelectedColumn))
            {
                ShowValues();
            }
        };
        ShowValues();

        // A read still running when the dialog closes is of no use any more.
        Closed += (_, _) => editor.Cancel();

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 10,
            Children =
            {
                Labelled("Column", column),
                new TextBlock { Text = "Values" },
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

    // Shows the dialog over its owner. Returns the confirmed filter (whose Filter is null for every row), or null when
    // the dialog was cancelled.
    public static Task<GraphFilterEdit?> ShowAsync(Window owner, GraphFilterEditorViewModel editor) =>
        new GraphFilterWindow(editor).ShowDialog<GraphFilterEdit?>(owner);

    private static Control Labelled(string text, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    // Name, with the column's data type beside it, as the setup shows columns.
    private static IDataTemplate ColumnTemplate() =>
        new FuncDataTemplate<GraphColumnOption>((option, _) => option is null
            ? null
            : new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = option.Name },
                    new TextBlock { Text = option.DataTypeName, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }
                }
            });
}
