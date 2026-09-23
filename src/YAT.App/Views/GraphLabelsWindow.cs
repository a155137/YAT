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

// The Edit Labels dialog of a graph window: the same label editor as the graph setup, with OK and Cancel. OK stays
// unavailable while the labels break the label rules, and the dialog says why in the setup's words. Modal to the graph
// window it belongs to.
//
// Opened on a title the user picked on the graph, it puts the caret in that title's text box with the text selected,
// so typing replaces it.
internal sealed class GraphLabelsWindow : Window
{
    private GraphLabelsWindow(GraphLabelsEditorViewModel labels, GraphTypeDefinition definition, GraphLabelField? focus)
    {
        Title = "Edit Labels";
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
            if (labels.IsValid)
            {
                Close(labels.Options);
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
            confirm.IsEnabled = labels.IsValid;
            validation.Text = labels.IsValid ? null : GraphValidationMessages.For(labels.Errors[0], definition);
            validation.IsVisible = !labels.IsValid;
        }

        labels.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(GraphLabelsEditorViewModel.IsValid))
            {
                ShowValidity();
            }
        };
        ShowValidity();

        var editor = GraphLabelsEditor.Create(labels);
        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 14,
            Children =
            {
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

        // Once the dialog is on screen and laid out: into the picked title's text, all of it selected, or onto the
        // first row's mode when no title was picked.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () =>
            {
                if (focus is { } field
                    && editor.GetLogicalDescendants().OfType<TextBox>()
                        .FirstOrDefault(box => box.Name == GraphLabelsEditor.TextBoxName(field)) is { } text
                    && text.IsEnabled)
                {
                    text.Focus();
                    text.SelectAll();
                    return;
                }

                editor.GetLogicalDescendants().OfType<ComboBox>().FirstOrDefault()?.Focus();
            },
            DispatcherPriority.Loaded);
    }

    // Shows the dialog over its graph window and returns the confirmed labels, or null when it was cancelled.
    public static Task<GraphLabelOptions?> ShowAsync(
        Window owner,
        GraphLabelsEditorViewModel labels,
        GraphTypeDefinition definition,
        GraphLabelField? focus) =>
        new GraphLabelsWindow(labels, definition, focus).ShowDialog<GraphLabelOptions?>(owner);
}

// The Edit Labels dialog of one graph window, owned by it.
internal sealed class AvaloniaGraphLabelsDialog : IGraphLabelsDialog
{
    private readonly Window _owner;

    public AvaloniaGraphLabelsDialog(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public Task<GraphLabelOptions?> EditAsync(
        GraphTypeDefinition definition,
        GraphLabelOptions current,
        GraphLabelField? focus,
        string? shownText)
    {
        var labels = new GraphLabelsEditorViewModel(current);
        if (focus is { } field)
        {
            labels.BeginEditing(field, shownText);
        }

        return GraphLabelsWindow.ShowAsync(_owner, labels, definition, focus);
    }
}
