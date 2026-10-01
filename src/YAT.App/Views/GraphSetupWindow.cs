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

        // Sized to its content when it opens; from then on only its height follows, so a reason for OK being unavailable
        // never widens it as it changes.
        Opened += (_, _) => SizeToContent = SizeToContent.Height;

        // Why OK is unavailable, said as the setup is edited (Task #048): on the worksheet's line, to its right, on that
        // one line, so the dialog is no taller when there is something to say - a row of its own, or a second line, would
        // take the box plot's setup past a 1280 x 720 screen. The reason keeps the room it needs and the worksheet's name
        // gives way to it; a reason longer than the whole line would be cut short, and is then shown whole on hover.
        _validation = new TextBlock
        {
            Name = "ValidationMessage",
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(16, 0, 0, 0)
        };
        DockPanel.SetDock(_validation, Dock.Right);
        ShowValidation(setup.ValidationMessage);

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

        // The appearance is chosen in a dialog of its own, from a button beside OK and Cancel (Task #046): a row of its
        // own would make the setup taller than a 1280 x 720 screen allows.
        var appearance = new Button
        {
            Name = "Appearance",
            Content = "Appearance...",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsVisible = setup.SupportsAppearance
        };
        appearance.Click += async (_, _) =>
        {
            var edited = await GraphAppearanceWindow.ShowAsync(
                this,
                new GraphAppearanceEditorViewModel(GraphTypeDefinitions.For(setup.GraphType), setup.Appearance));
            if (edited is not null)
            {
                setup.Appearance = edited;
            }
        };

        // A box plot's own options too (Task #047), from a button beside Appearance...: inline they would make the
        // setup taller than a 1280 x 720 screen allows.
        var boxPlot = new Button
        {
            Name = "BoxPlotOptions",
            Content = "Box Plot Options...",
            MinWidth = 88,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsVisible = setup.SupportsBoxPlotControls
        };
        boxPlot.Click += async (_, _) =>
        {
            var edited = await GraphBoxPlotWindow.ShowAsync(this, new GraphBoxPlotEditorViewModel(setup.BoxPlot));
            if (edited is not null)
            {
                setup.BoxPlot = edited;
            }
        };

        setup.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphSetupViewModel.CanConfirm))
            {
                _confirm.IsEnabled = setup.CanConfirm;
            }
            else if (e.PropertyName == nameof(GraphSetupViewModel.ValidationMessage))
            {
                ShowValidation(setup.ValidationMessage);
            }
        };

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
                new DockPanel
                {
                    Children =
                    {
                        _validation,
                        new TextBlock
                        {
                            Text = $"Worksheet: {setup.WorksheetName}",
                            Opacity = 0.7,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        }
                    }
                },
                RolesAndOptions(setup),
                new DockPanel
                {
                    LastChildFill = false,
                    Children =
                    {
                        appearance,
                        boxPlot,
                        Docked(
                            new StackPanel
                            {
                                Orientation = Orientation.Horizontal,
                                Spacing = 8,
                                Children = { _confirm, cancel }
                            },
                            Dock.Right)
                    }
                }
            }
        };
    }

    private void ShowValidation(string? message)
    {
        _validation.Text = message;
        _validation.IsVisible = !string.IsNullOrEmpty(message);
        ToolTip.SetTip(_validation, message);
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    // Shows the dialog and returns what was confirmed - the configuration and how its variables are drawn - or null
    // when it was cancelled.
    public static Task<GraphSetupRequest?> ShowAsync(Window owner, GraphSetupViewModel setup) =>
        new GraphSetupWindow(setup).ShowDialog<GraphSetupRequest?>(owner);

    // One grid for every role, so the selectors line up under each other whatever the roles are called: the label
    // column takes the width of the longest label ("Categorical variable for grouping" is a good deal longer than
    // "X-axis"), and the selectors share what is left. Where the graph type can draw several variables together or
    // separately, that choice follows the role the variables are picked in.
    private static Control Roles(GraphSetupViewModel setup)
    {
        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            VerticalAlignment = VerticalAlignment.Top
        };

        var row = 0;
        foreach (var role in setup.Roles)
        {
            // A role that takes several columns is picked from a list; one that takes a single column keeps its
            // selector. Which of the two is read from the role, so no graph type is named here.
            var selector = role.AllowsMultiple ? MultipleSelector(role) : SingleSelector(role);
            AddRoleRow(panel, row++, role.DisplayName, selector);

            if (role.AllowsMultiple && setup.SupportsVariableLayout)
            {
                AddRoleRow(panel, row++, "Display", Display(setup));
            }
        }

        // Which rows the graph uses (Task #049): a row of its own, not part of the group, with the values chosen in a
        // dialog of their own - a list of values inline would take the setup past a 1280 x 720 screen.
        if (setup.SupportsFilter)
        {
            AddRoleRow(panel, row, "Filter", FilterRow(setup));
        }

        return panel;
    }

    // The filter in a few words, and Edit... to change it.
    private static Control FilterRow(GraphSetupViewModel setup)
    {
        var summary = new TextBlock
        {
            Name = "FilterSummary",
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(GraphSetupViewModel.FilterSummary)) { Source = setup });
        summary.Bind(ToolTip.TipProperty, new Binding(nameof(GraphSetupViewModel.FilterSummary)) { Source = setup });

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

            // Cancel leaves the filter as it was; Apply sets it - to none when every row is kept.
            if (await GraphFilterWindow.ShowAsync(owner, setup.CreateFilterEditor()) is { } edited)
            {
                setup.Filter = edited.Filter;
            }
        };

        return new DockPanel { Children = { Docked(edit, Dock.Right), summary } };
    }

    private static void AddRoleRow(Grid panel, int row, string text, Control selector)
    {
        panel.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var label = new TextBlock
        {
            Text = text,
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
    }

    // Together or Separate: one graph with every variable, or a graph for each. It only does something with two or
    // more variables selected, so until then it is shown but not available.
    private static Control Display(GraphSetupViewModel setup)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (var (text, property) in new[]
                 {
                     ("Together", nameof(GraphSetupViewModel.IsTogether)),
                     ("Separate", nameof(GraphSetupViewModel.IsSeparate))
                 })
        {
            var choice = new RadioButton { Content = text, GroupName = "Display", Name = property };
            choice.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Source = setup, Mode = BindingMode.TwoWay });
            choice.Bind(IsEnabledProperty, new Binding(nameof(GraphSetupViewModel.IsLayoutEnabled)) { Source = setup });
            row.Children.Add(choice);
        }

        return row;
    }

    // The roles, then the options the graph type offers. An option the graph type does not have is left out entirely
    // rather than shown disabled; which options exist is read from the graph type's capabilities. The roles' own
    // selectors are where columns are picked: there is no separate list of the worksheet's columns (Task #046.2).
    //
    // A graph type with options of its own shows them in two columns - its own bins, statistics, fitted line and legend
    // on the left, the specification, the labels and the axis ranges on the right - to keep the dialog within a
    // 1280 x 720 screen; one without (a scatter plot, a box plot) shows the right-hand ones alone, the legend last.
    private static Control RolesAndOptions(GraphSetupViewModel setup)
    {
        var column = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        column.Children.Add(Roles(setup));

        if (!setup.SupportsStatisticsPanel && !setup.SupportsFittedLine && !setup.SupportsSpecificationLines
            && !setup.SupportsHistogramControls && !setup.SupportsLabels && !setup.SupportsAxisRanges
            && !setup.SupportsLegend)
        {
            return column;
        }

        var options = new StackPanel { Spacing = 4 };
        options.Children.Add(new TextBlock { Text = "Options", FontWeight = FontWeight.SemiBold });
        var hasOwnOptions =
            setup.SupportsHistogramControls || setup.SupportsStatisticsPanel || setup.SupportsFittedLine;
        var second = hasOwnOptions ? new StackPanel { Spacing = 4 } : options;

        if (setup.SupportsHistogramControls)
        {
            options.Children.Add(HistogramControls(setup));
            options.Children.Add(Option(setup, "Show normal fit", nameof(GraphSetupViewModel.ShowNormalFit)));
        }

        // The statistics options take one row, where a single check box used to be (Task #045).
        if (setup.SupportsStatisticsPanel)
        {
            options.Children.Add(GraphStatisticsEditor.Create(setup.Statistics));
        }

        if (setup.SupportsFittedLine)
        {
            options.Children.Add(Option(setup, "Show fitted line", nameof(GraphSetupViewModel.ShowFittedLine)));
        }

        // The legend closes the graph type's own options on the left - or, without them, the options on their own.
        var legend = setup.SupportsLegend ? Legend(setup) : null;
        if (legend is not null && hasOwnOptions)
        {
            options.Children.Add(legend);
        }

        if (setup.SupportsSpecificationLines)
        {
            second.Children.Add(new TextBlock { Text = "Specification", Margin = new Thickness(0, 6, 0, 0) });
            second.Children.Add(Specification(setup));
        }

        if (setup.SupportsLabels)
        {
            second.Children.Add(new TextBlock { Text = "Labels", Margin = new Thickness(0, 6, 0, 0) });
            second.Children.Add(GraphLabelsEditor.Create(setup.Labels));
        }

        if (setup.SupportsAxisRanges)
        {
            second.Children.Add(new TextBlock { Text = "Axes", Margin = new Thickness(0, 6, 0, 0) });
            second.Children.Add(GraphAxesEditor.Create(setup.Axes));
        }

        if (legend is not null && !hasOwnOptions)
        {
            second.Children.Add(legend);
        }

        if (ReferenceEquals(second, options))
        {
            column.Children.Add(options);
        }
        else
        {
            var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,24,Auto") };
            Grid.SetColumn(second, 2);
            columns.Children.Add(options);
            columns.Children.Add(second);
            column.Children.Add(columns);
        }

        return column;
    }

    // The legend options under their heading.
    private static Control Legend(GraphSetupViewModel setup) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = "Legend", Margin = new Thickness(0, 6, 0, 0) },
            GraphLegendEditor.Create(setup.Legend)
        }
    };

    // One on/off option, named after the view model property it edits.
    private static CheckBox Option(GraphSetupViewModel setup, string text, string property)
    {
        var option = new CheckBox { Content = text, Name = property };
        option.Bind(ToggleButton.IsCheckedProperty, new Binding(property) { Source = setup, Mode = BindingMode.TwoWay });
        return option;
    }

    // The histogram's own choices: what its bars measure, and how its bins are chosen. The number fields belong to one
    // binning mode each and are only editable while that mode is chosen; they stay in place so the dialog does not
    // change size as the mode changes.
    private static Control HistogramControls(GraphSetupViewModel setup)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto") };

        AddLabelled(grid, 0, "Y scale", Choice(setup, setup.YScaleChoices, nameof(GraphSetupViewModel.SelectedYScale)));
        AddLabelled(grid, 1, "Bins", Choice(setup, setup.BinningChoices, nameof(GraphSetupViewModel.SelectedBinning)));

        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 2) };
        AddField(fields, setup, "Number", nameof(GraphSetupViewModel.BinCountText),
            placeholder: "30", enabledProperty: nameof(GraphSetupViewModel.IsBinCountEnabled));
        AddField(fields, setup, "Width", nameof(GraphSetupViewModel.BinWidthText), leftMargin: 10,
            placeholder: "100", enabledProperty: nameof(GraphSetupViewModel.IsBinWidthAndStartEnabled));
        AddField(fields, setup, "Start", nameof(GraphSetupViewModel.BinStartText), leftMargin: 10,
            placeholder: "14000", enabledProperty: nameof(GraphSetupViewModel.IsBinWidthAndStartEnabled));
        Grid.SetRow(fields, 2);
        Grid.SetColumn(fields, 1);
        grid.Children.Add(fields);

        return grid;
    }

    private static void AddLabelled(Grid grid, int row, string label, Control control)
    {
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 12, 3) };
        Grid.SetRow(text, row);
        grid.Children.Add(text);

        control.Margin = new Thickness(0, 3);
        Grid.SetRow(control, row);
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
    }

    // A list of choices bound to one view model property, named after it.
    private static ComboBox Choice<T>(GraphSetupViewModel setup, IReadOnlyList<SetupChoice<T>> choices, string property)
    {
        var selector = new ComboBox { ItemsSource = choices, MinWidth = 180, Name = property };
        selector.Bind(SelectingItemsControl.SelectedItemProperty, new Binding(property) { Source = setup, Mode = BindingMode.TwoWay });
        return selector;
    }

    // LSL, Target and USL on one line. Blank fields draw nothing, so there is no separate switch for the lines.
    private static Control Specification(GraphSetupViewModel setup)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        AddField(row, setup, "LSL", nameof(GraphSetupViewModel.LowerLimitText));
        AddField(row, setup, "Target", nameof(GraphSetupViewModel.TargetText), leftMargin: 10);
        AddField(row, setup, "USL", nameof(GraphSetupViewModel.UpperLimitText), leftMargin: 10);
        return row;
    }

    // One labelled number field. enabledProperty: a view model flag the field is editable under, if it is not always.
    private static void AddField(
        StackPanel row,
        GraphSetupViewModel setup,
        string label,
        string property,
        double leftMargin = 0,
        string placeholder = "—",
        string? enabledProperty = null)
    {
        row.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(leftMargin, 0, 0, 0)
        });

        var editor = new TextBox
        {
            Width = 80,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            HorizontalContentAlignment = HorizontalAlignment.Right,
            PlaceholderText = placeholder
        };

        // Named after its field, so the dialog can be driven and inspected by the field it edits.
        editor.Name = property;
        editor.Bind(TextBox.TextProperty, new Binding(property) { Source = setup, Mode = BindingMode.TwoWay });
        if (enabledProperty is not null)
        {
            editor.Bind(IsEnabledProperty, new Binding(enabledProperty) { Source = setup });
        }

        row.Children.Add(editor);
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

            // About three and a half rows, so a part-shown row says the list scrolls: with the Filter row (Task #049) the
            // setup of a box plot or a distribution graph still fits a 1280 x 720 screen.
            MaxHeight = 136,
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
        if (_setup.ConfirmRequest() is { } request)
        {
            Close(request);
        }
    }
}
