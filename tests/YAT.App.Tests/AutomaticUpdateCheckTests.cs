using System.Runtime.InteropServices;
using YAT.Application.Updates;
using YAT.app.Updates;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

public sealed class AutomaticUpdateCheckTests
{
    private const string Sha = "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a";
    private static readonly UpdateEnvironment Windows = new("0.4.0", true, Architecture.X64);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Manifest(string version = "0.5.0", string releaseNotesUrl = "https://github.com/a155137/YAT/releases/tag/v0.5.0") => $$"""
        { "schemaVersion": 1, "version": "{{version}}", "releaseNotesUrl": "{{releaseNotesUrl}}",
          "packages": [ { "rid": "win-x64", "url": "https://github.com/a155137/YAT/releases/download/v{{version}}/YAT-v{{version}}-win-x64.zip", "sha256": "{{Sha}}", "size": 1024 } ] }
        """;

    [Fact]
    public async Task AutoCheckOncePerDayThenRechecksNextDayWhileManualRemainsUnrestricted()
    {
        var source = new FakeManifestSource(() => Task.FromResult(Manifest("0.4.0")));
        var state = new FakeStateStore();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 9, 0, 0, TimeSpan.FromHours(8)));
        var dialogs = new FakeDialogs();
        var service = new UpdateCheckService(source, new FakeDownloader(), Windows);
        var controller = new UpdateCheckController(dialogs, service, state, new FakeReleaseNotesSource("notes"), clock);

        // First startup today -> checks once, silent because up to date
        await controller.CheckForUpdatesAutomaticallyAsync(Token);
        Assert.Equal(1, source.Fetches);
        Assert.Empty(dialogs.Notifications);

        // Second startup same day -> skipped
        await controller.CheckForUpdatesAutomaticallyAsync(Token);
        Assert.Equal(1, source.Fetches);

        // Manual check same day -> NOT restricted by daily auto-check state
        await controller.CheckForUpdatesAsync();
        Assert.Equal(2, source.Fetches);
        Assert.Equal(1, dialogs.ManualShows);

        // Manual check when update is available -> fetches Release Notes onto UpdateViewModel
        var updateSource = new FakeManifestSource(() => Task.FromResult(Manifest("0.5.0")));
        var updateController = new UpdateCheckController(
            dialogs,
            new UpdateCheckService(updateSource, new FakeDownloader(), Windows),
            state,
            new FakeReleaseNotesSource("Manual Release Notes Body"),
            clock);
        await updateController.CheckForUpdatesAsync();
        Assert.Equal("Manual Release Notes Body", dialogs.LastManualUpdate?.ReleaseNotes);

        // Next day -> auto-checks again
        clock.Advance(TimeSpan.FromDays(1));
        await controller.CheckForUpdatesAutomaticallyAsync(Token);
        Assert.Equal(3, source.Fetches);
    }

    [Fact]
    public async Task NetworkFailureAndTimeoutAreSilent()
    {
        var failureSource = new FakeManifestSource(() => throw new UpdateTransportException(UpdateFailure.Timeout, "timed out"));
        var state = new FakeStateStore();
        var dialogs = new FakeDialogs();
        var service = new UpdateCheckService(failureSource, new FakeDownloader(), Windows);
        var controller = new UpdateCheckController(dialogs, service, state, new FakeReleaseNotesSource("notes"));

        await controller.CheckForUpdatesAutomaticallyAsync(Token);

        Assert.Empty(dialogs.Notifications);
        Assert.Empty(dialogs.Messages);
        Assert.Equal(0, dialogs.ManualShows);
    }

    [Fact]
    public async Task UpdateAvailableShowsReleaseNotesAndSupportsLaterViewOnGitHubAndUpdateNow()
    {
        var source = new FakeManifestSource(() => Task.FromResult(Manifest("0.5.0")));
        var downloader = new FakeDownloader();
        var state = new FakeStateStore();
        var dialogs = new FakeDialogs { NotificationChoice = UpdateNotificationChoice.Later };
        var service = new UpdateCheckService(source, downloader, Windows);
        var controller = new UpdateCheckController(dialogs, service, state, new FakeReleaseNotesSource("## Added\n- Feature X"));

        // Later -> shows notification, does not download, same day won't re-notify
        await controller.CheckForUpdatesAutomaticallyAsync(Token);
        var notification = Assert.Single(dialogs.Notifications);
        Assert.Equal("0.4.0", notification.CurrentVersion);
        Assert.Equal("0.5.0", notification.NewVersion);
        Assert.Equal("## Added\n- Feature X", notification.ReleaseNotes);

        // View on GitHub opens exact ReleaseNotesUrl
        await notification.ViewOnGitHubCommand.ExecuteAsync(null);
        Assert.Equal(new Uri("https://github.com/a155137/YAT/releases/tag/v0.5.0"), Assert.Single(dialogs.FakeLauncher.OpenedUris));
        Assert.Empty(downloader.Downloads);

        // Second check same day -> skipped
        await controller.CheckForUpdatesAutomaticallyAsync(Token);
        Assert.Single(dialogs.Notifications);

        // Reset state for next day + choose Update Now -> opens update window and downloads
        state.LastDate = null;
        dialogs.NotificationChoice = UpdateNotificationChoice.UpdateNow;
        await controller.CheckForUpdatesAutomaticallyAsync(Token);
        Assert.Equal(2, dialogs.Notifications.Count);
        Assert.Equal(1, dialogs.ManualShows);
        Assert.Single(downloader.Downloads);
    }

    [Fact]
    public async Task MissingReleaseNotesStillShowsNotificationAndAllowsUpdate()
    {
        var source = new FakeManifestSource(() => Task.FromResult(Manifest("0.5.0")));
        var downloader = new FakeDownloader();
        var state = new FakeStateStore();
        var dialogs = new FakeDialogs { NotificationChoice = UpdateNotificationChoice.UpdateNow };
        var service = new UpdateCheckService(source, downloader, Windows);
        var controller = new UpdateCheckController(dialogs, service, state, new FakeReleaseNotesSource(null));

        await controller.CheckForUpdatesAutomaticallyAsync(Token);

        var notification = Assert.Single(dialogs.Notifications);
        Assert.Equal(UpdateNotificationViewModel.UnavailableReleaseNotesText, notification.ReleaseNotes);
        Assert.Equal(1, dialogs.ManualShows);
        Assert.Single(downloader.Downloads);
    }

    [Fact]
    public async Task CancellationAndDuplicateGuardPreventDuplicateOrPostCloseNotifications()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new FakeManifestSource(() =>
        {
            entered.TrySetResult();
            return gate.Task;
        });
        var state = new FakeStateStore();
        var dialogs = new FakeDialogs();
        var service = new UpdateCheckService(source, new FakeDownloader(), Windows);
        var controller = new UpdateCheckController(dialogs, service, state, new FakeReleaseNotesSource("notes"));

        using var cts = new CancellationTokenSource();
        var running = controller.CheckForUpdatesAutomaticallyAsync(cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);

        // Concurrent auto-check while in-flight -> ignored
        await controller.CheckForUpdatesAutomaticallyAsync(Token);
        Assert.Equal(1, source.Fetches);

        // Cancel on window close -> completes silently without showing notification or recording date
        cts.Cancel();
        gate.TrySetCanceled(cts.Token);
        await running;

        Assert.Empty(dialogs.Notifications);
        Assert.Null(state.LastDate);
    }

    private sealed class FakeManifestSource(Func<Task<string>> respond) : IUpdateManifestSource
    {
        public int Fetches { get; private set; }
        public Task<string> FetchAsync(CancellationToken cancellationToken)
        {
            Fetches++;
            return respond();
        }
    }

    private sealed class FakeDownloader : IUpdatePackageDownloader
    {
        public List<string> Downloads { get; } = [];

        public Task<VerifiedUpdatePackage?> FindVerifiedAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken) =>
            Task.FromResult<VerifiedUpdatePackage?>(null);

        public Task<VerifiedUpdatePackage> DownloadAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Downloads.Add(offer.Version.ToString());
            return Task.FromResult(new VerifiedUpdatePackage(offer.Version, rid, "fake.zip", offer.Package.Size, Sha, offer.ReleaseNotesUrl));
        }
    }

    private sealed class FakeStateStore : IAutomaticUpdateCheckStateStore
    {
        public DateOnly? LastDate { get; set; }
        public DateOnly? ReadLastCheckedDate() => LastDate;
        public void WriteLastCheckedDate(DateOnly date) => LastDate = date;
    }

    private sealed class FakeReleaseNotesSource(string? notes) : IReleaseNotesSource
    {
        public Task<string?> FetchAsync(UpdateOffer offer, CancellationToken cancellationToken) =>
            Task.FromResult(notes);
    }

    private sealed class FakeTimeProvider(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test", _now.Offset, "Test", "Test");
    }

    private sealed class FakeDialogs : IUpdateDialogs
    {
        public FakeLauncherImpl FakeLauncher { get; } = new();
        public IUpdateLauncher Launcher => FakeLauncher;
        public IUpdatePrompts Prompts => new FakePrompts();
        public UpdateNotificationChoice NotificationChoice { get; set; } = UpdateNotificationChoice.Later;
        public List<UpdateNotificationViewModel> Notifications { get; } = [];
        public int ManualShows { get; private set; }
        public UpdateViewModel? LastManualUpdate { get; private set; }
        public List<string> Messages { get; } = [];

        public async Task ShowAsync(UpdateViewModel update)
        {
            ManualShows++;
            LastManualUpdate = update;
            await update.Completion;
        }

        public Task<UpdateNotificationChoice> ShowNotificationAsync(UpdateNotificationViewModel notification)
        {
            Notifications.Add(notification);
            return Task.FromResult(NotificationChoice);
        }

        public Task<bool> CloseApplicationForInstallAsync() => Task.FromResult(false);
        public void ExitApplication() { }
        public Task ShowMessageAsync(string message) { Messages.Add(message); return Task.CompletedTask; }

        public sealed class FakeLauncherImpl : IUpdateLauncher
        {
            public List<Uri> OpenedUris { get; } = [];
            public Task<bool> OpenUriAsync(Uri uri) { OpenedUris.Add(uri); return Task.FromResult(true); }
            public Task<bool> OpenFolderAsync(string folder) => Task.FromResult(true);
        }

        private sealed class FakePrompts : IUpdatePrompts
        {
            public Task<bool> ConfirmInstallAsync(string version) => Task.FromResult(false);
        }
    }
}
