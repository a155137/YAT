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
    private readonly IAutomaticUpdateCheckStateStore? _stateStore;
    private readonly IReleaseNotesSource? _releaseNotes;
    private readonly TimeProvider _timeProvider;
    private CancellationTokenSource? _autoCheckCts;
    private bool _isAutoChecking;

    public UpdateCheckController(
        IUpdateDialogs dialogs,
        UpdateCheckService service,
        IAutomaticUpdateCheckStateStore? stateStore = null,
        IReleaseNotesSource? releaseNotes = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(service);
        _dialogs = dialogs;
        _service = service;
        _stateStore = stateStore;
        _releaseNotes = releaseNotes;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    // Whether an update window or notification is open (only one may be).
    public bool IsRunning { get; private set; }

    public async Task CheckForUpdatesAsync()
    {
        if (IsRunning)
        {
            return;
        }

        _autoCheckCts?.Cancel();
        IsRunning = true;
        try
        {
            var update = new UpdateViewModel(_service, _dialogs.Launcher, _dialogs.Prompts, _releaseNotes);
            _ = update.CheckAsync();
            await RunUpdateWindowAsync(update);
        }
        finally
        {
            IsRunning = false;
        }
    }

    // Runs once per local calendar day on startup in the background. Up-to-date results and network/timeout failures
    // are silent; when a newer version is available, displays the notification with version-matched Release Notes.
    public async Task CheckForUpdatesAutomaticallyAsync(CancellationToken cancellationToken = default)
    {
        if (_stateStore is null || IsRunning || _isAutoChecking || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        var today = DateOnly.FromDateTime(_timeProvider.GetLocalNow().Date);
        if (_stateStore.ReadLastCheckedDate() == today)
        {
            return;
        }

        _isAutoChecking = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _autoCheckCts = linked;
        try
        {
            UpdateCheckResult result;
            try
            {
                result = await Task.Run(() => _service.CheckAsync(linked.Token), linked.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                return;
            }

            if (linked.IsCancellationRequested)
            {
                return;
            }

            // Record today's check once the attempt finishes (not when cancelled on app shutdown).
            _stateStore.WriteLastCheckedDate(today);

            if (result.Status != UpdateCheckStatus.UpdateAvailable || result.Offer is not { } offer)
            {
                return;
            }

            string? notes = null;
            if (_releaseNotes is not null)
            {
                try
                {
                    notes = await Task.Run(() => _releaseNotes.FetchAsync(offer, linked.Token), linked.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    notes = null;
                }
            }

            if (linked.IsCancellationRequested || IsRunning)
            {
                return;
            }

            IsRunning = true;
            try
            {
                var currentVersion = _service.Installed?.ToString() ?? "unknown";
                var notification = new UpdateNotificationViewModel(offer, currentVersion, notes, _dialogs.Launcher);
                UpdateNotificationChoice choice;
                try
                {
                    choice = await _dialogs.ShowNotificationAsync(notification);
                }
                catch when (linked.IsCancellationRequested)
                {
                    return;
                }

                if (choice != UpdateNotificationChoice.UpdateNow || linked.IsCancellationRequested)
                {
                    return;
                }

                var update = new UpdateViewModel(_service, _dialogs.Launcher, _dialogs.Prompts, _releaseNotes);
                _ = update.StartDownloadFromOfferAsync(offer, notes);
                await RunUpdateWindowAsync(update);
            }
            finally
            {
                IsRunning = false;
            }
        }
        finally
        {
            if (ReferenceEquals(_autoCheckCts, linked))
            {
                _autoCheckCts = null;
            }

            _isAutoChecking = false;
        }
    }

    private async Task RunUpdateWindowAsync(UpdateViewModel update)
    {
        await _dialogs.ShowAsync(update);

        // The window is closed: whatever still runs is of no use.
        update.Cancel();
        await update.Completion;

        if (update.PendingInstall is { } handoff)
        {
            await InstallAsync(handoff, update.Package!.Version.ToString());
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

    // Shows the startup update notification until the user chooses Update Now, Later, or closes it.
    Task<UpdateNotificationChoice> ShowNotificationAsync(UpdateNotificationViewModel notification) =>
        Task.FromResult(UpdateNotificationChoice.Later);

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
