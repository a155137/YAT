using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The graph setup dialog, shared by every graph type: the worksheet's columns on the left, one selector per role of the
// chosen graph type on the right. The roles come from the graph specification, so this window has no per-graph logic.
internal sealed class GraphSetupWindow : Window
{
    private readonly GraphSetupViewModel _setup;
    private readonly TextBlock _validation;
    private readonly Button _confirm;

    private GraphSetupWindow(GraphSetupViewModel setup)
    {
        _setup = setup;
        DataContext = setup;
        Title = setup.Title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 520;

        _validation = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false
        };

        _confirm = new Button
        {
            Content = "OK",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true,
            IsEnabled = setup.CanConfirm
        };
        _confirm.Classes.Add("accent");
        _confirm.Click += (_, _) => Confirm();

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Close(null);

        setup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphSetupViewModel.CanConfirm))
            {
                _confirm.IsEnabled = setup.CanConfirm;
            }
            else if (e.PropertyName == nameof(GraphSetupViewModel.ValidationMessage))
            {
                _validation.Text = setup.ValidationMessage;
                _validation.IsVisible = !string.IsNullOrEmpty(setup.ValidationMessage);
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Worksheet: {setup.WorksheetName}", Opacity = 0.7 },
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("240,24,*"),
                    Children = { AvailableColumns(setup), Roles(setup) }
                },
                _validation,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { _confirm, cancel }
                }
            }
        };
    }

    // Shows the dialog and returns the confirmed configuration, or null when it was cancelled.
    public static Task<GraphConfiguration?> ShowAsync(Window owner, GraphSetupViewModel setup) =>
        new GraphSetupWindow(setup).ShowDialog<GraphConfiguration?>(owner);

    private static Control AvailableColumns(GraphSetupViewModel setup)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = "Available Columns", FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new ListBox
        {
            ItemsSource = setup.AvailableColumns,
            Height = 180,
            ItemTemplate = ColumnTemplate(),
            SelectionMode = SelectionMode.Single
        });

        Grid.SetColumn(panel, 0);
        return panel;
    }

    // One grid for every role, so the selectors line up under each other whatever the roles are called: the label
    // column takes the width of the longest label ("Categorical variable for grouping" is a good deal longer than
    // "X-axis"), and the selectors share what is left.
    private static Control Roles(GraphSetupViewModel setup)
    {
        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Top
        };

        for (var index = 0; index < setup.Roles.Count; index++)
        {
            panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        var row = 0;
        foreach (var role in setup.Roles)
        {
            // A role that takes several columns is picked from a list; one that takes a single column keeps its
            // selector. Which of the two is read from the role, so no graph type is named here.
            var selector = role.AllowsMultiple ? MultipleSelector(role) : SingleSelector(role);

            var label = new TextBlock
            {
                Text = role.DisplayName,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 5, 12, 5)
            };

            Grid.SetRow(label, row);
            Grid.SetColumn(label, 0);
            panel.Children.Add(label);

            selector.Margin = new Thickness(0, 5);
            Grid.SetRow(selector, row);
            Grid.SetColumn(selector, 1);
            panel.Children.Add(selector);
            row++;
        }

        Grid.SetColumn(panel, 2);
        return panel;
    }

    // One column for a role that takes one.
    private static Control SingleSelector(GraphRoleViewModel role)
    {
        var selector = new ComboBox
        {
            ItemsSource = role.Options,
            ItemTemplate = ColumnTemplate(),
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            PlaceholderText = role.IsRequired ? "Select a column" : null
        };

        selector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(GraphRoleViewModel.SelectedOption))
        {
            Source = role,
            Mode = BindingMode.TwoWay
        });

        return selector;
    }

    // Several columns for a role that takes several: a list whose entries toggle, so picking a second variable does
    // not need a modifier key. The list's selection is mirrored onto the role, which is what the configuration reads.
    private static Control MultipleSelector(GraphRoleViewModel role)
    {
        var list = new ListBox
        {
            ItemsSource = role.Options,
            ItemTemplate = ColumnTemplate(),
            MinWidth = 200,
            MaxHeight = 160,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectionMode = SelectionMode.Multiple | SelectionMode.Toggle
        };

        list.SelectionChanged += (_, _) =>
        {
            var selected = list.SelectedItems?.OfType<GraphColumnOption>().ToArray() ?? [];
            foreach (var option in role.SelectedOptions.Except(selected).ToArray())
            {
                role.SelectedOptions.Remove(option);
            }

            foreach (var option in selected.Where(option => !role.SelectedOptions.Contains(option)))
            {
                role.SelectedOptions.Add(option);
            }
        };

        return list;
    }

    // Name, with the column's data type beside it; ids are never shown.
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

    private void Confirm()
    {
        if (_setup.Confirm() is { } configuration)
        {
            Close(configuration);
        }
    }
}
