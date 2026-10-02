using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using YAT.app.Updates;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// Help > Check for Updates... (Task #051.B): one window through every step - checking, the result, downloading with its
// progress, and the verified package - with the buttons each step has: Download, Cancel, Retry, Release Notes, Open
// Folder, Close. There is no Install: a verified package waits for a future version of YAT. Closing the window stops
// whatever runs. Modal to the main window.
internal sealed class UpdateWindow : Window
{
    private UpdateWindow(UpdateViewModel update)
    {
        DataContext = update;
        Title = "Check for Updates";
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var headline = Bound(new TextBlock { Name = "UpdateHeadline", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap }, update, nameof(UpdateViewModel.Headline));
        var detail = Bound(new TextBlock { Name = "UpdateDetail", TextWrapping = TextWrapping.Wrap, Opacity = 0.85 }, update, nameof(UpdateViewModel.Detail));
        var installed = new TextBlock { Name = "InstalledVersion", Text = $"Current version: {update.InstalledVersion}" };
        var available = Bound(new TextBlock { Name = "AvailableVersion" }, update, nameof(UpdateViewModel.AvailableVersion), "Available version: {0}");
        var size = Bound(new TextBlock { Name = "DownloadSize" }, update, nameof(UpdateViewModel.DownloadSize), "Download size: {0}");
        var progress = new ProgressBar { Name = "UpdateProgress", Minimum = 0, Maximum = 100, Height = 6 };
        progress.Bind(Avalonia.Controls.Primitives.RangeBase.ValueProperty, new Binding(nameof(UpdateViewModel.ProgressPercent)) { Source = update });
        var progressText = Bound(new TextBlock { Name = "UpdateProgressText", Opacity = 0.75 }, update, nameof(UpdateViewModel.ProgressText));

        var releaseNotes = Command("ReleaseNotes", "Release Notes", update.OpenReleaseNotesCommand);
        var download = Command("Download", "Download", update.DownloadCommand, accent: true);
        var cancel = Command("Cancel", "Cancel", update.CancelCommand);
        var retry = Command("Retry", "Retry", update.RetryCommand);
        var openFolder = Command("OpenFolder", "Open Folder", update.OpenFolderCommand);
        var close = new Button { Name = "Close", Content = "Close", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        close.Click += (_, _) => Close();

        void Show()
        {
            var state = update.State;
            detail.IsVisible = update.Detail is not null;
            available.IsVisible = state is UpdateWindowState.UpdateAvailable or UpdateWindowState.Downloading or UpdateWindowState.Verifying or UpdateWindowState.Verified
                || (state is UpdateWindowState.RemoteVersionOlder && update.AvailableVersion is not null);
            size.IsVisible = state is UpdateWindowState.UpdateAvailable or UpdateWindowState.Downloading;
            progress.IsVisible = state is UpdateWindowState.Downloading or UpdateWindowState.Verifying;
            progress.IsIndeterminate = state is UpdateWindowState.Checking;
            progressText.IsVisible = state is UpdateWindowState.Downloading or UpdateWindowState.Verifying;
            releaseNotes.IsVisible = update.ReleaseNotesUrl is not null && state is not (UpdateWindowState.Checking or UpdateWindowState.Failed);
            download.IsVisible = state is UpdateWindowState.UpdateAvailable;
            cancel.IsVisible = update.IsBusy;
            retry.IsVisible = state is UpdateWindowState.Failed;
            openFolder.IsVisible = state is UpdateWindowState.Verified;
            close.IsVisible = !update.IsBusy;
        }

        update.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdateViewModel.State) or nameof(UpdateViewModel.Detail) or nameof(UpdateViewModel.ReleaseNotesUrl)
                or nameof(UpdateViewModel.AvailableVersion))
            {
                Show();
            }
        };
        update.CloseRequested += (_, _) => Close();
        Closing += (_, _) => update.Cancel();
        Show();

        Content = new StackPanel
        {
            Margin = new Thickness(22, 18),
            Spacing = 8,
            Children =
            {
                headline,
                detail,
                new StackPanel { Spacing = 2, Margin = new Thickness(0, 6, 0, 0), Children = { installed, available, size } },
                progress,
                progressText,
                new DockPanel
                {
                    Margin = new Thickness(0, 10, 0, 0),
                    LastChildFill = false,
                    Children =
                    {
                        Docked(releaseNotes, Dock.Left),
                        Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { download, retry, openFolder, cancel, close } }, Dock.Right)
                    }
                }
            }
        };
    }

    public static Task ShowAsync(Window owner, UpdateViewModel update) => new UpdateWindow(update).ShowDialog(owner);

    private static TextBlock Bound(TextBlock text, UpdateViewModel update, string property, string? format = null)
    {
        text.Bind(TextBlock.TextProperty, new Binding(property) { Source = update, StringFormat = format });
        return text;
    }

    private static Button Command(string name, string content, System.Windows.Input.ICommand command, bool accent = false)
    {
        var button = new Button { Name = name, Content = content, Command = command, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (accent)
        {
            button.Classes.Add("accent");
        }

        return button;
    }

    private static Control Docked(Control control, Dock dock)
    {
        DockPanel.SetDock(control, dock);
        return control;
    }
}

// The update window of the main window, and the system's browser and file explorer through Avalonia's launcher.
public sealed class AvaloniaUpdateDialogs : IUpdateDialogs, IUpdateLauncher
{
    private readonly Window _owner;

    public AvaloniaUpdateDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public IUpdateLauncher Launcher => this;

    public Task ShowAsync(UpdateViewModel update) => UpdateWindow.ShowAsync(_owner, update);

    // Only https: the release notes address comes from the update information, which allows nothing else.
    public Task<bool> OpenUriAsync(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ? _owner.Launcher.LaunchUriAsync(uri) : Task.FromResult(false);

    public Task<bool> OpenFolderAsync(string folder) =>
        Directory.Exists(folder) ? _owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)) : Task.FromResult(false);
}
