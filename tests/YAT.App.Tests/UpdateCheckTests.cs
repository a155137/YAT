using System.Net;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using YAT.app;
using YAT.app.Composition;
using YAT.app.Updates;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// Help > Check for Updates... (Task #051.B) without a window or the network: the update window's states, a download only
// when the user clicks Download, its progress and Cancel, Retry, a verified package with nothing to install, Release Notes
// and Open Folder through the launcher, one update window at a time - and the whole way through the composition root over
// a fake HTTP handler, where an address that is not there is "not available", never "up to date".
public class UpdateCheckTests
{
    private const string Sha = "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a";

    private static readonly UpdateEnvironment Windows = new("0.2.0", true, Architecture.X64);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Manifest(string version = "0.3.0", string extra = """, "releaseNotesUrl": "https://example.test/notes" """, long size = 63_543_296) => $$"""
        { "schemaVersion": 1, "version": "{{version}}"{{extra}},
          "packages": [ { "rid": "win-x64", "url": "https://example.test/YAT-v{{version}}-win-x64.zip", "sha256": "{{Sha}}", "size": {{size}} } ] }
        """;

    // ---- Fakes ----

    private sealed class Source(params Func<CancellationToken, Task<string>>[] answers) : IUpdateManifestSource
    {
        public int Fetches { get; private set; }

        public Task<string> FetchAsync(CancellationToken cancellationToken)
        {
            var answer = answers[Math.Min(Fetches, answers.Length - 1)];
            Fetches++;
            return answer(cancellationToken);
        }

        public static Source Of(string json) => new(_ => Task.FromResult(json));
    }

    // A downloader that reports half the package, then waits for the test to let it finish, fail or be cancelled.
    private sealed class Downloader : IUpdatePackageDownloader
    {
        private TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];

        public Queue<UpdateFailure> Failures { get; } = [];

        public bool Gated { get; set; }

        public TaskCompletionSource Halfway { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release()
        {
            _gate.TrySetResult();
        }

        public Task<VerifiedUpdatePackage?> FindVerifiedAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("find");
            return Task.FromResult<VerifiedUpdatePackage?>(null);
        }

        public async Task<VerifiedUpdatePackage> DownloadAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("download");
            progress?.Report(new UpdateDownloadProgress(offer.Package.Size / 2, offer.Package.Size));
            Halfway.TrySetResult();
            if (Gated)
            {
                await _gate.Task.WaitAsync(cancellationToken);
                _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                Halfway = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (Failures.TryDequeue(out var failure))
            {
                throw new UpdateTransportException(failure);
            }

            progress?.Report(new UpdateDownloadProgress(offer.Package.Size, offer.Package.Size, Verifying: true));
            return new VerifiedUpdatePackage(offer.Version, rid, Path.Combine(@"C:\updates", offer.Version.Tag, offer.Version.PackageName(rid) + ".zip"), offer.Package.Size, offer.Package.Sha256, offer.ReleaseNotesUrl);
        }
    }

    private sealed class Launcher : IUpdateLauncher
    {
        public List<Uri> Uris { get; } = [];

        public List<string> Folders { get; } = [];

        public Task<bool> OpenUriAsync(Uri uri)
        {
            Uris.Add(uri);
            return Task.FromResult(true);
        }

        public Task<bool> OpenFolderAsync(string folder)
        {
            Folders.Add(folder);
            return Task.FromResult(true);
        }
    }

    // The update window: the test closes it.
    private sealed class Dialogs : IUpdateDialogs
    {
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<UpdateViewModel> Shown { get; } = [];

        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Launcher Launcher { get; } = new();

        IUpdateLauncher IUpdateDialogs.Launcher => Launcher;

        public UpdateViewModel Last => Shown[^1];

        public Prompts Prompts { get; } = new();

        IUpdatePrompts IUpdateDialogs.Prompts => Prompts;

        // What YAT's close decision answers (the save prompt): true lets YAT close.
        public bool AllowClose { get; set; } = true;

        public List<string> Steps { get; } = [];

        public Task ShowAsync(UpdateViewModel update)
        {
            Shown.Add(update);
            update.CloseRequested += (_, _) => _closed.TrySetResult();
            Opened.TrySetResult();
            return _closed.Task;
        }

        public Task<bool> CloseApplicationForInstallAsync()
        {
            Steps.Add(AllowClose ? "close approved" : "close cancelled");
            return Task.FromResult(AllowClose);
        }

        public void ExitApplication() => Steps.Add("exit");

        public Task ShowMessageAsync(string message)
        {
            Steps.Add($"message: {message}");
            return Task.CompletedTask;
        }

        public void Close() => _closed.TrySetResult();
    }

    private sealed class Prompts : IUpdatePrompts
    {
        public bool Answer { get; set; } = true;

        public List<string> Asked { get; } = [];

        public Task<bool> ConfirmInstallAsync(string version)
        {
            Asked.Add(version);
            return Task.FromResult(Answer);
        }
    }

    // The updater, ready; records what YAT tells it.
    private sealed class Handoff(List<string> said, bool updaterAlive = true) : IUpdateHandoff
    {
        public bool Go()
        {
            said.Add("go");
            return updaterAlive;
        }

        public void Cancel() => said.Add("cancel");

        public void Dispose() => said.Add("disposed");
    }

    private sealed class Installer : IUpdateInstaller
    {
        public bool IsAvailable { get; set; } = true;

        public Queue<UpdateInstallFailure> Failures { get; } = [];

        public List<string> Said { get; } = [];

        public bool UpdaterAlive { get; set; } = true;

        public TaskCompletionSource? Gate { get; set; }

        public int Prepared { get; private set; }

        public async Task<UpdateInstallPreparation> PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
        {
            Prepared++;
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            return Failures.TryDequeue(out var failure)
                ? UpdateInstallPreparation.Failed(failure, "why")
                : new UpdateInstallPreparation(new Handoff(Said, UpdaterAlive), null, null);
        }
    }

    private static (UpdateViewModel Update, Source Source, Downloader Downloader, Launcher Launcher) Window(Source source, UpdateEnvironment? environment = null)
    {
        var downloader = new Downloader();
        var launcher = new Launcher();
        return (new UpdateViewModel(new UpdateCheckService(source, downloader, environment ?? Windows), launcher), source, downloader, launcher);
    }

    private static async Task<(UpdateViewModel Update, Source Source, Downloader Downloader, Launcher Launcher)> Checked(Source source, UpdateEnvironment? environment = null)
    {
        var window = Window(source, environment);
        await window.Update.CheckAsync();
        return window;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The update window did not get there in time.");
            await Task.Delay(10, Token);
        }
    }

    // ---- Checking ----

    [Fact]
    public void TheWindowOpensCheckingWithTheInstalledVersion()
    {
        var (update, _, _, _) = Window(Source.Of(Manifest()));

        Assert.Equal(UpdateWindowState.Checking, update.State);
        Assert.Equal("Checking for updates...", update.Headline);
        Assert.Equal("0.2.0", update.InstalledVersion);
        Assert.True(update.IsBusy);
        Assert.True(update.CancelCommand.CanExecute(null));
        Assert.False(update.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public async Task ANewerVersionIsOfferedWithItsSizeAndNotesAndNothingIsDownloaded()
    {
        var (update, _, downloader, _) = await Checked(Source.Of(Manifest()));

        Assert.Equal(UpdateWindowState.UpdateAvailable, update.State);
        Assert.Equal("YAT 0.3.0 is available.", update.Headline);
        Assert.Equal("0.3.0", update.AvailableVersion);
        Assert.Equal("60.6 MB", update.DownloadSize);
        Assert.Equal(new Uri("https://example.test/notes"), update.ReleaseNotesUrl);
        Assert.True(update.DownloadCommand.CanExecute(null));
        Assert.False(update.IsBusy);
        Assert.Empty(downloader.Calls);
    }

    [Fact]
    public async Task TheSameVersionIsUpToDate()
    {
        var (update, _, downloader, _) = await Checked(Source.Of(Manifest("0.2.0")));

        Assert.Equal(UpdateWindowState.UpToDate, update.State);
        Assert.Equal("YAT 0.2.0 is up to date.", update.Headline);
        Assert.False(update.DownloadCommand.CanExecute(null));
        Assert.Empty(downloader.Calls);
    }

    [Fact]
    public async Task ANewerInstalledVersionIsUpToDateAndSaysWhy()
    {
        var (update, _, _, _) = await Checked(Source.Of(Manifest("0.1.0")));

        Assert.Equal(UpdateWindowState.RemoteVersionOlder, update.State);
        Assert.Equal("YAT 0.2.0 is up to date.", update.Headline);
        Assert.Equal("This version is newer than the latest published release (0.1.0).", update.Detail);
        Assert.False(update.DownloadCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(UpdateFailure.ManifestNotFound, "Update information is not available yet. No release has been published.")]
    [InlineData(UpdateFailure.NetworkUnavailable, "The update server could not be reached. Check the network connection.")]
    [InlineData(UpdateFailure.Timeout, "The update server did not respond in time.")]
    [InlineData(UpdateFailure.HttpError, "The update server returned an error.")]
    public async Task AFailedCheckSaysWhyAndOffersRetry(UpdateFailure failure, string detail)
    {
        var (update, _, _, _) = await Checked(new Source(_ => throw new UpdateTransportException(failure, "HTTP 404")));

        Assert.Equal(UpdateWindowState.Failed, update.State);
        Assert.Equal("The update could not be completed.", update.Headline);
        Assert.Equal(detail, update.Detail);
        Assert.True(update.RetryCommand.CanExecute(null));
        Assert.False(update.DownloadCommand.CanExecute(null));
    }

    [Fact]
    public void EveryFailureHasItsOwnWords()
    {
        var words = Enum.GetValues<UpdateFailure>().Select(UpdateViewModel.Describe).ToList();

        Assert.Equal(15, words.Count);
        Assert.Equal(words.Count, words.Distinct().Count());
        Assert.DoesNotContain(words, text => text.Contains("up to date", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AnotherPlatformIsSaidAsSuch()
    {
        var (update, source, _, _) = await Checked(Source.Of(Manifest()), Windows with { ProcessArchitecture = Architecture.Arm64 });

        Assert.Equal("Updates are published for Windows x64 only.", update.Detail);
        Assert.Equal(0, source.Fetches);
    }

    [Fact]
    public async Task RetryChecksAgain()
    {
        var (update, source, _, _) = await Checked(new Source(_ => throw new UpdateTransportException(UpdateFailure.NetworkUnavailable), _ => Task.FromResult(Manifest())));

        await update.RetryCommand.ExecuteAsync(null);

        Assert.Equal(2, source.Fetches);
        Assert.Equal(UpdateWindowState.UpdateAvailable, update.State);
    }

    [Fact]
    public async Task CancellingTheCheckClosesTheWindow()
    {
        var (update, _, _, _) = Window(new Source(async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return string.Empty;
        }));
        var closed = 0;
        update.CloseRequested += (_, _) => closed++;

        var checking = update.CheckAsync();
        update.CancelCommand.Execute(null);
        await checking.WaitAsync(Patience, Token);

        Assert.Equal(1, closed);
        Assert.NotEqual(UpdateWindowState.Failed, update.State);
    }

    // ---- Downloading ----

    [Fact]
    public async Task DownloadingShowsProgressThenAVerifiedPackage()
    {
        var (update, _, downloader, _) = await Checked(Source.Of(Manifest()));
        downloader.Gated = true;

        var downloading = update.DownloadCommand.ExecuteAsync(null);
        await downloader.Halfway.Task.WaitAsync(Patience, Token);
        await Until(() => update.ProgressPercent > 0);

        Assert.Equal(UpdateWindowState.Downloading, update.State);
        Assert.Equal("Downloading YAT 0.3.0...", update.Headline);
        Assert.Equal(50, update.ProgressPercent, 1);
        Assert.Equal("30.3 MB of 60.6 MB", update.ProgressText);
        Assert.True(update.IsBusy);
        Assert.True(update.CancelCommand.CanExecute(null));
        Assert.False(update.DownloadCommand.CanExecute(null));

        downloader.Release();
        await downloading.WaitAsync(Patience, Token);

        Assert.Equal(UpdateWindowState.Verified, update.State);
        Assert.Equal("YAT 0.3.0 has been downloaded and verified.", update.Headline);
        Assert.Equal("This YAT does not run from an unpacked release package, so it cannot install updates itself.", update.Detail);
        Assert.Equal(100, update.ProgressPercent);
        Assert.Equal(["find", "download"], downloader.Calls);
        Assert.True(update.OpenFolderCommand.CanExecute(null));
        Assert.False(update.IsBusy);

        // Without an installer (this YAT is not an unpacked release) nothing installs, and nothing tells the user to unpack or
        // copy the package over YAT.
        Assert.False(update.CanInstall);
        Assert.False(update.InstallCommand.CanExecute(null));
        Assert.Equal(
            ["CancelCommand", "DownloadCommand", "InstallCommand", "OpenFolderCommand", "OpenReleaseNotesCommand", "RetryCommand"],
            typeof(UpdateViewModel).GetProperties().Select(property => property.Name).Where(name => name.EndsWith("Command", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        foreach (var text in new[] { update.Headline, update.Detail! })
        {
            foreach (var word in new[] { "extract", "unzip", "overwrite", "replace", "copy" })
            {
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task CancellingADownloadKeepsTheOffer()
    {
        var (update, _, downloader, _) = await Checked(Source.Of(Manifest()));
        downloader.Gated = true;

        var downloading = update.DownloadCommand.ExecuteAsync(null);
        await downloader.Halfway.Task.WaitAsync(Patience, Token);
        update.CancelCommand.Execute(null);
        await downloading.WaitAsync(Patience, Token);

        Assert.Equal(UpdateWindowState.UpdateAvailable, update.State);
        Assert.Equal("The download was cancelled.", update.Detail);
        Assert.Equal(0, update.ProgressPercent);
        Assert.Null(update.Package);
        Assert.True(update.DownloadCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(UpdateFailure.HashMismatch, "The downloaded package does not match the published release.")]
    [InlineData(UpdateFailure.SizeMismatch, "The downloaded package is incomplete or damaged.")]
    [InlineData(UpdateFailure.StorageFailed, "The update package could not be saved (disk space or permissions).")]
    public async Task AFailedDownloadIsRetriedAsADownload(UpdateFailure failure, string detail)
    {
        var (update, source, downloader, _) = await Checked(Source.Of(Manifest()));
        downloader.Failures.Enqueue(failure);

        await update.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(UpdateWindowState.Failed, update.State);
        Assert.Equal(detail, update.Detail);
        Assert.Null(update.Package);

        await update.RetryCommand.ExecuteAsync(null);

        Assert.Equal(UpdateWindowState.Verified, update.State);
        Assert.Equal(["find", "download", "find", "download"], downloader.Calls);
        Assert.Equal(1, source.Fetches);
    }

    [Fact]
    public async Task AnUnexpectedErrorIsAFailureNotACrash()
    {
        var (update, _, _, _) = await Checked(new Source(_ => throw new InvalidOperationException("boom")));

        Assert.Equal(UpdateWindowState.Failed, update.State);
        Assert.Equal("An unexpected error occurred.", update.Detail);
        Assert.DoesNotContain("boom", update.Detail, StringComparison.Ordinal);
    }

    // ---- The launcher ----

    [Fact]
    public async Task ReleaseNotesOpenInTheBrowser()
    {
        var (update, _, _, launcher) = await Checked(Source.Of(Manifest()));

        await update.OpenReleaseNotesCommand.ExecuteAsync(null);

        Assert.Equal([new Uri("https://example.test/notes")], launcher.Uris);
    }

    [Fact]
    public async Task WithoutReleaseNotesThereIsNothingToOpen()
    {
        var (update, _, _, launcher) = await Checked(Source.Of(Manifest(extra: string.Empty)));

        Assert.Null(update.ReleaseNotesUrl);
        Assert.False(update.OpenReleaseNotesCommand.CanExecute(null));
        Assert.Empty(launcher.Uris);
    }

    [Fact]
    public async Task OpenFolderShowsTheVerifiedPackagesFolder()
    {
        var (update, _, _, launcher) = await Checked(Source.Of(Manifest()));
        Assert.False(update.OpenFolderCommand.CanExecute(null));

        await update.DownloadCommand.ExecuteAsync(null);
        await update.OpenFolderCommand.ExecuteAsync(null);

        Assert.Equal([Path.Combine(@"C:\updates", "v0.3.0")], launcher.Folders);
    }

    // ---- The controller ----

    [Fact]
    public async Task HelpChecksForUpdatesInOneWindowAndDownloadsNothingByItself()
    {
        var dialogs = new Dialogs();
        var downloader = new Downloader();
        var controller = new UpdateCheckController(dialogs, new UpdateCheckService(Source.Of(Manifest()), downloader, Windows));

        var first = controller.CheckForUpdatesAsync();
        await dialogs.Opened.Task.WaitAsync(Patience, Token);
        await Until(() => dialogs.Last.State == UpdateWindowState.UpdateAvailable);
        await controller.CheckForUpdatesAsync().WaitAsync(Patience, Token);

        Assert.True(controller.IsRunning);
        Assert.Single(dialogs.Shown);
        Assert.Empty(downloader.Calls);

        dialogs.Close();
        await first.WaitAsync(Patience, Token);

        Assert.False(controller.IsRunning);
    }

    [Fact]
    public async Task ClosingTheWindowStopsTheDownload()
    {
        var dialogs = new Dialogs();
        var downloader = new Downloader { Gated = true };
        var controller = new UpdateCheckController(dialogs, new UpdateCheckService(Source.Of(Manifest()), downloader, Windows));

        var running = controller.CheckForUpdatesAsync();
        await dialogs.Opened.Task.WaitAsync(Patience, Token);
        await Until(() => dialogs.Last.State == UpdateWindowState.UpdateAvailable);
        _ = dialogs.Last.DownloadCommand.ExecuteAsync(null);
        await downloader.Halfway.Task.WaitAsync(Patience, Token);
        dialogs.Close();
        await running.WaitAsync(Patience, Token);

        Assert.Equal(UpdateWindowState.UpdateAvailable, dialogs.Last.State);
        Assert.Null(dialogs.Last.Package);
        Assert.False(controller.IsRunning);
    }

    // ---- Install Update (Task #051.C) ----

    private static async Task<(UpdateViewModel Update, Installer Installer, Prompts Prompts)> VerifiedWith(Installer? installer = null, Prompts? prompts = null)
    {
        installer ??= new Installer();
        prompts ??= new Prompts();
        var update = new UpdateViewModel(new UpdateCheckService(Source.Of(Manifest()), new Downloader(), Windows, installer), new Launcher(), prompts);
        await update.CheckAsync();
        await update.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(UpdateWindowState.Verified, update.State);
        return (update, installer, prompts);
    }

    [Fact]
    public async Task InstallUpdateIsOfferedOnlyForAVerifiedPackageOfAReleaseInstallation()
    {
        var installer = new Installer();
        var update = new UpdateViewModel(new UpdateCheckService(Source.Of(Manifest()), new Downloader(), Windows, installer), new Launcher(), new Prompts());
        await update.CheckAsync();

        Assert.True(update.CanInstall);
        Assert.False(update.InstallCommand.CanExecute(null), "not before the package is downloaded and verified");

        await update.DownloadCommand.ExecuteAsync(null);

        Assert.True(update.InstallCommand.CanExecute(null));
        Assert.Equal("Install Update closes YAT, installs this version and starts YAT again.", update.Detail);

        var notARelease = (await VerifiedWith(new Installer { IsAvailable = false })).Update;
        Assert.False(notARelease.CanInstall);
        Assert.False(notARelease.InstallCommand.CanExecute(null));

        var noPrompts = new UpdateViewModel(new UpdateCheckService(Source.Of(Manifest()), new Downloader(), Windows, new Installer()), new Launcher());
        Assert.False(noPrompts.CanInstall);
    }

    [Fact]
    public async Task InstallAsksFirstAndNoChangesNothing()
    {
        var (update, installer, prompts) = await VerifiedWith(prompts: new Prompts { Answer = false });
        var closed = 0;
        update.CloseRequested += (_, _) => closed++;

        await update.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["0.3.0"], prompts.Asked);
        Assert.Equal(0, installer.Prepared);
        Assert.Equal(UpdateWindowState.Verified, update.State);
        Assert.Null(update.PendingInstall);
        Assert.Equal(0, closed);
    }

    [Fact]
    public async Task AConfirmedInstallPreparesTheUpdaterAndClosesTheWindowWithItReady()
    {
        var (update, installer, _) = await VerifiedWith();
        var closed = 0;
        update.CloseRequested += (_, _) => closed++;

        await update.InstallCommand.ExecuteAsync(null);

        Assert.Equal(1, installer.Prepared);
        Assert.NotNull(update.PendingInstall);
        Assert.Equal(1, closed);
        Assert.Equal(UpdateWindowState.Installing, update.State);
        Assert.Equal("Installing YAT 0.3.0...", update.Headline);
        Assert.Empty(installer.Said);
    }

    [Theory]
    [InlineData(UpdateInstallFailure.InstallationNotWritable, "Automatic installation cannot modify YAT's folder")]
    [InlineData(UpdateInstallFailure.OtherYatRunning, "Another YAT window is running from the same folder")]
    [InlineData(UpdateInstallFailure.UpdaterNotReady, "The updater could not be started")]
    [InlineData(UpdateInstallFailure.InsufficientSpace, "not enough free disk space")]
    [InlineData(UpdateInstallFailure.PackageInvalid, "no longer the verified release")]
    [InlineData(UpdateInstallFailure.UpdateInProgress, "already being installed")]
    public async Task AnInstallThatCannotStartKeepsYatOpenSaysWhyAndCanBeRetried(UpdateInstallFailure failure, string reason)
    {
        var installer = new Installer();
        installer.Failures.Enqueue(failure);
        var (update, _, prompts) = await VerifiedWith(installer);
        var closed = 0;
        update.CloseRequested += (_, _) => closed++;

        await update.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateWindowState.Failed, update.State);
        Assert.Equal("YAT 0.3.0 could not be installed.", update.Headline);
        Assert.Contains(reason, update.Detail, StringComparison.Ordinal);
        Assert.Null(update.PendingInstall);
        Assert.Equal(0, closed);
        Assert.True(update.OpenFolderCommand.CanExecute(null), "the downloaded package can still be found");

        await update.RetryCommand.ExecuteAsync(null);

        Assert.Equal(2, prompts.Asked.Count);
        Assert.Equal(2, installer.Prepared);
        Assert.NotNull(update.PendingInstall);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task CancellingThePreparationChangesNothing()
    {
        var installer = new Installer { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (update, _, _) = await VerifiedWith(installer);

        var installing = update.InstallCommand.ExecuteAsync(null);
        await Until(() => update.State == UpdateWindowState.Installing);
        Assert.True(update.CancelCommand.CanExecute(null));
        update.CancelCommand.Execute(null);
        await installing.WaitAsync(Patience, Token);

        Assert.Equal(UpdateWindowState.Verified, update.State);
        Assert.Equal("The installation was cancelled.", update.Detail);
        Assert.Null(update.PendingInstall);
    }

    private static async Task<(Dialogs Dialogs, Installer Installer, Task Running)> InstallThroughTheController(Installer? installer = null, Action<Dialogs>? setUp = null)
    {
        installer ??= new Installer();
        var dialogs = new Dialogs();
        setUp?.Invoke(dialogs);
        var controller = new UpdateCheckController(dialogs, new UpdateCheckService(Source.Of(Manifest()), new Downloader(), Windows, installer));
        var running = controller.CheckForUpdatesAsync();
        await dialogs.Opened.Task.WaitAsync(Patience, Token);
        await Until(() => dialogs.Last.State == UpdateWindowState.UpdateAvailable);
        await dialogs.Last.DownloadCommand.ExecuteAsync(null);
        await dialogs.Last.InstallCommand.ExecuteAsync(null);
        return (dialogs, installer, running);
    }

    [Fact]
    public async Task WhenYatMayCloseTheUpdaterIsToldToGoAndYatExits()
    {
        var (dialogs, installer, running) = await InstallThroughTheController();
        await running.WaitAsync(Patience, Token);

        Assert.Equal(["close approved", "exit"], dialogs.Steps);
        Assert.Equal(["go", "disposed"], installer.Said);
    }

    [Fact]
    public async Task WhenTheUserKeepsYatOpenTheUpdaterIsToldToChangeNothing()
    {
        var (dialogs, installer, running) = await InstallThroughTheController(setUp: dialogs => dialogs.AllowClose = false);
        await running.WaitAsync(Patience, Token);

        Assert.Equal(["close cancelled"], dialogs.Steps);
        Assert.Equal(["cancel", "disposed"], installer.Said);
    }

    [Fact]
    public async Task AnUpdaterGoneWhenToldToGoIsSaidAndYatStillCloses()
    {
        var (dialogs, installer, running) = await InstallThroughTheController(new Installer { UpdaterAlive = false });
        await running.WaitAsync(Patience, Token);

        Assert.Equal(3, dialogs.Steps.Count);
        Assert.StartsWith("message: YAT 0.3.0 could not be installed: the updater stopped.", dialogs.Steps[1], StringComparison.Ordinal);
        Assert.Equal("exit", dialogs.Steps[2]);
        Assert.Equal(["go", "disposed"], installer.Said);
    }

    [Fact]
    public async Task AnUpdaterThatCannotStartLeavesYatOpen()
    {
        var installer = new Installer();
        installer.Failures.Enqueue(UpdateInstallFailure.UpdaterNotReady);
        var (dialogs, _, running) = await InstallThroughTheController(installer);

        Assert.Equal(UpdateWindowState.Failed, dialogs.Last.State);
        Assert.False(running.IsCompleted, "the update window stays open");

        dialogs.Close();
        await running.WaitAsync(Patience, Token);
        Assert.Empty(dialogs.Steps);
    }

    [Fact]
    public async Task ClosingTheWindowOfAVerifiedPackageInstallsNothing()
    {
        var installer = new Installer();
        var dialogs = new Dialogs();
        var controller = new UpdateCheckController(dialogs, new UpdateCheckService(Source.Of(Manifest()), new Downloader(), Windows, installer));
        var running = controller.CheckForUpdatesAsync();
        await dialogs.Opened.Task.WaitAsync(Patience, Token);
        await Until(() => dialogs.Last.State == UpdateWindowState.UpdateAvailable);
        await dialogs.Last.DownloadCommand.ExecuteAsync(null);

        dialogs.Close();
        await running.WaitAsync(Patience, Token);

        Assert.Equal(0, installer.Prepared);
        Assert.Empty(dialogs.Steps);
        Assert.Empty(installer.Said);
    }

    [Fact]
    public void TheUpdatedNoticeIsTakenOnceAndOnlyForThisVersion()
    {
        using var directory = new TemporaryDirectory();
        var composition = new CompositionRoot(TimeProvider.System, directory.File("temp"));
        var installation = directory.File("YAT");
        var marker = ReleaseInstallation.SuccessMarkerPath(installation);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);

        File.WriteAllText(marker, $$"""{"schemaVersion":1,"version":"{{ApplicationInfo.Current.Version}}"}""");
        Assert.Equal(ApplicationInfo.Current.Version, composition.TakeInstalledUpdateNotice(installation));
        Assert.Null(composition.TakeInstalledUpdateNotice(installation));
        Assert.False(File.Exists(marker));

        File.WriteAllText(marker, """{"schemaVersion":1,"version":"99.0.0"}""");
        Assert.Null(composition.TakeInstalledUpdateNotice(installation));
        Assert.False(File.Exists(marker), "a notice of another version is not kept to be shown later");
    }

    [Fact]
    public void TheApplicationsUpdateCheckInstallsOnlyFromAnUnpackedRelease()
    {
        using var directory = new TemporaryDirectory();
        var composition = new CompositionRoot(TimeProvider.System, directory.File("temp"));

        // The test host is not YAT.exe beside a yat-files.json: it may check and download, not install.
        Assert.False(composition.CreateUpdateInstaller(directory.File("updates")).IsAvailable);

        var installation = directory.File("YAT");
        Directory.CreateDirectory(installation);
        File.WriteAllBytes(ReleaseInstallation.InventoryPath(installation), new ReleaseInventory(ReleaseVersion.Parse(ApplicationInfo.Current.Version), "win-x64", []).ToJson());
        var target = new YAT.Infrastructure.Updates.UpdateInstallTarget(installation, ReleaseInstallation.Executable(installation), Environment.ProcessId, 1, ApplicationInfo.Current.Version);
        Assert.True(composition.CreateUpdateInstaller(directory.File("updates"), target).IsAvailable);
        Assert.False(composition.CreateUpdateInstaller(directory.File("updates"), target with { InstalledVersion = "99.0.0" }).IsAvailable);
    }

    // ---- The menu and the composition root ----

    [Fact]
    public void CheckForUpdatesIsInHelpAboveAbout()
    {
        var help = XDocument.Load(Path.Combine(Root, "src", "YAT.App", "Views", "MainWindow.axaml")).Descendants()
            .Single(element => element.Name.LocalName == "MenuItem" && (string?)element.Attribute("Header") == "_Help")
            .Elements().Select(element => (string?)element.Attribute("Header")).ToList();

        Assert.Equal(["Check for _Updates...", "_About YAT..."], help);
    }

    [Fact]
    public async Task TheShellsCommandOpensTheUpdateWindowOnlyWhenThereIsAnUpdateCheck()
    {
        using var directory = new TemporaryDirectory();
        var composition = new CompositionRoot(new FixedTimeProvider(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)), directory.File("temp"));
        using var workspace = composition.CreateProjectWorkspace();
        var clipboard = new FakeClipboard();
        var lifecycle = composition.CreateProjectLifecycle(workspace, clipboard, clipboard, new FakeProjectLifecycleDialogs());
        var graphs = composition.CreateGraphSetup(new FakeGraphSetupDialogs(), new FakeGraphWindowPresenter());
        var results = new FakeAnalysisResultPresenter();
        var statistics = composition.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), results);
        var capability = composition.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), results);
        var dialogs = new Dialogs();
        var updates = composition.CreateUpdateCheck(dialogs, Source.Of(Manifest("0.2.0")), new Downloader(), Windows);

        Assert.False(composition.CreateMainWindowShellViewModel(lifecycle, graphs, statistics, capability).CheckForUpdatesCommand.CanExecute(null));

        var shell = composition.CreateMainWindowShellViewModel(lifecycle, graphs, statistics, capability, updates);
        Assert.True(shell.CheckForUpdatesCommand.CanExecute(null));
        var running = shell.CheckForUpdatesCommand.ExecuteAsync(null);
        await dialogs.Opened.Task.WaitAsync(Patience, Token);
        await Until(() => dialogs.Last.State == UpdateWindowState.UpToDate);
        dialogs.Close();
        await running.WaitAsync(Patience, Token);
    }

    // The production chain - connection, HTTP client, manifest source, downloader, store - over a fake handler.
    private sealed class Server(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }

    [Fact]
    public async Task AnUnpublishedReleaseIsNotAvailableNeverUpToDate()
    {
        using var directory = new TemporaryDirectory();
        var server = new Server(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var connection = new CompositionRoot(TimeProvider.System, directory.File("temp")).CreateUpdateConnection(updatesRoot: directory.File("updates"), handler: server);
        var update = new UpdateViewModel(new UpdateCheckService(connection.Source, connection.Downloader, UpdateEnvironment.Current(ApplicationInfo.Current.Version)), new Launcher());

        await update.CheckAsync();

        Assert.Equal(UpdateWindowState.Failed, update.State);
        Assert.Equal("Update information is not available yet. No release has been published.", update.Detail);
        Assert.Equal([UpdateEndpoint.ManifestUrl!], server.Requests);
        Assert.False(Directory.Exists(directory.File("updates")));
    }

    [Fact]
    public async Task ANewerReleaseIsDownloadedAndVerifiedThroughTheWholeChain()
    {
        using var directory = new TemporaryDirectory();
        var installed = ReleaseVersion.Parse(ApplicationInfo.Current.Version);
        var next = ReleaseVersion.Parse($"{installed.Major}.{installed.Minor + 1}.0");
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("not really a zip ", 20_000)));
        var packageUrl = new Uri($"https://example.test/{next.Tag}/{next.PackageName("win-x64")}.zip");
        var manifest = $$"""
            { "schemaVersion": 1, "version": "{{next}}",
              "packages": [ { "rid": "win-x64", "url": "{{packageUrl}}", "sha256": "{{Convert.ToHexStringLower(SHA256.HashData(payload))}}", "size": {{payload.Length}} } ] }
            """;
        var server = new Server(request => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = request.RequestUri == packageUrl ? new ByteArrayContent(payload) : new StringContent(manifest)
        });
        var root = directory.File("updates");
        using var connection = new CompositionRoot(TimeProvider.System, directory.File("temp")).CreateUpdateConnection(updatesRoot: root, handler: server);
        var update = new UpdateViewModel(new UpdateCheckService(connection.Source, connection.Downloader, UpdateEnvironment.Current(ApplicationInfo.Current.Version)), new Launcher());

        await update.CheckAsync();
        Assert.Equal(UpdateWindowState.UpdateAvailable, update.State);
        Assert.Single(server.Requests);

        await update.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(UpdateWindowState.Verified, update.State);
        var folder = Path.Combine(root, next.Tag);
        Assert.Equal(Path.Combine(folder, next.PackageName("win-x64") + ".zip"), update.Package!.LocalPackagePath);
        Assert.Equal(payload, File.ReadAllBytes(update.Package.LocalPackagePath));
        Assert.True(File.Exists(Path.Combine(folder, ReleaseManifest.FileName)));
        Assert.Equal([UpdateEndpoint.ManifestUrl!, packageUrl], server.Requests);
    }

    // ---- Where the address comes from ----

    [Fact]
    public void TheUpdateAddressIsTheOneInDirectoryBuildProps()
    {
        var written = Assert.Single(XDocument.Load(Path.Combine(Root, "Directory.Build.props")).Descendants("YatUpdateManifestUrl")).Value;

        Assert.Equal(new Uri(written), UpdateEndpoint.ManifestUrl);
        Assert.Equal(written, typeof(ApplicationInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(attribute => attribute.Key == UpdateEndpoint.MetadataKey).Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://example.test/yat-update.json")]
    [InlineData("file:///C:/yat-update.json")]
    [InlineData("not an address")]
    public void ABuildWithoutAnHttpsAddressHasNone(string? value)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("YatUpdateEndpointProbe"), AssemblyBuilderAccess.Run);
        if (value is not null)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!,
                [UpdateEndpoint.MetadataKey, value]));
        }

        Assert.Null(UpdateEndpoint.From(assembly));
    }

    private static string Root
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "YAT.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new InvalidOperationException("Repository root containing YAT.slnx was not found.");
        }
    }
}
