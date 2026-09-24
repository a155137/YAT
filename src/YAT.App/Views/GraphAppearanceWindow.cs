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

// The appearance dialog (Task #046): the graph setup's Appearance... and a graph window's Edit Appearance... are this
// one dialog, with the one appearance editor, OK and Cancel. OK is available only while the appearance is valid - every
// colour "#RRGGBB", a custom palette of one to sixteen colours - and the dialog says why when it is not. Modal to the
// window it was opened from.
internal sealed class GraphAppearanceWindow : Window
{
    private GraphAppearanceWindow(GraphAppearanceEditorViewModel appearance)
    {
        Title = "Appearance";
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
            if (appearance.IsValid && appearance.Options is { } options)
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

        // Why OK is not available: shown only while the appearance cannot be confirmed.
        void ShowValidity()
        {
            var message = appearance.ValidationMessage;
            confirm.IsEnabled = message is null;
            validation.Text = message;
            validation.IsVisible = message is not null;
        }

        appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphAppearanceEditorViewModel.ValidationMessage))
            {
                ShowValidity();
            }
        };
        ShowValidity();

        var editor = GraphAppearanceEditor.Create(appearance);
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
                    Text = GraphAppearanceEditorViewModel.Note,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 420
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

    // Shows the dialog over its owner and returns the confirmed appearance, or null when it was cancelled.
    public static Task<GraphAppearanceOptions?> ShowAsync(Window owner, GraphAppearanceEditorViewModel appearance) =>
        new GraphAppearanceWindow(appearance).ShowDialog<GraphAppearanceOptions?>(owner);
}

// The Edit Appearance dialog of one graph window, owned by it.
internal sealed class AvaloniaGraphAppearanceDialog : IGraphAppearanceDialog
{
    private readonly Window _owner;

    public AvaloniaGraphAppearanceDialog(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<GraphAppearanceOptions?> EditAsync(GraphTypeDefinition definition, GraphAppearanceOptions current) =>
        GraphAppearanceWindow.ShowAsync(_owner, new GraphAppearanceEditorViewModel(definition, current));
}
