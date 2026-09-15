using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.Lifecycle;

namespace YAT.app.Views;

// A small modal message window for the project lifecycle: the Save / Discard / Cancel prompt, or an error with OK.
// Closing it without choosing returns SaveChangesChoice.Cancel.
internal sealed class LifecycleMessageWindow : Window
{
    private LifecycleMessageWindow(string title, string message, string? detail, params (string Text, SaveChangesChoice Choice, bool IsDefault)[] buttons)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        MinWidth = 360;
        MaxWidth = 520;

        var text = new StackPanel { Spacing = 6 };
        text.Children.Add(new TextBlock { Text = message, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (detail is not null)
        {
            text.Children.Add(new TextBlock { Text = detail, Opacity = 0.7, TextWrapping = TextWrapping.Wrap });
        }

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var (buttonText, choice, isDefault) in buttons)
        {
            var button = new Button { Content = buttonText, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center, IsDefault = isDefault };
            if (isDefault)
            {
                button.Classes.Add("accent");
            }

            button.Click += (_, _) => Close(choice);
            buttonRow.Children.Add(button);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 18,
            Children = { text, buttonRow }
        };
    }

    public static Task<SaveChangesChoice> AskSaveChangesAsync(Window owner, string projectName, bool closing) =>
        new LifecycleMessageWindow(
                "YAT",
                closing ? "Save this project before closing?" : "Save this project before continuing?",
                $"“{projectName}” has not been saved to a project file.",
                ("Save", SaveChangesChoice.Save, true),
                ("Discard", SaveChangesChoice.Discard, false),
                ("Cancel", SaveChangesChoice.Cancel, false))
            .ShowDialog<SaveChangesChoice>(owner);

    public static Task ShowErrorAsync(Window owner, string message) =>
        new LifecycleMessageWindow("YAT", message, null, ("OK", SaveChangesChoice.Cancel, true))
            .ShowDialog<SaveChangesChoice>(owner);
}
