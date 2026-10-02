using System.Reflection;
using YAT.Application.Updates;
using YAT.app.ViewModels;

namespace YAT.app.Updates;

// Help > Check for Updates... (Task #051.B): one update window at a time, which checks as it opens and downloads only
// when the user asks. Closing the window stops whatever is still running. Nothing is installed.
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
            var update = new UpdateViewModel(_service, _dialogs.Launcher);
            _ = update.CheckAsync();
            await _dialogs.ShowAsync(update);

            // The window is closed: whatever still runs is of no use.
            update.Cancel();
            await update.Completion;
        }
        finally
        {
            IsRunning = false;
        }
    }
}

// The update window, and the system's browser and file explorer. The desktop implementation is Avalonia's; tests use a
// fake.
public interface IUpdateDialogs
{
    IUpdateLauncher Launcher { get; }

    // Shows the update window until the user closes it.
    Task ShowAsync(UpdateViewModel update);
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
