using System.Runtime.InteropServices;
using YAT.Application.Distribution;

namespace YAT.Application.Updates;

// Why checking for, or downloading, an update did not succeed (Task #051.B). A failed check is never "up to date".
public enum UpdateFailure
{
    // The update information is not published (HTTP 404): no release has a manifest yet.
    ManifestNotFound,

    // The update server could not be reached at all.
    NetworkUnavailable,

    // The update server stopped answering.
    Timeout,

    // The update server answered with an error (Detail: the HTTP status).
    HttpError,

    // The update information is not JSON.
    ManifestMalformed,

    // The update information is of a later schema than this YAT understands.
    ManifestNewerSchema,

    // The update information breaks the release manifest's rules, or is too large.
    ManifestInvalid,

    // The release has no package for this computer's runtime.
    NoPackageForRuntime,

    // The release's package is not where the manifest says (HTTP 404).
    PackageNotFound,

    // The download broke off before it was complete.
    DownloadInterrupted,

    // The package is not the size the manifest says.
    SizeMismatch,

    // The package is not the SHA-256 the manifest says.
    HashMismatch,

    // The package or its manifest could not be written (disk space, permissions).
    StorageFailed,

    // This YAT's own version is not a stable release version.
    InstalledVersionUnknown,

    // This computer is not one YAT publishes packages for (Windows x64).
    UnsupportedPlatform
}

// A transport or storage step that failed in a way the update workflow names. Cancellation is never one of these: it is
// an OperationCanceledException, normal control flow.
public sealed class UpdateTransportException : Exception
{
    public UpdateTransportException(UpdateFailure failure, string? detail = null, Exception? innerException = null)
        : base(detail ?? failure.ToString(), innerException)
    {
        Failure = failure;
        Detail = detail;
    }

    public UpdateFailure Failure { get; }

    public string? Detail { get; }
}

// A newer release, offered for download: its validated manifest and its package for this computer.
public sealed record UpdateOffer(ReleaseManifest Manifest, ReleasePackage Package)
{
    public ReleaseVersion Version => Manifest.Version;

    public Uri? ReleaseNotesUrl => Manifest.ReleaseNotesUrl;
}

// A downloaded package whose size and SHA-256 were computed from the file itself and match the manifest, kept with a
// snapshot of that manifest beside it - what a future installer starts from (and verifies again).
public sealed record VerifiedUpdatePackage(
    ReleaseVersion Version,
    string Rid,
    string LocalPackagePath,
    long Size,
    string Sha256,
    Uri? ReleaseNotesUrl);

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,

    // The published release is older than this YAT (a development build): shown as up to date, kept apart here.
    RemoteVersionOlder,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    ReleaseVersion? Installed,
    ReleaseVersion? Available,
    UpdateOffer? Offer,
    UpdateFailure? Failure,
    string? Detail)
{
    public static UpdateCheckResult Failed(UpdateFailure failure, ReleaseVersion? installed = null, string? detail = null) =>
        new(UpdateCheckStatus.Failed, installed, null, null, failure, detail);
}

public sealed record UpdateDownloadResult(VerifiedUpdatePackage? Package, UpdateFailure? Failure, string? Detail)
{
    public bool Succeeded => Package is not null;
}

// How far a download has come. Verifying: the bytes are all there and the package is being checked.
public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes, bool Verifying = false);

// What this YAT is and runs on, as far as updates go.
public sealed record UpdateEnvironment(string InstalledVersion, bool IsWindows, Architecture ProcessArchitecture)
{
    public static UpdateEnvironment Current(string installedVersion) =>
        new(installedVersion, OperatingSystem.IsWindows(), RuntimeInformation.ProcessArchitecture);
}

// Where the update information comes from. Throws UpdateTransportException for what went wrong on the way.
public interface IUpdateManifestSource
{
    Task<string> FetchAsync(CancellationToken cancellationToken);
}

// Downloads and verifies release packages. Throws UpdateTransportException for what went wrong on the way.
public interface IUpdatePackageDownloader
{
    // The offer's package as already downloaded - its size and SHA-256 computed again from the file and matching - or
    // null when there is none that can be trusted.
    Task<VerifiedUpdatePackage?> FindVerifiedAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken);

    Task<VerifiedUpdatePackage> DownloadAsync(UpdateOffer offer, string rid, IProgress<UpdateDownloadProgress>? progress, CancellationToken cancellationToken);
}
