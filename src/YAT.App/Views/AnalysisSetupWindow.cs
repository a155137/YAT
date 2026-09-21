using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.Application.Analyses;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The analysis setup dialog: the worksheet's numeric columns on the left, where one or more variables are picked, and
// the optional grouping column on the right.
//
// It is the analysis counterpart of the graph setup window and deliberately its own: an analysis picks several
// variables at once, where a graph fills one role per column. Nothing here is specific to descriptive statistics -
// the title comes from the view model.
internal sealed class AnalysisSetupWindow : Window
{
    private readonly AnalysisSetupViewModel _setup;
    private readonly TextBlock _validation;
    private readonly Button _confirm;

    private AnalysisSetupWindow(AnalysisSetupViewModel setup)
    {
        _setup = setup;
        DataContext = setup;
        Title = setup.Title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 560;

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
            if (e.PropertyName == nameof(AnalysisSetupViewModel.CanConfirm))
            {
                _confirm.IsEnabled = setup.CanConfirm;
            }
            else if (e.PropertyName == nameof(AnalysisSetupViewModel.ValidationMessage))
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
                    ColumnDefinitions = new ColumnDefinitions("260,24,*"),
                    Children = { VariableList(setup), GroupSelector(setup) }
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
    public static Task<AnalysisConfiguration?> ShowAsync(Window owner, AnalysisSetupViewModel setup) =>
        new AnalysisSetupWindow(setup).ShowDialog<AnalysisConfiguration?>(owner);

    // The variables: several may be picked (click, Ctrl+click, Shift+click). The list's selection is mirrored onto the
    // view model, which is what the configuration is built from.
    private static Control VariableList(AnalysisSetupViewModel setup)
    {
        var list = new ListBox
        {
            ItemsSource = setup.Variables,
            Height = 200,
            SelectionMode = SelectionMode.Multiple,
            ItemTemplate = new FuncDataTemplate<AnalysisVariableViewModel>((variable, _) => variable is null
                ? null
                : Entry(variable.Name, variable.DataTypeName))
        };

        list.SelectionChanged += (_, _) =>
        {
            foreach (var variable in setup.Variables)
            {
                variable.IsSelected = list.SelectedItems?.Contains(variable) == true;
            }
        };

        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Variables", FontWeight = FontWeight.SemiBold },
                list
            }
        };

        Grid.SetColumn(panel, 0);
        return panel;
    }

    private static Control GroupSelector(AnalysisSetupViewModel setup)
    {
        var selector = new ComboBox
        {
            ItemsSource = setup.GroupOptions,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<AnalysisColumnOption>((option, _) => option is null
                ? null
                : Entry(option.Name, option.DataTypeName))
        };
        selector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(AnalysisSetupViewModel.SelectedGroup))
        {
            Source = setup,
            Mode = BindingMode.TwoWay
        });

        var panel = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                new TextBlock { Text = "Categorical variable for grouping", FontWeight = FontWeight.SemiBold },
                selector,
                new TextBlock { Text = "Optional.", Opacity = 0.6, FontSize = 11 }
            }
        };

        Grid.SetColumn(panel, 2);
        return panel;
    }

    // Name, with the column's data type beside it; ids are never shown.
    private static Control Entry(string name, string dataTypeName) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Children =
        {
            new TextBlock { Text = name },
            new TextBlock { Text = dataTypeName, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 }
        }
    };

    private void Confirm()
    {
        if (_setup.Confirm() is { } configuration)
        {
            Close(configuration);
        }
    }
}
