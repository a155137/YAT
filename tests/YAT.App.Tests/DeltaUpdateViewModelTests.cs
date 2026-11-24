using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using YAT.app.Updates;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

public sealed class DeltaUpdateViewModelTests : IDisposable
{
    private static readonly ReleaseVersion V2 = ReleaseVersion.Parse("0.2.0");
    private static readonly ReleaseVersion V3 = ReleaseVersion.Parse("0.3.0");
    private static readonly UpdateEnvironment Windows = new("0.2.0", true, Architecture.X64);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yat-stage4b-tests", Guid.NewGuid().ToString("N"));
    private readonly string _installDir;
    private readonly string _updatesDir;

    public DeltaUpdateViewModelTests()
    {
        _installDir = Path.Combine(_root, "install");
        _updatesDir = Path.Combine(_root, "updates");
        Directory.CreateDirectory(_installDir);
        Directory.CreateDirectory(_updatesDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static string ManifestJson(long fullSize = 20_000_000, long deltaSize = 2_000_000) => $$"""
        {
          "schemaVersion": 1,
          "version": "0.3.0",
          "packages": [
            { "rid": "win-x64", "url": "https://example.test/YAT-v0.3.0-win-x64.zip", "sha256": "{{new string('a', 64)}}", "size": {{fullSize}} }
          ],
          "deltas": [
            { "rid": "win-x64", "baseVersion": "0.2.0", "url": "https://example.test/YAT-v0.2.0-to-0.3.0-win-x64.delta.zip", "sha256": "{{new string('b', 64)}}", "size": {{deltaSize}} }
          ]
        }
        """;

    private string CreateValidDeltaZip()
    {
        var baseExe = Encoding.UTF8.GetBytes("exe 0.2.0");
        var newExe = Encoding.UTF8.GetBytes("exe 0.3.0");
        var baseInventory = new ReleaseInventory(V2, "win-x64", [new ReleaseFile("YAT.exe", baseExe.LongLength, Sha(baseExe))]);
        var baseInventoryJson = baseInventory.ToJson();
        var targetInventory = new ReleaseInventory(V3, "win-x64", [new ReleaseFile("YAT.exe", newExe.LongLength, Sha(newExe))]);
        var deltaManifest = new ReleaseDeltaManifest(V2, V3, "win-x64", Sha(baseInventoryJson), ["YAT.exe"], []);

        var zipPath = Path.Combine(_updatesDir, "delta.zip");
        File.Delete(zipPath);
        var top = V3.PackageName("win-x64") + "/";
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var (name, data) in new[]
            {
                (top + "YAT.exe", newExe),
                (top + ReleaseInventory.FileName, targetInventory.ToJson()),
                (top + ReleaseDeltaManifest.FileName, deltaManifest.ToJson())
            })
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return zipPath;
    }

    private UpdateViewModel CreateViewModel(
        string manifestJson,
        FakeDownloader downloader,
        FakePreVerifier? preVerifier = null,
        FakeInstaller? installer = null,
        FakePrompts? prompts = null)
    {
        var service = new UpdateCheckService(
            new FakeSource(manifestJson),
            downloader,
            Windows,
            installer ?? new FakeInstaller(),
            preVerifier ?? new FakePreVerifier(eligible: true, preVerifyResult: new DeltaPreVerificationResult(true)),
            _installDir);
        return new UpdateViewModel(service, new FakeLauncher(), prompts ?? new FakePrompts());
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met within timeout.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DeltaEligible_DownloadsDeltaAndTransitionsToVerified()
    {
        var deltaZip = CreateValidDeltaZip();
        var downloader = new FakeDownloader(deltaZip, Path.Combine(_updatesDir, "full.zip"));
        var vm = CreateViewModel(ManifestJson(), downloader);

        await vm.CheckAsync();
        Assert.Equal(UpdateWindowState.UpdateAvailable, vm.State);
        Assert.Equal(UpdateViewModel.Megabytes(2_000_000), vm.DownloadSize);

        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["delta"], downloader.Calls);
        Assert.Equal(UpdateWindowState.Verified, vm.State);
        Assert.NotNull(vm.Package);
        Assert.True(vm.Package!.IsDelta);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeltaIneligible_UsesFullZipDirectly()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"));
        var vmIneligible = CreateViewModel(
            ManifestJson(),
            downloader,
            new FakePreVerifier(eligible: false, new DeltaPreVerificationResult(true)));

        await vmIneligible.CheckAsync();
        Assert.Equal(UpdateViewModel.Megabytes(20_000_000), vmIneligible.DownloadSize);
        await vmIneligible.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["find", "full"], downloader.Calls);
        Assert.Equal(UpdateWindowState.Verified, vmIneligible.State);
        Assert.False(vmIneligible.Package!.IsDelta);
    }

    [Theory]
    [InlineData(2_000_000, 2_000_000)]
    [InlineData(2_000_000, 2_500_000)]
    public async Task DeltaEqualOrLargerThanFullZip_SelectsFullZipDirectlyWithoutFallback(long fullSize, long deltaSize)
    {
        var details = new List<string?>();
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, $"full-{deltaSize}.zip"));
        var vm = CreateViewModel(ManifestJson(fullSize: fullSize, deltaSize: deltaSize), downloader);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(UpdateViewModel.Detail))
            {
                details.Add(vm.Detail);
            }
        };

        await vm.CheckAsync();
        Assert.Equal(UpdateViewModel.Megabytes(fullSize), vm.DownloadSize);
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["find", "full"], downloader.Calls);
        Assert.Equal(UpdateWindowState.Verified, vm.State);
        Assert.False(vm.Package!.IsDelta);
        Assert.DoesNotContain("Switching to full update package.", details);
    }

    [Fact]
    public async Task TerminalProgressCallback_AfterVerified_DoesNotOverwriteStateSizeOrInstallCommand()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"));
        var vm = CreateViewModel(ManifestJson(fullSize: 20_000_000, deltaSize: 2_000_000), downloader);

        await vm.CheckAsync();
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(UpdateWindowState.Verified, vm.State);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal(UpdateViewModel.Megabytes(2_000_000), vm.DownloadSize);
        Assert.True(vm.InstallCommand.CanExecute(null));

        downloader.LastDeltaProgress?.Report(new UpdateDownloadProgress(100_000, 2_000_000, Verifying: false, UpdateDownloadPhase.Delta));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateWindowState.Verified, vm.State);
        Assert.Equal(100, vm.ProgressPercent);
        Assert.Equal(UpdateViewModel.Megabytes(2_000_000), vm.DownloadSize);
        Assert.True(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeltaPreVerifying_IgnoresLateDeltaDownloadProgressCallbacks()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"));
        var preVerifier = new FakePreVerifier(eligible: true, new DeltaPreVerificationResult(true)) { HoldVerify = true };
        var vm = CreateViewModel(ManifestJson(fullSize: 20_000_000, deltaSize: 2_000_000), downloader, preVerifier);

        await vm.CheckAsync();
        var running = vm.DownloadCommand.ExecuteAsync(null);
        await preVerifier.VerifyStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateWindowState.Verifying, vm.State);
        downloader.LastDeltaProgress?.Report(new UpdateDownloadProgress(1_000_000, 2_000_000, Verifying: false, UpdateDownloadPhase.Delta));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateWindowState.Verifying, vm.State);
        Assert.Equal(100, vm.ProgressPercent);

        preVerifier.ReleaseVerify();
        await running;
        Assert.Equal(UpdateWindowState.Verified, vm.State);
    }

    [Fact]
    public async Task DeltaDownloadFailure_FallsBackToFullZipAutomatically()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            DeltaFailure = UpdateFailure.DownloadInterrupted
        };
        var vm = CreateViewModel(ManifestJson(), downloader);

        await vm.CheckAsync();
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["delta", "find", "full"], downloader.Calls);
        Assert.Equal(UpdateWindowState.Verified, vm.State);
        Assert.False(vm.Package!.IsDelta);
        Assert.Equal(UpdateViewModel.Megabytes(20_000_000), vm.DownloadSize);
    }

    [Fact]
    public async Task DeltaPreVerificationFailure_FallsBackToFullZipAutomatically()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"));
        var preVerifier = new FakePreVerifier(eligible: true, new DeltaPreVerificationResult(false, "Kept file mismatch"));
        var vm = CreateViewModel(ManifestJson(), downloader, preVerifier);

        await vm.CheckAsync();
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(["delta", "find", "full"], downloader.Calls);
        Assert.Equal(UpdateWindowState.Verified, vm.State);
        Assert.False(vm.Package!.IsDelta);
    }

    [Fact]
    public async Task Fallback_ResetsDownloadSizeProgressPercentAndProgressText()
    {
        var observed = new List<(string? Size, double Percent, string? Text, string? Detail)>();
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            DeltaFailure = UpdateFailure.HashMismatch,
            HoldFullDownload = true
        };
        var vm = CreateViewModel(ManifestJson(fullSize: 20_000_000, deltaSize: 2_000_000), downloader);
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(UpdateViewModel.ProgressPercent) or nameof(UpdateViewModel.ProgressText) or nameof(UpdateViewModel.DownloadSize) or nameof(UpdateViewModel.Detail))
            {
                observed.Add((vm.DownloadSize, vm.ProgressPercent, vm.ProgressText, vm.Detail));
            }
        };

        await vm.CheckAsync();
        var running = vm.DownloadCommand.ExecuteAsync(null);
        await downloader.FullStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        Assert.Contains(observed, entry => entry.Detail == "Switching to full update package." && entry.Percent == 0 && entry.Size == UpdateViewModel.Megabytes(20_000_000));
        downloader.ReleaseFull();
        await running;

        Assert.Equal(UpdateWindowState.Verified, vm.State);
        Assert.Equal(UpdateViewModel.Megabytes(20_000_000), vm.DownloadSize);
    }

    [Fact]
    public async Task Fallback_IgnoresLateDeltaProgressCallbacks()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            DeltaFailure = UpdateFailure.DownloadInterrupted,
            HoldFullDownload = true
        };
        var vm = CreateViewModel(ManifestJson(fullSize: 20_000_000, deltaSize: 2_000_000), downloader);

        await vm.CheckAsync();
        var running = vm.DownloadCommand.ExecuteAsync(null);
        await downloader.FullStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Simulate a late delta progress callback arriving after fallback has started.
        downloader.LastDeltaProgress?.Report(new UpdateDownloadProgress(1_900_000, 2_000_000, Verifying: false, UpdateDownloadPhase.Delta));
        await Task.Delay(20, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateViewModel.Megabytes(20_000_000), vm.DownloadSize);
        Assert.DoesNotContain(UpdateViewModel.Megabytes(2_000_000), vm.ProgressText ?? string.Empty, StringComparison.Ordinal);

        downloader.ReleaseFull();
        await running;
    }

    [Fact]
    public async Task InstallDispatch_RoutesDeltaToPrepareDeltaInstallAsync_AndFullToPrepareInstallAsync()
    {
        var installer1 = new FakeInstaller();
        var vmDelta = CreateViewModel(ManifestJson(), new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip")), installer: installer1);
        await vmDelta.CheckAsync();
        await vmDelta.DownloadCommand.ExecuteAsync(null);
        await vmDelta.InstallCommand.ExecuteAsync(null);

        Assert.Equal([true], installer1.PreparedIsDeltaCalls);

        var installer2 = new FakeInstaller();
        var vmFull = CreateViewModel(
            ManifestJson(),
            new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full2.zip")),
            new FakePreVerifier(eligible: false, new DeltaPreVerificationResult(true)),
            installer2);
        await vmFull.CheckAsync();
        await vmFull.DownloadCommand.ExecuteAsync(null);
        await vmFull.InstallCommand.ExecuteAsync(null);

        Assert.Equal([false], installer2.PreparedIsDeltaCalls);
    }

    [Fact]
    public async Task InstallDispatch_AfterFallback_UsesFullZipPrepareInstallAsync()
    {
        var installer = new FakeInstaller();
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            DeltaFailure = UpdateFailure.DownloadInterrupted
        };
        var vm = CreateViewModel(ManifestJson(), downloader, installer: installer);

        await vm.CheckAsync();
        await vm.DownloadCommand.ExecuteAsync(null);
        Assert.False(vm.Package!.IsDelta);

        await vm.InstallCommand.ExecuteAsync(null);
        Assert.Equal([false], installer.PreparedIsDeltaCalls);
    }

    [Fact]
    public async Task Cancel_DuringDeltaDownload_DoesNotTriggerFallback()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            HoldDeltaDownload = true
        };
        var vm = CreateViewModel(ManifestJson(), downloader);

        await vm.CheckAsync();
        var running = vm.DownloadCommand.ExecuteAsync(null);
        await downloader.DeltaStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        vm.CancelCommand.Execute(null);
        await running;

        Assert.Equal(["delta"], downloader.Calls);
        Assert.Equal(UpdateWindowState.UpdateAvailable, vm.State);
        Assert.Equal("The download was cancelled.", vm.Detail);
        Assert.Null(vm.Package);
    }

    [Fact]
    public async Task Cancel_DuringDeltaPreVerification_DoesNotTriggerFallback()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"));
        var preVerifier = new FakePreVerifier(eligible: true, new DeltaPreVerificationResult(true)) { HoldVerify = true };
        var vm = CreateViewModel(ManifestJson(), downloader, preVerifier);

        await vm.CheckAsync();
        var running = vm.DownloadCommand.ExecuteAsync(null);
        await preVerifier.VerifyStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        vm.CancelCommand.Execute(null);
        await running;

        Assert.Equal(["delta"], downloader.Calls);
        Assert.Equal(UpdateWindowState.UpdateAvailable, vm.State);
        Assert.Null(vm.Package);
    }

    [Fact]
    public async Task Cancel_DuringFullZipFallbackDownload_ReturnsToUpdateAvailableWithoutStalePackage()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            DeltaFailure = UpdateFailure.DownloadInterrupted,
            HoldFullDownload = true
        };
        var vm = CreateViewModel(ManifestJson(), downloader);

        await vm.CheckAsync();
        var running = vm.DownloadCommand.ExecuteAsync(null);
        await downloader.FullStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        vm.CancelCommand.Execute(null);
        await running;

        Assert.Equal(UpdateWindowState.UpdateAvailable, vm.State);
        Assert.Null(vm.Package);
        Assert.False(vm.InstallCommand.CanExecute(null));
    }

    [Fact]
    public async Task FallbackFailure_ShowsErrorState()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            DeltaFailure = UpdateFailure.DownloadInterrupted,
            FullFailure = UpdateFailure.NetworkUnavailable
        };
        var vm = CreateViewModel(ManifestJson(), downloader);

        await vm.CheckAsync();
        await vm.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(UpdateWindowState.Failed, vm.State);
        Assert.Equal(UpdateViewModel.Describe(UpdateFailure.NetworkUnavailable), vm.Detail);
        Assert.Null(vm.Package);
    }

    [Fact]
    public async Task InstallPreparationFailure_KeepsYatOpenInFailedState()
    {
        var installer = new FakeInstaller { Failure = UpdateInstallFailure.PackageInvalid };
        var vm = CreateViewModel(ManifestJson(), new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip")), installer: installer);
        var closeRequested = false;
        vm.CloseRequested += (_, _) => closeRequested = true;

        await vm.CheckAsync();
        await vm.DownloadCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(UpdateWindowState.Failed, vm.State);
        Assert.Null(vm.PendingInstall);
        Assert.False(closeRequested);
    }

    [Fact]
    public async Task Concurrency_PreventsDuplicateDownloadAndDuplicateInstall()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            HoldDeltaDownload = true
        };
        var installer = new FakeInstaller { HoldPrepare = true };
        var vm = CreateViewModel(ManifestJson(), downloader, installer: installer);

        await vm.CheckAsync();
        var dl1 = vm.DownloadCommand.ExecuteAsync(null);
        await downloader.DeltaStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var dl2 = vm.DownloadCommand.ExecuteAsync(null);
        Assert.Equal(["delta"], downloader.Calls);

        downloader.ReleaseDelta();
        await dl1;
        await dl2;
        Assert.Equal(UpdateWindowState.Verified, vm.State);

        var inst1 = vm.InstallCommand.ExecuteAsync(null);
        await installer.PrepareStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var inst2 = vm.InstallCommand.ExecuteAsync(null);
        Assert.Single(installer.PreparedIsDeltaCalls);

        installer.ReleasePrepare();
        await inst1;
        await inst2;
        Assert.NotNull(vm.PendingInstall);
    }

    [Fact]
    public async Task CancelAndRetryDownload_DoesNotPolluteNextRoundWithStaleCallbacks()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"))
        {
            HoldDeltaDownload = true
        };
        var vm = CreateViewModel(ManifestJson(fullSize: 20_000_000, deltaSize: 2_000_000), downloader);

        await vm.CheckAsync();
        var round1 = vm.DownloadCommand.ExecuteAsync(null);
        await downloader.DeltaStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);
        var staleProgress = downloader.LastDeltaProgress;

        vm.CancelCommand.Execute(null);
        await round1;

        downloader.HoldDeltaDownload = false;
        downloader.DeltaFailure = UpdateFailure.DownloadInterrupted;
        downloader.HoldFullDownload = true;

        var round2 = vm.DownloadCommand.ExecuteAsync(null);
        await downloader.FullStarted.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // Late callback from cancelled round 1 must not overwrite round 2.
        staleProgress?.Report(new UpdateDownloadProgress(500_000, 2_000_000, Verifying: false, UpdateDownloadPhase.Delta));
        await Task.Delay(20, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateViewModel.Megabytes(20_000_000), vm.DownloadSize);

        downloader.ReleaseFull();
        await round2;
        Assert.Equal(UpdateWindowState.Verified, vm.State);
    }

    [Fact]
    public async Task FullZip_ExistingUiFlowRemainsCompatible()
    {
        var downloader = new FakeDownloader(CreateValidDeltaZip(), Path.Combine(_updatesDir, "full.zip"));
        var installer = new FakeInstaller();
        var service = new UpdateCheckService(
            new FakeSource(ManifestJson()),
            downloader,
            Windows,
            installer);
        var vm = new UpdateViewModel(service, new FakeLauncher(), new FakePrompts());

        await vm.CheckAsync();
        await vm.DownloadCommand.ExecuteAsync(null);
        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Equal(["find", "full"], downloader.Calls);
        Assert.Equal([false], installer.PreparedIsDeltaCalls);
        Assert.NotNull(vm.PendingInstall);
    }

    private sealed class FakeSource(string json) : IUpdateManifestSource
    {
        public Task<string> FetchAsync(CancellationToken cancellationToken) => Task.FromResult(json);
    }

    private sealed class FakePreVerifier(bool eligible, DeltaPreVerificationResult preVerifyResult) : ILocalDeltaPreVerifier
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HoldVerify { get; set; }

        public void ReleaseVerify() => _gate.TrySetResult();

        public TaskCompletionSource VerifyStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsLocalInventoryEligible(string installationDirectory, ReleaseVersion expectedBaseVersion, string expectedRid) => eligible;

        public async Task<DeltaPreVerificationResult> VerifyLocalAsync(
            string installationDirectory,
            ReleaseVersion expectedBaseVersion,
            string expectedRid,
            ReleaseDeltaManifest deltaManifest,
            ReleaseInventory targetInventory,
            CancellationToken cancellationToken)
        {
            VerifyStarted.TrySetResult();
            if (HoldVerify)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            return preVerifyResult;
        }
    }

    private sealed class FakeDownloader(string deltaPath, string fullPath) : IUpdatePackageDownloader
    {
        private readonly TaskCompletionSource _deltaGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fullGate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];
        public UpdateFailure? DeltaFailure { get; set; }
        public UpdateFailure? FullFailure { get; set; }
        public bool HoldDeltaDownload { get; set; }
        public bool HoldFullDownload { get; set; }
        public TaskCompletionSource DeltaStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FullStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IProgress<UpdateDownloadProgress>? LastDeltaProgress { get; private set; }

        public void ReleaseDelta() => _deltaGate.TrySetResult();
        public void ReleaseFull() => _fullGate.TrySetResult();

        public Task<VerifiedUpdatePackage?> FindVerifiedAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("find");
            return Task.FromResult<VerifiedUpdatePackage?>(null);
        }

        public async Task<VerifiedUpdatePackage> DownloadDeltaAsync(UpdateOffer offer, ReleaseDeltaPackage delta, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("delta");
            LastDeltaProgress = progress;
            progress?.Report(new UpdateDownloadProgress(delta.Size / 2, delta.Size, Verifying: false, UpdateDownloadPhase.Delta));
            DeltaStarted.TrySetResult();
            if (HoldDeltaDownload)
            {
                await _deltaGate.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (DeltaFailure is { } failure)
            {
                throw new UpdateTransportException(failure);
            }

            progress?.Report(new UpdateDownloadProgress(delta.Size, delta.Size, Verifying: true, UpdateDownloadPhase.Delta));
            return new VerifiedUpdatePackage(offer.Version, rid, deltaPath, delta.Size, delta.Sha256, offer.ReleaseNotesUrl, IsDelta: true, BaseVersion: delta.BaseVersion);
        }

        public async Task<VerifiedUpdatePackage> DownloadAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("full");
            progress?.Report(new UpdateDownloadProgress(offer.Package.Size / 2, offer.Package.Size, Verifying: false, UpdateDownloadPhase.Full));
            FullStarted.TrySetResult();
            if (HoldFullDownload)
            {
                await _fullGate.Task.WaitAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (FullFailure is { } failure)
            {
                throw new UpdateTransportException(failure);
            }

            File.WriteAllBytes(fullPath, Encoding.UTF8.GetBytes("full zip"));
            progress?.Report(new UpdateDownloadProgress(offer.Package.Size, offer.Package.Size, Verifying: true, UpdateDownloadPhase.Full));
            return new VerifiedUpdatePackage(offer.Version, rid, fullPath, offer.Package.Size, offer.Package.Sha256, offer.ReleaseNotesUrl, IsDelta: false);
        }
    }

    private sealed class FakeInstaller : IUpdateInstaller
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsAvailable => true;
        public UpdateInstallFailure? Failure { get; set; }
        public bool HoldPrepare { get; set; }
        public List<bool> PreparedIsDeltaCalls { get; } = [];
        public TaskCompletionSource PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleasePrepare() => _gate.TrySetResult();

        public async Task<UpdateInstallPreparation> PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
        {
            PreparedIsDeltaCalls.Add(package.IsDelta);
            PrepareStarted.TrySetResult();
            if (HoldPrepare)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            if (Failure is { } failure)
            {
                return UpdateInstallPreparation.Failed(failure);
            }

            return new UpdateInstallPreparation(new FakeHandoff(), null, null);
        }
    }

    private sealed class FakeHandoff : IUpdateHandoff
    {
        public bool Go() => true;
        public void Cancel() { }
        public void Dispose() { }
    }

    private sealed class FakePrompts : IUpdatePrompts
    {
        public Task<bool> ConfirmInstallAsync(string version) => Task.FromResult(true);
    }

    private sealed class FakeLauncher : IUpdateLauncher
    {
        public Task<bool> OpenUriAsync(Uri uri) => Task.FromResult(true);
        public Task<bool> OpenFolderAsync(string path) => Task.FromResult(true);
    }
}
