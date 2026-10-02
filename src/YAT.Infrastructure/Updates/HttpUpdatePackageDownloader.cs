using System.Net;
using System.Security.Cryptography;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Infrastructure.Updates;

// Downloads a release package and verifies it against its manifest (Task #051.B), streaming - the package is never held
// in memory:
//
//     HTTP response -> .partial file, counting the bytes and hashing them (SHA-256) as they come
//
//     1. a Content-Length, when the server sends one, must be the manifest's size;
//     2. the download stops as soon as more bytes come than the manifest's size;
//     3. all of them must be the manifest's size;
//     4. their SHA-256 must be the manifest's;
//     5. only then is the .partial renamed to the package's own name, and
//     6. the manifest written beside it.
//
// Whatever goes wrong, or a cancellation, removes the .partial: an incomplete or unverified download never has the
// package's name. A package already there is trusted only once its size and SHA-256 are computed again from the file.
// A package that cannot have its manifest written beside it is not reported ready (StorageFailed). Nothing is extracted.
public sealed class HttpUpdatePackageDownloader : IUpdatePackageDownloader
{
    public const long MaximumPackageBytes = 2L * 1024 * 1024 * 1024;

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private const int BufferSize = 81920;

    private readonly HttpClient _client;
    private readonly UpdatePackageStore _store;
    private readonly TimeSpan _idleTimeout;

    public HttpUpdatePackageDownloader(HttpClient client, UpdatePackageStore store, TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        _client = client;
        _store = store;
        _idleTimeout = idleTimeout ?? DefaultIdleTimeout;
    }

    // Test-only: called after each chunk is written to the .partial file.
    internal Action<long>? AfterChunkWritten { get; set; }

    public async Task<VerifiedUpdatePackage?> FindVerifiedAsync(
        UpdateOffer offer,
        string rid,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var path = _store.PackagePath(offer.Version, rid);
        if (!File.Exists(path))
        {
            return null;
        }

        progress?.Report(new UpdateDownloadProgress(offer.Package.Size, offer.Package.Size, Verifying: true));
        long size;
        string hash;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            size = stream.Length;
            if (size != offer.Package.Size)
            {
                return null;
            }

            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (hash != offer.Package.Sha256)
        {
            return null;
        }

        WriteSnapshot(offer);
        return Verified(offer, rid, path);
    }

    public async Task<VerifiedUpdatePackage> DownloadAsync(
        UpdateOffer offer,
        string rid,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(offer);
        var package = offer.Package;
        if (package.Size > MaximumPackageBytes)
        {
            throw new UpdateTransportException(UpdateFailure.ManifestInvalid, "The package is declared larger than 2 GB.");
        }

        if (package.Url.Scheme != Uri.UriSchemeHttps)
        {
            throw new UpdateTransportException(UpdateFailure.ManifestInvalid, "The package address is not https.");
        }

        var final = _store.PackagePath(offer.Version, rid);
        var partial = _store.PartialPath(offer.Version, rid);
        PrepareFolder(offer.Version, partial, package.Size);

        using var idle = new CancellationTokenSource(_idleTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, idle.Token);
        var completed = false;
        try
        {
            using (var response = await Send(package.Url, linked.Token, idle))
            {
                if (response.Content.Headers.ContentLength is { } length && length != package.Size)
                {
                    throw new UpdateTransportException(UpdateFailure.SizeMismatch, $"The server sends {length} bytes; the manifest says {package.Size}.");
                }

                await using var body = await response.Content.ReadAsStreamAsync(linked.Token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var received = await Copy(body, partial, hash, package.Size, progress, linked.Token, idle);

                if (received != package.Size)
                {
                    throw new UpdateTransportException(UpdateFailure.SizeMismatch, $"{received} bytes came; the manifest says {package.Size}.");
                }

                progress?.Report(new UpdateDownloadProgress(received, package.Size, Verifying: true));
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (actual != package.Sha256)
                {
                    throw new UpdateTransportException(UpdateFailure.HashMismatch, $"The package's SHA-256 is {actual}, not {package.Sha256}.");
                }
            }

            Storage(() => File.Move(partial, final, overwrite: true));
            completed = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (idle.IsCancellationRequested)
        {
            throw new UpdateTransportException(UpdateFailure.Timeout, "The download stopped receiving data.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateTransportException(UpdateFailure.DownloadInterrupted, exception.Message, exception);
        }
        finally
        {
            if (!completed)
            {
                TryDelete(partial);
            }
        }

        WriteSnapshot(offer);
        return Verified(offer, rid, final);
    }

    private async Task<HttpResponseMessage> Send(Uri url, CancellationToken token, CancellationTokenSource idle)
    {
        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
        }
        catch (HttpRequestException exception)
        {
            throw new UpdateTransportException(UpdateFailure.NetworkUnavailable, exception.Message, exception);
        }

        idle.CancelAfter(_idleTimeout);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            response.Dispose();
            throw new UpdateTransportException(UpdateFailure.PackageNotFound, "HTTP 404");
        }

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new UpdateTransportException(UpdateFailure.HttpError, $"HTTP {status}");
        }

        if (!UpdateHttp.EndedHttps(response))
        {
            response.Dispose();
            throw new UpdateTransportException(UpdateFailure.HttpError, "The package was redirected away from https.");
        }

        return response;
    }

    // The response body into the .partial file, hashed on the way; the number of bytes written. Network failures while
    // reading are DownloadInterrupted, failures to write are StorageFailed; more bytes than expected stop it at once.
    private async Task<long> Copy(
        Stream body,
        string partial,
        IncrementalHash hash,
        long expected,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken token,
        CancellationTokenSource idle)
    {
        await using var file = Storage(() => new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true));
        var buffer = new byte[BufferSize];
        long received = 0;
        var lastPercent = -1;
        while (true)
        {
            int read;
            try
            {
                read = await body.ReadAsync(buffer, token);
            }
            catch (IOException exception)
            {
                throw new UpdateTransportException(UpdateFailure.DownloadInterrupted, exception.Message, exception);
            }

            if (read == 0)
            {
                break;
            }

            idle.CancelAfter(_idleTimeout);
            received += read;
            if (received > expected)
            {
                throw new UpdateTransportException(UpdateFailure.SizeMismatch, $"More than the manifest's {expected} bytes came.");
            }

            hash.AppendData(buffer, 0, read);
            try
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new UpdateTransportException(UpdateFailure.StorageFailed, exception.Message, exception);
            }

            AfterChunkWritten?.Invoke(received);
            var percent = (int)(received * 100 / expected);
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress?.Report(new UpdateDownloadProgress(received, expected));
            }
        }

        try
        {
            await file.FlushAsync(token);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdateTransportException(UpdateFailure.StorageFailed, exception.Message, exception);
        }

        return received;
    }

    // The version's folder, without an old .partial; an early word when the disk has clearly too little room (an actual
    // failure to write is still StorageFailed).
    private void PrepareFolder(ReleaseVersion version, string partial, long size)
    {
        Storage(() =>
        {
            Directory.CreateDirectory(_store.Folder(version));
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            return true;
        });

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(_store.Root)!);
            if (drive.IsReady && drive.AvailableFreeSpace < size)
            {
                throw new UpdateTransportException(UpdateFailure.StorageFailed, "There is not enough free disk space for the package.");
            }
        }
        catch (ArgumentException)
        {
            // A path without a drive (a network share): nothing to check up front.
        }
    }

    // The manifest the package was verified against, beside it - replaced whole, never left half written. Not proof of
    // the package by itself: whoever uses the package verifies it again.
    private void WriteSnapshot(UpdateOffer offer)
    {
        var path = _store.SnapshotPath(offer.Version);
        var temporary = path + ".tmp";
        Storage(() =>
        {
            File.WriteAllBytes(temporary, offer.Manifest.ToJson());
            File.Move(temporary, path, overwrite: true);
            return true;
        });
        TryDelete(temporary);
    }

    private static VerifiedUpdatePackage Verified(UpdateOffer offer, string rid, string path) =>
        new(offer.Version, rid, path, offer.Package.Size, offer.Package.Sha256, offer.ReleaseNotesUrl);

    private static T Storage<T>(Func<T> step)
    {
        try
        {
            return step();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UpdateTransportException(UpdateFailure.StorageFailed, exception.Message, exception);
        }
    }

    private static void Storage(Action step) => Storage(() =>
    {
        step();
        return true;
    });

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind, it is never mistaken for a package: it does not have the package's name.
        }
    }
}
