using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The Filter dialog of a graph or analysis setup (Task #053): one row per condition - Column, Operator, Value(s) and
// Remove (x) - the conditions all of which a row must meet; + Add condition below them, and Clear, Cancel and Apply. A
// value-set operator ("is any of", "is not any of") chooses its values in a dialog of its own (Choose...); a comparison
// takes a number, between two, "is" / "is not" text. Each row says what is wrong with it, and Apply is unavailable
// until every row can be applied.
//
// The rows scroll within a fixed height, so the dialog stays the same size for 1 condition or 20, well within a
// 1280 x 720 screen. Modal to the setup.
internal sealed class RowFilterWindow : Window
{
    private const double ListHeight = 300;

    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));

    private RowFilterWindow(RowFilterEditorViewModel editor)
    {
        DataContext = editor;
        Title = "Filter Data";
        SizeToContent = SizeToContent.Height;
        Width = 720;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var conditions = new ItemsControl
        {
            Name = "Conditions",
            ItemsSource = editor.Conditions,
            ItemTemplate = new FuncDataTemplate<RowFilterConditionViewModel>((condition, _) => condition is null ? null : ConditionRow(this, condition))
        };

        var empty = new TextBlock
        {
            Name = "NoConditions",
            Text = "No conditions: every row is used. Add a condition to select rows.",
            Opacity = 0.7,
            Margin = new Thickness(4, 8)
        };

        void ShowEmpty() => empty.IsVisible = editor.Conditions.Count == 0;
        editor.Conditions.CollectionChanged += (_, _) => ShowEmpty();
        ShowEmpty();

        var header = new Grid { ColumnDefinitions = Columns(), Margin = new Thickness(0, 0, 0, 2) };
        Add(header, 0, new TextBlock { Text = "Column", Opacity = 0.7 });
        Add(header, 1, new TextBlock { Text = "Operator", Opacity = 0.7 });
        Add(header, 2, new TextBlock { Text = "Value(s)", Opacity = 0.7 });

        var add = new Button { Name = "AddCondition", Content = "+ Add condition", Command = editor.AddConditionCommand };

        var summary = new TextBlock { Name = "FilterSummary", Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(RowFilterEditorViewModel.Summary)) { Source = editor });

        var clear = Button("Clear", "Clear");
        clear.Command = editor.ClearCommand;

        var cancel = Button("Cancel", "Cancel");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(null);

        var apply = Button("Apply", "Apply");
        apply.IsDefault = true;
        apply.Classes.Add("accent");
        apply.Bind(IsEnabledProperty, new Binding(nameof(RowFilterEditorViewModel.CanApply)) { Source = editor });
        apply.Click += (_, _) =>
        {
            if (editor.TryApply(out var edit))
            {
                Close(edit);
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Use the rows that meet all of these conditions. The worksheet is not changed.", TextWrapping = TextWrapping.Wrap },
                header,
                new ScrollViewer
                {
                    Height = ListHeight,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = new Panel { Children = { conditions, empty } }
                },
                add,
                summary,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 4, 0, 0),
                    Children = { clear, cancel, apply }
                }
            }
        };
    }

    // Shows the dialog over its owner. Returns the confirmed filter (whose Filter is null for every row), or null when
    // the dialog was cancelled.
    public static Task<RowFilterEdit?> ShowAsync(Window owner, RowFilterEditorViewModel editor) =>
        new RowFilterWindow(editor).ShowDialog<RowFilterEdit?>(owner);

    private static ColumnDefinitions Columns() => new("180,130,*,36");

    private static Control ConditionRow(Window owner, RowFilterConditionViewModel condition)
    {
        var grid = new Grid { ColumnDefinitions = Columns(), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 2, 0, 4) };

        var column = new ComboBox
        {
            Name = "ConditionColumn",
            ItemsSource = condition.Columns,
            ItemTemplate = ColumnTemplate(),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = "Column",
            Margin = new Thickness(0, 0, 6, 0)
        };
        column.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(RowFilterConditionViewModel.SelectedColumn)) { Source = condition, Mode = BindingMode.TwoWay });
        Add(grid, 0, column);

        var op = new ComboBox { Name = "ConditionOperator", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 6, 0) };
        op.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(RowFilterConditionViewModel.Operators)) { Source = condition });
        op.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(RowFilterConditionViewModel.SelectedOperator)) { Source = condition, Mode = BindingMode.TwoWay });
        Add(grid, 1, op);

        // Value(s): Choose... and what was chosen, or a number (two for between), or text.
        var choose = new Button { Name = "ChooseValues", Content = "Choose...", MinHeight = 0, Padding = new Thickness(10, 3) };
        choose.Click += async (_, _) =>
        {
            if (condition.CreateValueChooser() is { } chooser && await FilterValueChooserWindow.ShowAsync(owner, chooser) is { } choice)
            {
                condition.ApplyValues(choice);
            }
        };
        var chosen = new TextBlock { Name = "ChosenValues", VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(8, 0, 0, 0) };
        chosen.Bind(TextBlock.TextProperty, new Binding(nameof(RowFilterConditionViewModel.ValuesSummary)) { Source = condition });
        chosen.Bind(ToolTip.TipProperty, new Binding(nameof(RowFilterConditionViewModel.ValuesSummary)) { Source = condition });
        var values = new DockPanel { Name = "ValueSet", Children = { Docked(choose, Dock.Left), chosen } };
        values.Bind(IsVisibleProperty, new Binding(nameof(RowFilterConditionViewModel.ShowsValues)) { Source = condition });

        var value = Field("ConditionValue", nameof(RowFilterConditionViewModel.ValueText), condition);
        var and = new TextBlock { Text = "and", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0) };
        and.Bind(IsVisibleProperty, new Binding(nameof(RowFilterConditionViewModel.ShowsUpper)) { Source = condition });
        var upper = Field("ConditionUpper", nameof(RowFilterConditionViewModel.UpperText), condition);
        upper.Bind(IsVisibleProperty, new Binding(nameof(RowFilterConditionViewModel.ShowsUpper)) { Source = condition });
        var typed = new StackPanel { Orientation = Orientation.Horizontal, Children = { value, and, upper } };
        typed.Bind(IsVisibleProperty, new Binding(nameof(RowFilterConditionViewModel.ShowsValue)) { Source = condition });

        Add(grid, 2, new Panel { Children = { values, typed } });

        var remove = new Button
        {
            Name = "RemoveCondition",
            Content = "x",
            Command = condition.RemoveCommand,
            MinHeight = 0,
            Padding = new Thickness(8, 3),
            HorizontalAlignment = HorizontalAlignment.Right
        };
        ToolTip.SetTip(remove, "Remove this condition");
        Add(grid, 3, remove);

        var error = new TextBlock { Name = "ConditionError", Foreground = ErrorBrush, FontSize = 12, Margin = new Thickness(2, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(RowFilterConditionViewModel.Error)) { Source = condition });
        error.Bind(IsVisibleProperty, new Binding(nameof(RowFilterConditionViewModel.IsValid)) { Source = condition, Converter = Avalonia.Data.Converters.BoolConverters.Not });
        Grid.SetRow(error, 1);
        Grid.SetColumnSpan(error, 4);
        grid.Children.Add(error);

        return grid;
    }

    private static TextBox Field(string name, string property, RowFilterConditionViewModel condition)
    {
        var field = new TextBox { Name = name, Width = 120, MinHeight = 0, Padding = new Thickness(6, 2), VerticalAlignment = VerticalAlignment.Center };
        field.Bind(TextBox.TextProperty, new Binding(property) { Source = condition, Mode = BindingMode.TwoWay });
        return field;
    }

    private static Button Button(string name, string content) =>
        new() { Name = name, Content = content, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };

    private static void Add(Grid grid, int column, Control control)
    {
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    // Name, with the column's data type beside it, as the setups show columns.
    private static IDataTemplate ColumnTemplate() =>
        new FuncDataTemplate<FilterColumnOption>((option, _) => option is null
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

// The Filter row of a setup (Tasks #049, #053): the filter in a few words - "Filter: All rows", "Filter: 3 conditions" -
// and Edit... to change it in the Filter dialog. Graph, Descriptive Statistics and Capability Analysis setups share it.
internal static class RowFilterSetupRow
{
    // summaryProperty: the setup's property with the filter in a few words. createEditor: the Filter dialog's editor
    // for the filter as it is now. apply: sets the confirmed filter (null: every row).
    public static Control Create(object setup, string summaryProperty, Func<RowFilterEditorViewModel> createEditor, Action<RowFilterEdit> apply)
    {
        var summary = new TextBlock
        {
            Name = "FilterSummary",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        summary.Bind(TextBlock.TextProperty, new Binding(summaryProperty) { Source = setup });

        // A compact button: the row is no taller than a role's.
        var edit = new Button
        {
            Name = "EditFilter",
            Content = "Edit...",
            MinWidth = 72,
            MinHeight = 0,
            Padding = new Thickness(10, 3),
            Margin = new Thickness(12, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        edit.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(edit) is not Window owner)
            {
                return;
            }

            // Cancel leaves the filter as it was; Apply sets it - to none without conditions.
            if (await RowFilterWindow.ShowAsync(owner, createEditor()) is { } edited)
            {
                apply(edited);
            }
        };

        DockPanel.SetDock(edit, Dock.Right);
        return new DockPanel { Children = { edit, summary } };
    }
}
