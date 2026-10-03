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

// The capability analysis setup dialog: the worksheet's numeric columns with a specification limit of their own on the
// left, and the grouping column and the statistics to show on the right.
//
// Specifications are per variable, so they are entered on the variable's own row rather than once for the whole
// analysis. An empty side is a one-sided specification.
internal sealed class CapabilityAnalysisSetupWindow : Window
{
    private const double VariableColumnWidth = 210;
    private const double LimitColumnWidth = 110;

    private readonly CapabilityAnalysisSetupViewModel _setup;
    private readonly TextBlock _validation;
    private readonly Button _confirm;

    private CapabilityAnalysisSetupWindow(CapabilityAnalysisSetupViewModel setup)
    {
        _setup = setup;
        DataContext = setup;
        Title = setup.Title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 680;

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
            if (e.PropertyName == nameof(CapabilityAnalysisSetupViewModel.CanConfirm))
            {
                _confirm.IsEnabled = setup.CanConfirm;
            }
            else if (e.PropertyName == nameof(CapabilityAnalysisSetupViewModel.ValidationMessage))
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
                    ColumnDefinitions = new ColumnDefinitions("Auto,24,*"),
                    Children = { Specifications(setup), Options(setup) }
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
    public static Task<CapabilityAnalysisConfiguration?> ShowAsync(Window owner, CapabilityAnalysisSetupViewModel setup) =>
        new CapabilityAnalysisSetupWindow(setup).ShowDialog<CapabilityAnalysisConfiguration?>(owner);

    // One row per numeric column: tick it to analyse it, and give it its own limits. A blank limit means that side is
    // not specified.
    private static Control Specifications(CapabilityAnalysisSetupViewModel setup)
    {
        var headings = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{VariableColumnWidth},{LimitColumnWidth},{LimitColumnWidth}"),
            Margin = new Thickness(0, 0, 0, 2)
        };

        AddHeading(headings, "Variable", 0);
        AddHeading(headings, "LSL", 1);
        AddHeading(headings, "USL", 2);

        var rows = new ItemsControl
        {
            Name = "CapabilityVariableRows",
            ItemsSource = setup.Variables,
            ItemTemplate = new FuncDataTemplate<CapabilityVariableViewModel>((variable, _) => variable is null ? null : Row(variable))
        };

        var panel = new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "Variables / Specifications", FontWeight = FontWeight.SemiBold },
                headings,
                new ScrollViewer
                {
                    Height = 210,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = rows
                }
            }
        };

        Grid.SetColumn(panel, 0);
        return panel;
    }

    private static Control Row(CapabilityVariableViewModel variable)
    {
        var selection = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        selection.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(CapabilityVariableViewModel.IsSelected))
        {
            Source = variable,
            Mode = BindingMode.TwoWay
        });
        selection.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = variable.Name, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = variable.DataTypeName,
                    Opacity = 0.6,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions($"{VariableColumnWidth},{LimitColumnWidth},{LimitColumnWidth}"),
            Margin = new Thickness(0, 2)
        };

        Grid.SetColumn(selection, 0);
        row.Children.Add(selection);
        row.Children.Add(Limit(variable, nameof(CapabilityVariableViewModel.LowerSpecificationLimitText), 1));
        row.Children.Add(Limit(variable, nameof(CapabilityVariableViewModel.UpperSpecificationLimitText), 2));
        return row;
    }

    private static Control Limit(CapabilityVariableViewModel variable, string property, int column)
    {
        var editor = new TextBox
        {
            Margin = new Thickness(4, 0, 0, 0),
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            PlaceholderText = "—"
        };

        editor.Bind(TextBox.TextProperty, new Binding(property) { Source = variable, Mode = BindingMode.TwoWay });
        Grid.SetColumn(editor, column);
        return editor;
    }

    // Grouping and the statistics the result table shows. Switching a statistic off only hides its column.
    private static Control Options(CapabilityAnalysisSetupViewModel setup)
    {
        var selector = new ComboBox
        {
            ItemsSource = setup.GroupOptions,
            MinWidth = 200,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemTemplate = new FuncDataTemplate<AnalysisColumnOption>((option, _) => option is null
                ? null
                : new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = option.Name },
                        new TextBlock
                        {
                            Text = option.DataTypeName,
                            Opacity = 0.6,
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center
                        }
                    }
                })
        };
        selector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(nameof(CapabilityAnalysisSetupViewModel.SelectedGroup))
        {
            Source = setup,
            Mode = BindingMode.TwoWay
        });

        var statistics = new ItemsControl
        {
            Name = "CapabilityStatistics",
            ItemsSource = setup.Statistics,
            ItemTemplate = new FuncDataTemplate<CapabilityStatisticViewModel>((statistic, _) =>
            {
                if (statistic is null)
                {
                    return null;
                }

                var check = new CheckBox { Content = statistic.DisplayName, MinHeight = 0, Margin = new Thickness(0, 1) };
                check.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(CapabilityStatisticViewModel.IsSelected))
                {
                    Source = statistic,
                    Mode = BindingMode.TwoWay
                });
                return check;
            })
        };

        var panel = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                new TextBlock { Text = "Categorical variable for grouping", FontWeight = FontWeight.SemiBold },
                selector,
                new TextBlock { Text = "Optional.", Opacity = 0.6, FontSize = 11, Margin = new Thickness(0, 0, 0, 8) },
                new TextBlock { Text = "Display statistics", FontWeight = FontWeight.SemiBold },
                statistics
            }
        };

        // Which rows the analysis uses (Task #053): a compact row under the grouping - the conditions are set in a dialog.
        if (setup.SupportsFilter)
        {
            var filter = RowFilterSetupRow.Create(
                setup, nameof(CapabilityAnalysisSetupViewModel.FilterSummary), setup.CreateFilterEditor, edited => setup.Filter = edited.Filter);
            filter.Margin = new Thickness(0, 0, 0, 8);
            panel.Children.Insert(3, filter);
        }

        Grid.SetColumn(panel, 2);
        return panel;
    }

    private static void AddHeading(Grid headings, string text, int column)
    {
        var heading = new TextBlock
        {
            Text = text,
            Opacity = 0.7,
            FontSize = 11,
            HorizontalAlignment = column == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 0 ? 0 : 6, 0)
        };

        Grid.SetColumn(heading, column);
        headings.Children.Add(heading);
    }

    private void Confirm()
    {
        if (_setup.Confirm() is { } configuration)
        {
            Close(configuration);
        }
    }
}
