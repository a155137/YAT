using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The box plot options dialog (Task #047): the graph setup's Box Plot Options... and a box plot window's Edit Box
// Plot... are this one dialog, with the one box plot editor, OK and Cancel. OK is available only while the box width is
// a whole number from 20 to 90, and the dialog says why when it is not. Modal to the window it was opened from.
internal sealed class GraphBoxPlotWindow : Window
{
    private GraphBoxPlotWindow(GraphBoxPlotEditorViewModel boxPlot)
    {
        Title = "Box Plot Options";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var validation = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360
        };

        var confirm = new Button
        {
            Content = "OK",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        confirm.Classes.Add("accent");
        confirm.Click += (_, _) =>
        {
            if (boxPlot.Options is { } options)
            {
                Close(options);
            }
        };

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Close(null);

        // Why OK is not available: shown only while the options cannot be confirmed.
        void ShowValidity()
        {
            var message = boxPlot.ValidationMessage;
            confirm.IsEnabled = message is null;
            validation.Text = message;
            validation.IsVisible = message is not null;
        }

        boxPlot.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphBoxPlotEditorViewModel.ValidationMessage))
            {
                ShowValidity();
            }
        };
        ShowValidity();

        var editor = GraphBoxPlotEditor.Create(boxPlot);
        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
                editor,
                validation,
                new TextBlock
                {
                    Text = GraphBoxPlotEditorViewModel.Note,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 360
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { confirm, cancel }
                }
            }
        };

        // Once the dialog is on screen and laid out: onto the box width.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => editor.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Loaded);
    }

    // Shows the dialog over its owner and returns the confirmed options, or null when it was cancelled.
    public static Task<BoxPlotOptions?> ShowAsync(Window owner, GraphBoxPlotEditorViewModel boxPlot) =>
        new GraphBoxPlotWindow(boxPlot).ShowDialog<BoxPlotOptions?>(owner);
}

// The Edit Box Plot dialog of one graph window, owned by it.
internal sealed class AvaloniaGraphBoxPlotDialog : IGraphBoxPlotDialog
{
    private readonly Window _owner;

    public AvaloniaGraphBoxPlotDialog(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<BoxPlotOptions?> EditAsync(BoxPlotOptions current) =>
        GraphBoxPlotWindow.ShowAsync(_owner, new GraphBoxPlotEditorViewModel(current));
}
