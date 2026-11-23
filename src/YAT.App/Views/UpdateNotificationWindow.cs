using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// Shown when an automatic startup check finds a newer version of YAT. Displays Current / New version and plain-text
// Release Notes inside a scrollable read-only box (no HTML or script execution), with Update Now, Later, and View on GitHub.
internal sealed class UpdateNotificationWindow : Window
{
    private UpdateNotificationWindow(UpdateNotificationViewModel viewModel)
    {
        DataContext = viewModel;
        Title = "Update Available";
        Width = 540;
        MinHeight = 320;
        MaxHeight = 620;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var headline = new TextBlock
        {
            Name = "NotificationHeadline",
            Text = $"YAT {viewModel.NewVersion} is available.",
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            TextWrapping = TextWrapping.Wrap
        };

        var versions = new TextBlock
        {
            Name = "NotificationVersions",
            Text = $"Current version: {viewModel.CurrentVersion}    |    New version: {viewModel.NewVersion}",
            Opacity = 0.85,
            TextWrapping = TextWrapping.Wrap
        };

        var notesLabel = new TextBlock
        {
            Text = "Release Notes",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var notesBox = new TextBox
        {
            Name = "ReleaseNotesBox",
            Text = viewModel.ReleaseNotes,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 240,
            VerticalContentAlignment = VerticalAlignment.Top
        };

        var viewOnGitHub = new Button
        {
            Name = "ViewOnGitHubButton",
            Content = "View on GitHub",
            Command = viewModel.ViewOnGitHubCommand,
            IsVisible = viewModel.HasReleaseNotesUrl,
            MinWidth = 110,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        DockPanel.SetDock(viewOnGitHub, Dock.Left);

        var later = new Button
        {
            Name = "LaterButton",
            Content = "Later",
            Command = viewModel.LaterCommand,
            IsCancel = true,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };

        var updateNow = new Button
        {
            Name = "UpdateNowButton",
            Content = "Update Now",
            Command = viewModel.UpdateNowCommand,
            IsDefault = true,
            MinWidth = 104,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        updateNow.Classes.Add("accent");

        var rightButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { later, updateNow }
        };

        var buttonBar = new DockPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Children = { viewOnGitHub, rightButtons }
        };

        viewModel.CloseRequested += (_, choice) => Close(choice);

        Content = new StackPanel
        {
            Margin = new Thickness(20, 18),
            Spacing = 10,
            Children =
            {
                headline,
                versions,
                notesLabel,
                notesBox,
                buttonBar
            }
        };
    }

    public static async Task<UpdateNotificationChoice> ShowAsync(Window owner, UpdateNotificationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(viewModel);

        var result = await new UpdateNotificationWindow(viewModel).ShowDialog<UpdateNotificationChoice?>(owner);
        return result ?? viewModel.Choice;
    }
}
