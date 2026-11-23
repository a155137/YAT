using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Updates;
using YAT.app.Updates;

namespace YAT.app.ViewModels;

public enum UpdateNotificationChoice
{
    Later,
    UpdateNow
}

// ViewModel for the startup automatic update notification window: displays the current and new versions along with
// plain-text GitHub Release Notes, and lets the user choose Update Now, Later, or View on GitHub.
public sealed partial class UpdateNotificationViewModel : ObservableObject
{
    public const string UnavailableReleaseNotesText = "Release notes are not available for this version.";

    private readonly IUpdateLauncher _launcher;

    public UpdateNotificationViewModel(
        UpdateOffer offer,
        string currentVersion,
        string? releaseNotes,
        IUpdateLauncher launcher)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);
        ArgumentNullException.ThrowIfNull(launcher);

        Offer = offer;
        CurrentVersion = currentVersion;
        NewVersion = offer.Version.ToString();
        ReleaseNotes = string.IsNullOrWhiteSpace(releaseNotes) ? UnavailableReleaseNotesText : releaseNotes;
        ReleaseNotesUrl = offer.ReleaseNotesUrl;
        _launcher = launcher;

        UpdateNowCommand = new RelayCommand(SelectUpdateNow);
        LaterCommand = new RelayCommand(SelectLater);
        ViewOnGitHubCommand = new AsyncRelayCommand(ViewOnGitHubAsync, () => ReleaseNotesUrl is not null);
    }

    public UpdateOffer Offer { get; }

    public string CurrentVersion { get; }

    public string NewVersion { get; }

    public string ReleaseNotes { get; }

    public Uri? ReleaseNotesUrl { get; }

    public bool HasReleaseNotesUrl => ReleaseNotesUrl is not null;

    public UpdateNotificationChoice Choice { get; private set; } = UpdateNotificationChoice.Later;

    public IRelayCommand UpdateNowCommand { get; }

    public IRelayCommand LaterCommand { get; }

    public IAsyncRelayCommand ViewOnGitHubCommand { get; }

    public event EventHandler<UpdateNotificationChoice>? CloseRequested;

    private void SelectUpdateNow()
    {
        Choice = UpdateNotificationChoice.UpdateNow;
        CloseRequested?.Invoke(this, UpdateNotificationChoice.UpdateNow);
    }

    private void SelectLater()
    {
        Choice = UpdateNotificationChoice.Later;
        CloseRequested?.Invoke(this, UpdateNotificationChoice.Later);
    }

    private Task ViewOnGitHubAsync() =>
        ReleaseNotesUrl is { } url ? _launcher.OpenUriAsync(url) : Task.CompletedTask;
}
