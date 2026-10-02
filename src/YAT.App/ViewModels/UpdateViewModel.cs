using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Updates;
using YAT.app.Updates;

namespace YAT.app.ViewModels;

// What the update window shows.
public enum UpdateWindowState
{
    Checking,
    UpToDate,
    RemoteVersionOlder,
    UpdateAvailable,
    Downloading,
    Verifying,
    Verified,

    // Preparing the installation (Task #051.C): the package verified again, the updater started.
    Installing,
    Failed
}

// The update window (Task #051.B): checking, then the result - up to date, a newer version offered, or why the check
// failed - and, only when the user clicks Download, downloading and verifying the package. A verified package can be
// installed (Task #051.C): once the user confirms, the updater is prepared and started - while YAT is still open, so
// any failure leaves YAT open - and the window closes with the updater ready (PendingInstall); the controller then
// closes YAT the usual way and the updater installs. Nothing here extracts or replaces anything.
//
// The work runs off the UI thread through the service; the state and the progress are put back here. Cancel stops a
// download (the offer stays) or a check (the window closes). Retry repeats what failed. One step at a time.
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateCheckService _service;
    private readonly IUpdateLauncher _launcher;
    private readonly IUpdatePrompts? _prompts;
    private CancellationTokenSource? _running;
    private UpdateOffer? _offer;
    private bool _downloadFailed;
    private bool _installFailed;

    public UpdateViewModel(UpdateCheckService service, IUpdateLauncher launcher, IUpdatePrompts? prompts = null)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(launcher);
        _service = service;
        _launcher = launcher;
        _prompts = prompts;
        CanInstall = prompts is not null && service.CanInstall;
        InstallCommand = new AsyncRelayCommand(InstallAsync, () => State == UpdateWindowState.Verified && CanInstall);
        InstalledVersion = service.Installed?.ToString() ?? "unknown";
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => State == UpdateWindowState.UpdateAvailable);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        RetryCommand = new AsyncRelayCommand(RetryAsync, () => State == UpdateWindowState.Failed);
        OpenReleaseNotesCommand = new AsyncRelayCommand(OpenReleaseNotesAsync, () => ReleaseNotesUrl is not null);
        OpenFolderCommand = new AsyncRelayCommand(OpenFolderAsync, () => Package is not null && State is UpdateWindowState.Verified or UpdateWindowState.Failed);
        State = UpdateWindowState.Checking;
        Headline = "Checking for updates...";
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusy))]
    public partial UpdateWindowState State { get; private set; }

    public string InstalledVersion { get; }

    [ObservableProperty]
    public partial string? AvailableVersion { get; private set; }

    // The package's size, as people read it ("60.6 MB").
    [ObservableProperty]
    public partial string? DownloadSize { get; private set; }

    [ObservableProperty]
    public partial string Headline { get; private set; }

    // A second line: why it failed, why a newer YAT is up to date, or what a verified package is for.
    [ObservableProperty]
    public partial string? Detail { get; private set; }

    [ObservableProperty]
    public partial double ProgressPercent { get; private set; }

    [ObservableProperty]
    public partial string? ProgressText { get; private set; }

    // Release notes of the offered version, when it has them (always https).
    [ObservableProperty]
    public partial Uri? ReleaseNotesUrl { get; private set; }

    // The verified package, once there is one.
    [ObservableProperty]
    public partial VerifiedUpdatePackage? Package { get; private set; }

    // Something is running that Cancel stops.
    public bool IsBusy => State is UpdateWindowState.Checking or UpdateWindowState.Downloading or UpdateWindowState.Verifying or UpdateWindowState.Installing;

    // Whether a verified package can be installed from this YAT (it runs from an unpacked release).
    public bool CanInstall { get; }

    // The updater, ready, once the user chose Install Update and the window closed for it (Task #051.C).
    public IUpdateHandoff? PendingInstall { get; private set; }

    // The step running now, or the last one (for tests and for the window, which waits for it when it closes).
    public Task Completion { get; private set; } = Task.CompletedTask;

    public IAsyncRelayCommand DownloadCommand { get; }

    public IRelayCommand CancelCommand { get; }

    public IAsyncRelayCommand RetryCommand { get; }

    public IAsyncRelayCommand OpenReleaseNotesCommand { get; }

    public IAsyncRelayCommand OpenFolderCommand { get; }

    public IAsyncRelayCommand InstallCommand { get; }

    // Raised when the window should close (a check was cancelled).
    public event EventHandler? CloseRequested;

    public Task CheckAsync() => Completion = Run(CheckCoreAsync, onCancelled: () => CloseRequested?.Invoke(this, EventArgs.Empty));

    public void Cancel() => _running?.Cancel();

    // User-facing words for a failure; no stack trace, at most the server's status.
    public static string Describe(UpdateFailure failure) => failure switch
    {
        UpdateFailure.ManifestNotFound => "Update information is not available yet. No release has been published.",
        UpdateFailure.NetworkUnavailable => "The update server could not be reached. Check the network connection.",
        UpdateFailure.Timeout => "The update server did not respond in time.",
        UpdateFailure.HttpError => "The update server returned an error.",
        UpdateFailure.ManifestMalformed => "The update information could not be read.",
        UpdateFailure.ManifestNewerSchema => "The update information needs a newer version of YAT. Get it from the YAT website.",
        UpdateFailure.ManifestInvalid => "The update information is not valid.",
        UpdateFailure.NoPackageForRuntime => "The new version has no package for this computer.",
        UpdateFailure.PackageNotFound => "The update package could not be found.",
        UpdateFailure.DownloadInterrupted => "The download was interrupted. Try again.",
        UpdateFailure.SizeMismatch => "The downloaded package is incomplete or damaged.",
        UpdateFailure.HashMismatch => "The downloaded package does not match the published release.",
        UpdateFailure.StorageFailed => "The update package could not be saved (disk space or permissions).",
        UpdateFailure.InstalledVersionUnknown => "This build of YAT has no release version to compare.",
        UpdateFailure.UnsupportedPlatform => "Updates are published for Windows x64 only.",
        _ => "The update could not be checked."
    };

    public static string Megabytes(long bytes) =>
        (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    partial void OnStateChanged(UpdateWindowState value)
    {
        DownloadCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnPackageChanged(VerifiedUpdatePackage? value) => OpenFolderCommand.NotifyCanExecuteChanged();

    partial void OnReleaseNotesUrlChanged(Uri? value) => OpenReleaseNotesCommand.NotifyCanExecuteChanged();

    private async Task CheckCoreAsync(CancellationToken token)
    {
        _downloadFailed = false;
        _offer = null;
        State = UpdateWindowState.Checking;
        Headline = "Checking for updates...";
        Detail = null;

        // Off the UI thread: the network and the disk are the service's.
        var result = await Task.Run(() => _service.CheckAsync(token), token);
        AvailableVersion = result.Available?.ToString();
        switch (result.Status)
        {
            case UpdateCheckStatus.UpToDate:
                Headline = $"YAT {InstalledVersion} is up to date.";
                Detail = "No newer version is available.";
                State = UpdateWindowState.UpToDate;
                break;

            case UpdateCheckStatus.RemoteVersionOlder:
                Headline = $"YAT {InstalledVersion} is up to date.";
                Detail = $"This version is newer than the latest published release ({result.Available}).";
                State = UpdateWindowState.RemoteVersionOlder;
                break;

            case UpdateCheckStatus.UpdateAvailable:
                _offer = result.Offer!;
                DownloadSize = Megabytes(_offer.Package.Size);
                ReleaseNotesUrl = _offer.ReleaseNotesUrl;
                Headline = $"YAT {result.Available} is available.";
                Detail = null;
                State = UpdateWindowState.UpdateAvailable;
                break;

            default:
                Fail(result.Failure!.Value);
                break;
        }
    }

    private Task DownloadAsync()
    {
        if (_offer is not { } offer || State != UpdateWindowState.UpdateAvailable)
        {
            return Task.CompletedTask;
        }

        return Completion = Run(token => DownloadCoreAsync(offer, token), onCancelled: () =>
        {
            Headline = $"YAT {offer.Version} is available.";
            Detail = "The download was cancelled.";
            ProgressPercent = 0;
            ProgressText = null;
            State = UpdateWindowState.UpdateAvailable;
        });
    }

    private async Task DownloadCoreAsync(UpdateOffer offer, CancellationToken token)
    {
        _downloadFailed = false;
        State = UpdateWindowState.Downloading;
        Headline = $"Downloading YAT {offer.Version}...";
        Detail = null;
        ProgressPercent = 0;
        ProgressText = $"0.0 MB of {Megabytes(offer.Package.Size)}";

        var progress = new UiProgress(step =>
        {
            if (State is not (UpdateWindowState.Downloading or UpdateWindowState.Verifying))
            {
                return;
            }

            ProgressPercent = step.TotalBytes == 0 ? 0 : step.BytesReceived * 100d / step.TotalBytes;
            ProgressText = $"{Megabytes(step.BytesReceived)} of {Megabytes(step.TotalBytes)}";
            if (step.Verifying)
            {
                State = UpdateWindowState.Verifying;
                Headline = $"Verifying YAT {offer.Version}...";
            }
        });

        var result = await Task.Run(() => _service.DownloadAsync(offer, progress, token), token);
        if (result.Package is { } package)
        {
            Package = package;
            ProgressPercent = 100;
            Headline = $"YAT {package.Version} has been downloaded and verified.";
            Detail = CanInstall
                ? "Install Update closes YAT, installs this version and starts YAT again."
                : "This YAT does not run from an unpacked release package, so it cannot install updates itself.";
            State = UpdateWindowState.Verified;
            return;
        }

        _downloadFailed = true;
        Fail(result.Failure!.Value);
    }

    private Task RetryAsync()
    {
        if (State != UpdateWindowState.Failed)
        {
            return Task.CompletedTask;
        }

        if (_installFailed && Package is not null)
        {
            State = UpdateWindowState.Verified;
            return InstallAsync();
        }

        if (_downloadFailed && _offer is not null)
        {
            State = UpdateWindowState.UpdateAvailable;
            return DownloadAsync();
        }

        return CheckAsync();
    }

    // Install Update (Task #051.C): confirmed, then the updater prepared and started off the UI thread. Ready: the window
    // closes and the controller closes YAT. Not ready: why, and YAT stays open. Cancel (or closing the window) while it
    // prepares stops it; an updater already started is told to change nothing.
    private async Task InstallAsync()
    {
        if (Package is not { } package || State != UpdateWindowState.Verified || !CanInstall
            || !await _prompts!.ConfirmInstallAsync(package.Version.ToString()))
        {
            return;
        }

        await (Completion = Run(token => InstallCoreAsync(package, token), onCancelled: () =>
        {
            Headline = $"YAT {package.Version} has been downloaded and verified.";
            Detail = "The installation was cancelled.";
            State = UpdateWindowState.Verified;
        }));
    }

    private async Task InstallCoreAsync(VerifiedUpdatePackage package, CancellationToken token)
    {
        _installFailed = false;
        State = UpdateWindowState.Installing;
        Headline = $"Preparing to install YAT {package.Version}...";
        Detail = null;

        var result = await Task.Run(() => _service.PrepareInstallAsync(package, token), token);
        if (result.Handoff is { } handoff)
        {
            if (token.IsCancellationRequested)
            {
                handoff.Cancel();
                handoff.Dispose();
                token.ThrowIfCancellationRequested();
            }

            PendingInstall = handoff;
            Headline = $"Installing YAT {package.Version}...";
            Detail = "YAT will close, install the update and start again.";
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        _installFailed = true;
        Headline = $"YAT {package.Version} could not be installed.";
        Detail = DescribeInstall(result.Failure!.Value);
        ProgressText = null;
        State = UpdateWindowState.Failed;
    }

    // User-facing words for why an update cannot be installed now; YAT stays open in every case.
    public static string DescribeInstall(UpdateInstallFailure failure) => failure switch
    {
        UpdateInstallFailure.NotAReleaseInstallation => "This YAT does not run from an unpacked release package, so it cannot install updates itself.",
        UpdateInstallFailure.OtherYatRunning => "Another YAT window is running from the same folder. Close it, then try again.",
        UpdateInstallFailure.InstallationNotWritable =>
            "Automatic installation cannot modify YAT's folder (for example a folder under Program Files, or a read-only location). Use Open Folder to find the downloaded package.",
        UpdateInstallFailure.InsufficientSpace => "There is not enough free disk space in YAT's folder to install the update.",
        UpdateInstallFailure.PackageInvalid => "The downloaded package is no longer the verified release. Check for updates again.",
        UpdateInstallFailure.UpdateInProgress => "An update of this YAT is already being installed.",
        UpdateInstallFailure.UpdaterNotReady => "The updater could not be started. Security software may have blocked it.",
        _ => "The update could not be installed."
    };

    private void Fail(UpdateFailure failure)
    {
        Headline = "The update could not be completed.";
        Detail = Describe(failure);
        ProgressText = null;
        State = UpdateWindowState.Failed;
    }

    private async Task OpenReleaseNotesAsync()
    {
        if (ReleaseNotesUrl is { Scheme: "https" } url)
        {
            await _launcher.OpenUriAsync(url);
        }
    }

    private async Task OpenFolderAsync()
    {
        if (Package is { } package && Path.GetDirectoryName(package.LocalPackagePath) is { } folder)
        {
            await _launcher.OpenFolderAsync(folder);
        }
    }

    // Progress put back on the thread the window lives on (when there is one), in order; a report that comes after the
    // step ended is ignored by the step's own check of the state.
    private sealed class UiProgress(Action<UpdateDownloadProgress> report) : IProgress<UpdateDownloadProgress>
    {
        private readonly SynchronizationContext? _context = SynchronizationContext.Current;

        public void Report(UpdateDownloadProgress value)
        {
            if (_context is null)
            {
                report(value);
            }
            else
            {
                _context.Post(_ => report(value), null);
            }
        }
    }

    // One step at a time: a step cancelled by the user runs onCancelled; nothing else escapes.
    private async Task Run(Func<CancellationToken, Task> step, Action onCancelled)
    {
        var cancellation = new CancellationTokenSource();
        _running = cancellation;
        try
        {
            await step(cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            onCancelled();
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.TraceError($"The update step failed: {exception}");
            Headline = "The update could not be completed.";
            Detail = "An unexpected error occurred.";
            ProgressText = null;
            State = UpdateWindowState.Failed;
        }
        finally
        {
            if (ReferenceEquals(_running, cancellation))
            {
                _running = null;
            }

            cancellation.Dispose();
        }
    }
}
