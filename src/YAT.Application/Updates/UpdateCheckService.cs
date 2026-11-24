using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using YAT.Application.Distribution;

namespace YAT.Application.Updates;

// Checking for an update, and downloading one when the user asks (Task #051.B): the release manifest is fetched, read
// with the release contract's own reader (Task #051.A), its version compared with this YAT's, and its package for this
// computer offered. Downloading is a separate step the user starts; it gives a package verified against the manifest.
// Installing it is a third (Task #051.C), when this YAT has an installer: the updater is prepared and started here, and
// installs once YAT has closed.
//
// A failure is a result of its own, never "up to date". Cancellation is an OperationCanceledException.
public sealed class UpdateCheckService
{
    // The one runtime YAT publishes packages for.
    public const string SupportedRid = "win-x64";

    private readonly IUpdateManifestSource _source;
    private readonly IUpdatePackageDownloader _downloader;
    private readonly UpdateEnvironment _environment;
    private readonly IUpdateInstaller? _installer;
    private readonly ILocalDeltaPreVerifier? _deltaPreVerifier;
    private readonly string? _installationDirectory;

    public UpdateCheckService(
        IUpdateManifestSource source,
        IUpdatePackageDownloader downloader,
        UpdateEnvironment environment,
        IUpdateInstaller? installer = null,
        ILocalDeltaPreVerifier? deltaPreVerifier = null,
        string? installationDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(downloader);
        ArgumentNullException.ThrowIfNull(environment);
        _source = source;
        _downloader = downloader;
        _environment = environment;
        _installer = installer;
        _deltaPreVerifier = deltaPreVerifier;
        _installationDirectory = installationDirectory;
    }

    // Whether a verified package can be installed from here (Task #051.C).
    public bool CanInstall => _installer?.IsAvailable == true;

    // This YAT's version, or null when it is not a stable release version.
    public ReleaseVersion? Installed => ReleaseVersion.TryParse(_environment.InstalledVersion, out var version) ? version : null;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!_environment.IsWindows || _environment.ProcessArchitecture != Architecture.X64)
        {
            return UpdateCheckResult.Failed(UpdateFailure.UnsupportedPlatform, Installed);
        }

        if (Installed is not { } installed)
        {
            return UpdateCheckResult.Failed(UpdateFailure.InstalledVersionUnknown, detail: _environment.InstalledVersion);
        }

        string json;
        try
        {
            json = await _source.FetchAsync(cancellationToken);
        }
        catch (UpdateTransportException exception)
        {
            return UpdateCheckResult.Failed(exception.Failure, installed, exception.Detail);
        }

        if (!IsJson(json))
        {
            return UpdateCheckResult.Failed(UpdateFailure.ManifestMalformed, installed);
        }

        var read = ReleaseManifestReader.Read(json);
        switch (read.Status)
        {
            case ReleaseManifestStatus.NewerSchema:
                return UpdateCheckResult.Failed(UpdateFailure.ManifestNewerSchema, installed, $"schema version {read.SchemaVersion}");
            case ReleaseManifestStatus.Invalid:
                return UpdateCheckResult.Failed(UpdateFailure.ManifestInvalid, installed, string.Join(" ", read.Problems));
        }

        var manifest = read.Manifest!;
        if (manifest.PackageFor(SupportedRid) is not { } package)
        {
            return UpdateCheckResult.Failed(UpdateFailure.NoPackageForRuntime, installed, $"no {SupportedRid} package in {manifest.Version}");
        }

        var comparison = manifest.Version.CompareTo(installed);
        return comparison switch
        {
            > 0 => new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, installed, manifest.Version, new UpdateOffer(manifest, package), null, null),
            0 => new UpdateCheckResult(UpdateCheckStatus.UpToDate, installed, manifest.Version, null, null, null),
            _ => new UpdateCheckResult(UpdateCheckStatus.RemoteVersionOlder, installed, manifest.Version, null, null, null)
        };
    }

    // The offer's package, verified: one already downloaded is used when its size and SHA-256, computed again, match;
    // otherwise it is downloaded and verified now.
    public async Task<UpdateDownloadResult> DownloadAsync(
        UpdateOffer offer,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        try
        {
            var package = await _downloader.FindVerifiedAsync(offer, SupportedRid, progress, cancellationToken)
                ?? await _downloader.DownloadAsync(offer, SupportedRid, progress, cancellationToken);
            return new UpdateDownloadResult(package, null, null);
        }
        catch (UpdateTransportException exception)
        {
            return new UpdateDownloadResult(null, exception.Failure, exception.Detail);
        }
    }

    public ReleaseDeltaPackage? SelectEligibleDelta(UpdateOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        if (Installed is { } installed &&
            _deltaPreVerifier is not null &&
            !string.IsNullOrWhiteSpace(_installationDirectory) &&
            offer.Manifest.DeltaFor(SupportedRid, installed) is { } delta &&
            delta.Size < offer.Package.Size &&
            _deltaPreVerifier.IsLocalInventoryEligible(_installationDirectory, installed, SupportedRid))
        {
            return delta;
        }

        return null;
    }

    // Stage 3: attempts differential download + local pre-verification when eligible, and automatically falls back
    // to Full ZIP on any non-cancellation delta failure. Never falls back when the user cancels.
    public async Task<UpdateDownloadResult> DownloadDeltaOrFallbackAsync(
        UpdateOffer offer,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);

        if (Installed is { } installed &&
            !string.IsNullOrWhiteSpace(_installationDirectory) &&
            SelectEligibleDelta(offer) is { } delta)
        {
            VerifiedUpdatePackage? verifiedDelta = null;
            var deltaProgress = progress is null
                ? null
                : new SynchronousProgress<UpdateDownloadProgress>(step =>
                    progress.Report(step with { Phase = UpdateDownloadPhase.Delta }));
            try
            {
                verifiedDelta = await _downloader.DownloadDeltaAsync(offer, delta, SupportedRid, deltaProgress, cancellationToken);
                progress?.Report(new UpdateDownloadProgress(
                    verifiedDelta.Size,
                    verifiedDelta.Size,
                    Verifying: true,
                    Phase: UpdateDownloadPhase.DeltaPreVerifying));

                if (await PreVerifyDeltaPackageAsync(_installationDirectory, installed, offer.Version, verifiedDelta.LocalPackagePath, cancellationToken))
                {
                    return new UpdateDownloadResult(verifiedDelta, null, null);
                }

                TryDeleteFile(verifiedDelta.LocalPackagePath);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (UpdateTransportException)
            {
                if (verifiedDelta is not null)
                {
                    TryDeleteFile(verifiedDelta.LocalPackagePath);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                if (verifiedDelta is not null)
                {
                    TryDeleteFile(verifiedDelta.LocalPackagePath);
                }
            }

            progress?.Report(new UpdateDownloadProgress(
                0,
                offer.Package.Size,
                Verifying: false,
                Phase: UpdateDownloadPhase.SwitchingToFull));
        }

        // Single Full ZIP fallback (no retry loop).
        var fullProgress = progress is null
            ? null
            : new SynchronousProgress<UpdateDownloadProgress>(step =>
                progress.Report(step with { Phase = UpdateDownloadPhase.Full }));
        return await DownloadAsync(offer, fullProgress, cancellationToken);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // Prepares installing a verified package: the updater is ready, or why it is not. Never throws for a failure.
    // Differential packages are rejected on the default UI path until Stage 4B enables UI integration.
    public Task<UpdateInstallPreparation> PrepareInstallAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.IsDelta)
        {
            return Task.FromResult(UpdateInstallPreparation.Failed(
                UpdateInstallFailure.NotAReleaseInstallation,
                "Differential package installation is not enabled on the default UI path before Stage 4B."));
        }

        return _installer is null
            ? Task.FromResult(UpdateInstallPreparation.Failed(UpdateInstallFailure.NotAReleaseInstallation))
            : _installer.PrepareAsync(package, cancellationToken);
    }

    // Stage 4A explicit delta installation path: delegates to IUpdateInstaller, which independently re-verifies
    // the delta package, snapshot, and updater binary without trusting package.IsDelta alone.
    public Task<UpdateInstallPreparation> PrepareDeltaInstallAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (!package.IsDelta)
        {
            return Task.FromResult(UpdateInstallPreparation.Failed(
                UpdateInstallFailure.PackageInvalid,
                "Expected a differential update package."));
        }

        return _installer is null
            ? Task.FromResult(UpdateInstallPreparation.Failed(UpdateInstallFailure.NotAReleaseInstallation))
            : _installer.PrepareAsync(package, cancellationToken);
    }

    private async Task<bool> PreVerifyDeltaPackageAsync(
        string installationDirectory,
        ReleaseVersion baseVersion,
        ReleaseVersion targetVersion,
        string deltaZipPath,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(deltaZipPath);
        var contents = ReleasePackageVerifier.ReadDelta(archive, baseVersion, targetVersion, SupportedRid, verifyContents: false);
        if (!contents.IsValid)
        {
            return false;
        }

        var result = await _deltaPreVerifier!.VerifyLocalAsync(
            installationDirectory,
            baseVersion,
            SupportedRid,
            contents.Delta!,
            contents.TargetInventory!,
            cancellationToken);

        return result.IsValid;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool IsJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
