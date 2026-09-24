using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// The Edit Axes dialog of a graph window (Task #043): the same axis range editor as the graph setup, with the graph's
// automatic values shown in every blank field, and OK and Cancel. OK stays unavailable while the ranges break the
// rules or do not fit the graph's automatic ranges, and the dialog says why as the ranges are typed. Modal to the graph
// window it belongs to.
internal sealed class GraphAxesWindow : Window
{
    private GraphAxesWindow(GraphAxesEditorViewModel axes)
    {
        Title = "Edit Axes";
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
            if (axes.IsValid)
            {
                Close(axes.Options);
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

        void ShowValidity()
        {
            var message = axes.ValidationMessage;
            confirm.IsEnabled = message is null;
            validation.Text = message;
            validation.IsVisible = message is not null;
        }

        axes.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphAxesEditorViewModel.ValidationMessage))
            {
                ShowValidity();
            }
        };
        ShowValidity();

        var editor = GraphAxesEditor.Create(axes);
        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Leave a field blank for Auto.", Opacity = 0.7 },
                editor,
                validation,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { confirm, cancel }
                }
            }
        };

        // Once the dialog is on screen and laid out: into the first field.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => editor.GetLogicalDescendants().OfType<TextBox>().FirstOrDefault()?.Focus(),
            DispatcherPriority.Loaded);
    }

    // Shows the dialog over its graph window and returns the confirmed ranges, or null when it was cancelled.
    public static Task<GraphAxisRangeOptions?> ShowAsync(Window owner, GraphAxesEditorViewModel axes) =>
        new GraphAxesWindow(axes).ShowDialog<GraphAxisRangeOptions?>(owner);
}

// The Edit Axes dialog of one graph window, owned by it.
internal sealed class AvaloniaGraphAxesDialog : IGraphAxesDialog
{
    private readonly Window _owner;

    public AvaloniaGraphAxesDialog(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<GraphAxisRangeOptions?> EditAsync(
        GraphTypeDefinition definition,
        GraphAxisRangeOptions current,
        GraphRenderModel autoFrame) =>
        GraphAxesWindow.ShowAsync(_owner, new GraphAxesEditorViewModel(definition, current, autoFrame));
}
