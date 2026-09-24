using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The Edit Legend dialog of a graph window (Task #044): the same legend editor as the graph setup, with OK and Cancel.
// Every choice it offers is a valid one, so OK is always available. Modal to the graph window it belongs to.
internal sealed class GraphLegendWindow : Window
{
    private GraphLegendWindow(GraphLegendEditorViewModel legend)
    {
        Title = "Edit Legend";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var confirm = new Button
        {
            Content = "OK",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsDefault = true
        };
        confirm.Classes.Add("accent");
        confirm.Click += (_, _) => Close(legend.Options);

        var cancel = new Button
        {
            Content = "Cancel",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            IsCancel = true
        };
        cancel.Click += (_, _) => Close(null);

        var editor = GraphLegendEditor.Create(legend);
        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
                editor,
                new TextBlock
                {
                    Text = GraphLegendEditorViewModel.Note,
                    Opacity = 0.7,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
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

        // Once the dialog is on screen and laid out: onto the first list.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => editor.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Loaded);
    }

    // Shows the dialog over its graph window and returns the confirmed options, or null when it was cancelled.
    public static Task<GraphLegendOptions?> ShowAsync(Window owner, GraphLegendEditorViewModel legend) =>
        new GraphLegendWindow(legend).ShowDialog<GraphLegendOptions?>(owner);
}

// The Edit Legend dialog of one graph window, owned by it.
internal sealed class AvaloniaGraphLegendDialog : IGraphLegendDialog
{
    private readonly Window _owner;

    public AvaloniaGraphLegendDialog(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<GraphLegendOptions?> EditAsync(GraphLegendOptions current) =>
        GraphLegendWindow.ShowAsync(_owner, new GraphLegendEditorViewModel(current));
}
