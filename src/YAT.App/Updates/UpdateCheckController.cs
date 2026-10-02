using System.Reflection;
using YAT.Application.Updates;
using YAT.app.ViewModels;

namespace YAT.app.Updates;

// Help > Check for Updates... (Task #051.B): one update window at a time, which checks as it opens and downloads only
// when the user asks. Closing the window stops whatever is still running. When the window closed for Install Update
// (Task #051.C), the updater is ready: YAT then closes the usual way - the save prompt included - and the updater is told
// to go; if the user keeps YAT open after all, it is told to change nothing.
//
// It knows nothing of HTTP or windows: the service comes from the composition root, the window through IUpdateDialogs.
public sealed class UpdateCheckController
{
    private readonly IUpdateDialogs _dialogs;
    private readonly UpdateCheckService _service;

    public UpdateCheckController(IUpdateDialogs dialogs, UpdateCheckService service)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(service);
        _dialogs = dialogs;
        _service = service;
    }

    // Whether an update window is open (only one may be).
    public bool IsRunning { get; private set; }

    public async Task CheckForUpdatesAsync()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        try
        {
            var update = new UpdateViewModel(_service, _dialogs.Launcher, _dialogs.Prompts);
            _ = update.CheckAsync();
            await _dialogs.ShowAsync(update);

            // The window is closed: whatever still runs is of no use.
            update.Cancel();
            await update.Completion;

            if (update.PendingInstall is { } handoff)
            {
                await InstallAsync(handoff, update.Package!.Version.ToString());
            }
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task InstallAsync(IUpdateHandoff handoff, string version)
    {
        using (handoff)
        {
            if (!await _dialogs.CloseApplicationForInstallAsync())
            {
                handoff.Cancel();
                return;
            }

            if (!handoff.Go())
            {
                // YAT's project is closed already; the updater is gone, so nothing will be installed.
                await _dialogs.ShowMessageAsync($"YAT {version} could not be installed: the updater stopped. YAT will now close; start it again.");
            }
        }

        _dialogs.ExitApplication();
    }
}

// The update window, and the system's browser and file explorer. The desktop implementation is Avalonia's; tests use a
// fake.
public interface IUpdateDialogs
{
    IUpdateLauncher Launcher { get; }

    IUpdatePrompts Prompts { get; }

    // Shows the update window until the user closes it.
    Task ShowAsync(UpdateViewModel update);

    // Runs YAT's usual close decision (the save prompt); true when YAT may close (its project is then closed).
    Task<bool> CloseApplicationForInstallAsync();

    // Ends YAT.
    void ExitApplication();

    Task ShowMessageAsync(string message);
}

// Asks the user (Task #051.C).
public interface IUpdatePrompts
{
    // "YAT will close, install vX.Y.Z, and restart." True to install.
    Task<bool> ConfirmInstallAsync(string version);
}

// Opens an address in the system's browser, or a folder in its file explorer. False when it could not.
public interface IUpdateLauncher
{
    Task<bool> OpenUriAsync(Uri uri);

    Task<bool> OpenFolderAsync(string folder);
}

// The address YAT reads its update information from: Directory.Build.props' YatUpdateManifestUrl, carried by the
// application assembly (Task #051.B). Null when the build did not give one.
public static class UpdateEndpoint
{
    public const string MetadataKey = "YatUpdateManifestUrl";

    public static Uri? ManifestUrl { get; } = From(typeof(UpdateEndpoint).Assembly);

    public static Uri? From(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var value = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(attribute => attribute.Key == MetadataKey)?.Value;
        return Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps ? url : null;
    }
}
