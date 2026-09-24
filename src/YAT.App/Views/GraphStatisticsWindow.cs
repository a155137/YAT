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

// The Edit Statistics dialog of a graph window (Task #045): the same statistics editor as the graph setup, with OK and
// Cancel. OK is available only while the options are valid - a shown panel needs at least one statistic - and the
// dialog says why when they are not. Modal to the graph window it belongs to.
internal sealed class GraphStatisticsWindow : Window
{
    private GraphStatisticsWindow(GraphStatisticsEditorViewModel statistics)
    {
        Title = "Edit Statistics";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var validation = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 420
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
            if (statistics.IsValid)
            {
                Close(statistics.Options);
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
            var message = statistics.ValidationMessage;
            confirm.IsEnabled = message is null;
            validation.Text = message;
            validation.IsVisible = message is not null;
        }

        statistics.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphStatisticsEditorViewModel.ValidationMessage))
            {
                ShowValidity();
            }
        };
        ShowValidity();

        var editor = GraphStatisticsEditor.Create(statistics);
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
                    Text = GraphStatisticsEditorViewModel.Note,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 320
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

        // Once the dialog is on screen and laid out: onto the list.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => editor.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Loaded);
    }

    // Shows the dialog over its graph window and returns the confirmed options, or null when it was cancelled.
    public static Task<GraphStatisticsOptions?> ShowAsync(Window owner, GraphStatisticsEditorViewModel statistics) =>
        new GraphStatisticsWindow(statistics).ShowDialog<GraphStatisticsOptions?>(owner);
}

// The Edit Statistics dialog of one graph window, owned by it.
internal sealed class AvaloniaGraphStatisticsDialog : IGraphStatisticsDialog
{
    private readonly Window _owner;

    public AvaloniaGraphStatisticsDialog(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<GraphStatisticsOptions?> EditAsync(GraphTypeDefinition definition, GraphStatisticsOptions current) =>
        GraphStatisticsWindow.ShowAsync(_owner, new GraphStatisticsEditorViewModel(definition, current));
}
