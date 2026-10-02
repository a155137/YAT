using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using YAT.app.Updates;
using YAT.app.ViewModels;

namespace YAT.app.Views;

// Help > Check for Updates... (Task #051.B): one window through every step - checking, the result, downloading with its
// progress, the verified package and preparing its installation (Task #051.C) - with the buttons each step has:
// Download, Install Update, Cancel, Retry, Release Notes, Open Folder, Close. Closing the window stops whatever runs.
// Modal to the main window.
internal sealed class UpdateWindow : Window
{
    private UpdateWindow(UpdateViewModel update)
    {
        DataContext = update;
        Title = "Check for Updates";
        Width = 500;
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
        var install = Command("Install", "Install Update", update.InstallCommand, accent: true);
        var close = new Button { Name = "Close", Content = "Close", MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center, IsCancel = true };
        close.Click += (_, _) => Close();

        void Show()
        {
            var state = update.State;
            detail.IsVisible = update.Detail is not null;
            available.IsVisible = state is UpdateWindowState.UpdateAvailable or UpdateWindowState.Downloading or UpdateWindowState.Verifying or UpdateWindowState.Verified
                or UpdateWindowState.Installing
                || (state is UpdateWindowState.RemoteVersionOlder && update.AvailableVersion is not null);
            size.IsVisible = state is UpdateWindowState.UpdateAvailable or UpdateWindowState.Downloading;
            progress.IsVisible = state is UpdateWindowState.Downloading or UpdateWindowState.Verifying or UpdateWindowState.Installing;
            progress.IsIndeterminate = state is UpdateWindowState.Checking or UpdateWindowState.Installing;
            progressText.IsVisible = state is UpdateWindowState.Downloading or UpdateWindowState.Verifying;
            releaseNotes.IsVisible = update.ReleaseNotesUrl is not null && state is not (UpdateWindowState.Checking or UpdateWindowState.Failed);
            download.IsVisible = state is UpdateWindowState.UpdateAvailable;
            cancel.IsVisible = update.IsBusy;
            retry.IsVisible = state is UpdateWindowState.Failed;
            openFolder.IsVisible = update.Package is not null && state is UpdateWindowState.Verified or UpdateWindowState.Failed;
            install.IsVisible = state is UpdateWindowState.Verified && update.CanInstall;
            close.IsVisible = !update.IsBusy;
        }

        update.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdateViewModel.State) or nameof(UpdateViewModel.Detail) or nameof(UpdateViewModel.ReleaseNotesUrl)
                or nameof(UpdateViewModel.AvailableVersion) or nameof(UpdateViewModel.Package))
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
                        Docked(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { download, install, retry, openFolder, cancel, close } }, Dock.Right)
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

// The update window of the main window, and the system's browser and file explorer through Avalonia's launcher. For
// Install Update (Task #051.C): the confirmation, YAT's own close decision (MainWindow's) and the end of the application.
public sealed class AvaloniaUpdateDialogs : IUpdateDialogs, IUpdateLauncher, IUpdatePrompts
{
    private readonly Window _owner;
    private readonly IClassicDesktopStyleApplicationLifetime? _lifetime;

    public AvaloniaUpdateDialogs(Window owner, IClassicDesktopStyleApplicationLifetime? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
        _lifetime = lifetime;
    }

    public IUpdateLauncher Launcher => this;

    public IUpdatePrompts Prompts => this;

    // Asked over the update window.
    public Task<bool> ConfirmInstallAsync(string version) =>
        LifecycleMessageWindow.ConfirmAsync(
            _owner.OwnedWindows.OfType<UpdateWindow>().LastOrDefault() ?? _owner,
            $"Install YAT {version} now?",
            $"YAT will close, install v{version}, and restart. Unsaved changes will be handled before YAT closes.",
            "Install");

    public Task<bool> CloseApplicationForInstallAsync() =>
        _owner is MainWindow main ? main.CloseForUpdateAsync() : Task.FromResult(false);

    public void ExitApplication()
    {
        if (_lifetime is not null)
        {
            _lifetime.Shutdown();
        }
        else
        {
            _owner.Close();
        }
    }

    public Task ShowMessageAsync(string message) => LifecycleMessageWindow.ShowErrorAsync(_owner, message);

    public Task ShowAsync(UpdateViewModel update) => UpdateWindow.ShowAsync(_owner, update);

    // Only https: the release notes address comes from the update information, which allows nothing else.
    public Task<bool> OpenUriAsync(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps ? _owner.Launcher.LaunchUriAsync(uri) : Task.FromResult(false);

    public Task<bool> OpenFolderAsync(string folder) =>
        Directory.Exists(folder) ? _owner.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(folder)) : Task.FromResult(false);
}
