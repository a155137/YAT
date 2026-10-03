using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The Edit X Scale / Edit Y Scale dialog of a graph window (Tasks #052, #054), opened by double-clicking the axis. Range:
// its minimum and maximum, each Auto (showing the graph's automatic value) or a typed value. Ticks: Auto, an interval or
// values of the user's own. Then Reset to Auto, Cancel and OK. OK stays unavailable while the range breaks the rules or
// does not fit the graph's automatic ends, or the ticks cannot be drawn over it, and the dialog says why; tick values
// off the range are allowed, and the dialog says so without refusing them. Reset to Auto changes the dialog only. Modal
// to the graph window it belongs to.
//
// Controls are named after the properties they edit (MinimumIsAuto, MinimumText, ...) so tests and probes can find them.
internal sealed class GraphAxisScaleWindow : Window
{
    private GraphAxisScaleWindow(GraphAxisScaleEditorViewModel scale)
    {
        DataContext = scale;
        Title = scale.Title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var validation = new TextBlock
        {
            Name = "Validation",
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        };

        var confirm = Button("OK", "OK");
        confirm.IsDefault = true;
        confirm.Classes.Add("accent");
        confirm.Click += (_, _) =>
        {
            if (scale.Edit is { } edit)
            {
                Close(edit);
            }
        };

        var cancel = Button("Cancel", "Cancel");
        cancel.IsCancel = true;
        cancel.Click += (_, _) => Close(null);

        var reset = Button("ResetToAuto", "Reset to Auto");
        reset.Click += (_, _) => scale.ResetToAuto();

        void ShowValidity()
        {
            var message = scale.ValidationMessage;
            confirm.IsEnabled = message is null;
            validation.Text = message;
            validation.IsVisible = message is not null;
        }

        var notice = new TextBlock
        {
            Name = "TickNotice",
            Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        };

        void ShowNotice()
        {
            notice.Text = scale.TickNotice;
            notice.IsVisible = scale.TickNotice is not null;
        }

        scale.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphAxisScaleEditorViewModel.ValidationMessage))
            {
                ShowValidity();
            }
            else if (e.PropertyName == nameof(GraphAxisScaleEditorViewModel.TickNotice))
            {
                ShowNotice();
            }
        };
        ShowValidity();
        ShowNotice();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,Auto") };
        AddRow(grid, 0, scale, "Minimum", nameof(GraphAxisScaleEditorViewModel.MinimumIsAuto), nameof(GraphAxisScaleEditorViewModel.MinimumText));
        AddRow(grid, 1, scale, "Maximum", nameof(GraphAxisScaleEditorViewModel.MaximumIsAuto), nameof(GraphAxisScaleEditorViewModel.MaximumText));

        var ticks = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto") };
        AddTickRow(ticks, 0, scale, "Auto", nameof(GraphAxisScaleEditorViewModel.TicksAreAuto), textProperty: null, 0);
        AddTickRow(ticks, 1, scale, "Interval", nameof(GraphAxisScaleEditorViewModel.TicksAreInterval), nameof(GraphAxisScaleEditorViewModel.IntervalText), 130);
        AddTickRow(ticks, 2, scale, "Custom", nameof(GraphAxisScaleEditorViewModel.TicksAreCustom), nameof(GraphAxisScaleEditorViewModel.ValuesText), 260);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
                new TextBlock { Name = "AxisLabel", Text = scale.AxisLabel, FontWeight = FontWeight.SemiBold },
                Section("Range"),
                grid,
                Section("Ticks"),
                ticks,
                notice,
                validation,
                new DockPanel
                {
                    LastChildFill = false,
                    Children =
                    {
                        Docked(reset, Dock.Left),
                        Docked(
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(24, 0, 0, 0), Children = { cancel, confirm } },
                            Dock.Right)
                    }
                }
            }
        };

        Opened += (_, _) => Dispatcher.UIThread.Post(() => confirm.Focus(), DispatcherPriority.Loaded);
    }

    // Shows the dialog over its graph window and returns the confirmed range and ticks of its axis, or null when it was
    // cancelled.
    public static Task<GraphAxisScaleEdit?> ShowAsync(Window owner, GraphAxisScaleEditorViewModel scale) =>
        new GraphAxisScaleWindow(scale).ShowDialog<GraphAxisScaleEdit?>(owner);

    private static TextBlock Section(string text) =>
        new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 4, 0, -6) };

    // One tick choice: its radio button and, for an interval or values, the field it is typed in - available only while
    // the choice is made.
    private static void AddTickRow(Grid grid, int row, GraphAxisScaleEditorViewModel scale, string caption, string choiceProperty, string? textProperty, double width)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var choice = new RadioButton { Name = choiceProperty, Content = caption, GroupName = "Ticks", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 14, 4) };
        choice.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(choiceProperty) { Source = scale, Mode = BindingMode.TwoWay });
        Add(grid, row, 0, choice);

        if (textProperty is null)
        {
            return;
        }

        var field = new TextBox
        {
            Name = textProperty,
            Width = width,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        field.Bind(TextBox.TextProperty, new Binding(textProperty) { Source = scale, Mode = BindingMode.TwoWay });
        field.Bind(Avalonia.Input.InputElement.IsEnabledProperty, new Binding(choiceProperty) { Source = scale });
        Add(grid, row, 1, field);

        if (scale.Unit is { } unit)
        {
            Add(grid, row, 2, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 4, 0, 4) });
        }
    }

    private static void AddRow(Grid grid, int row, GraphAxisScaleEditorViewModel scale, string caption, string autoProperty, string textProperty)
    {
        grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        Add(grid, row, 0, new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 14, 4) });

        var auto = new CheckBox { Name = autoProperty, Content = "Auto", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
        auto.Bind(Avalonia.Controls.Primitives.ToggleButton.IsCheckedProperty, new Binding(autoProperty) { Source = scale, Mode = BindingMode.TwoWay });
        Add(grid, row, 1, auto);

        var field = new TextBox
        {
            Name = textProperty,
            Width = 130,
            MinHeight = 0,
            Padding = new Thickness(6, 2),
            Margin = new Thickness(0, 4),
            VerticalAlignment = VerticalAlignment.Center
        };
        field.Bind(TextBox.TextProperty, new Binding(textProperty) { Source = scale, Mode = BindingMode.TwoWay });
        field.Bind(Avalonia.Input.InputElement.IsEnabledProperty, new Binding(autoProperty) { Source = scale, Converter = Avalonia.Data.Converters.BoolConverters.Not });
        Add(grid, row, 2, field);

        if (scale.Unit is { } unit)
        {
            Add(grid, row, 3, new TextBlock { Text = unit, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 4, 0, 4) });
        }
    }

    private static Button Button(string name, string content) =>
        new() { Name = name, Content = content, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }

    private static void Add(Grid grid, int row, int column, Control control)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        grid.Children.Add(control);
    }
}

// The Edit X Scale / Edit Y Scale dialog of one graph window, owned by it.
internal sealed class AvaloniaGraphAxisScaleDialog : IGraphAxisScaleDialog
{
    private readonly Window _owner;

    public AvaloniaGraphAxisScaleDialog(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<GraphAxisScaleEdit?> EditAsync(
        GraphTypeDefinition definition,
        GraphAxisField axis,
        GraphAxisRangeOptions currentRanges,
        GraphAxisTickOptions currentTicks,
        GraphRenderModel autoFrame) =>
        GraphAxisScaleWindow.ShowAsync(_owner, new GraphAxisScaleEditorViewModel(definition, axis, currentRanges, autoFrame, currentTicks));
}
