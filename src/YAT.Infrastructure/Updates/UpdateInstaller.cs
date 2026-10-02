using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YAT.Application.Distribution;
using YAT.Application.Updates;

namespace YAT.Infrastructure.Updates;

// The YAT an update is installed for: the folder it runs from, its executable, its process (id and start time, so the
// updater can tell it from any other) and its version.
public sealed record UpdateInstallTarget(string Installation, string Executable, int ProcessId, long ProcessStartUtcTicks, string InstalledVersion)
{
    // This process.
    public static UpdateInstallTarget Current(string installedVersion)
    {
        using var process = Process.GetCurrentProcess();
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The running executable is not known.");
        return new UpdateInstallTarget(Path.GetDirectoryName(executable)!, executable, process.Id, process.StartTime.ToUniversalTime().Ticks, installedVersion);
    }
}

// Prepares the installation of a verified package for the running YAT (Task #051.C), before YAT closes - so that
// everything that can be known to fail fails while YAT is still open:
//
//     1. YAT runs from an unpacked release: <folder>\YAT.exe with yat-files.json of this version and win-x64;
//     2. the package is newer than this YAT;
//     3. no other YAT runs from the folder (by the executable each runs, not by name); no updater holds the folder;
//     4. the package is verified again - its manifest snapshot, size, SHA-256 and every release package rule;
//     5. YAT.Updater.exe is taken from it, checked against its inventory as it is written, into
//        <folder>\.yat-update\updater - which also proves the folder can be written;
//     6. there is room for the new release;
//     7. the updater is started (an argument list, no shell) with anonymous pipes, and must say "ready" in time.
//
// The handoff then carries "go" or "cancel". Nothing of the installation is changed here.
public sealed class UpdateInstaller : IUpdateInstaller
{
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(20);

    private readonly UpdateInstallTarget _target;
    private readonly string _updatesRoot;
    private readonly TimeSpan _readyTimeout;

    public UpdateInstaller(UpdateInstallTarget target, string updatesRoot, TimeSpan? readyTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrWhiteSpace(updatesRoot);
        _target = target with { Installation = Path.GetFullPath(target.Installation), Executable = Path.GetFullPath(target.Executable) };
        _updatesRoot = Path.GetFullPath(updatesRoot);
        _readyTimeout = readyTimeout ?? DefaultReadyTimeout;
    }

    public bool IsAvailable =>
        string.Equals(_target.Executable, ReleaseInstallation.Executable(_target.Installation), StringComparison.OrdinalIgnoreCase)
        && ReleaseVersion.TryParse(_target.InstalledVersion, out var installed)
        && TryReadInventory(_target.Installation, out var inventory)
        && inventory!.Version == installed
        && inventory.Rid == ReleaseInstallation.Rid;

    public Task<UpdateInstallPreparation> PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        return Task.Run(() => Prepare(package, cancellationToken), cancellationToken);
    }

    private UpdateInstallPreparation Prepare(VerifiedUpdatePackage package, CancellationToken cancellationToken)
    {
        var installation = _target.Installation;

        // 1-2. An unpacked release, older than the package.
        if (!IsAvailable || !ReleaseVersion.TryParse(_target.InstalledVersion, out var installed))
        {
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.NotAReleaseInstallation, installation);
        }

        if (package.Version <= installed || package.Rid != ReleaseInstallation.Rid)
        {
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, $"{package.Version} {package.Rid} is not newer than {installed}.");
        }

        // 3. Alone.
        if (OthersRunning() > 0)
        {
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.OtherYatRunning);
        }

        if (LockHeld(installation))
        {
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.UpdateInProgress);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4-6. The package again, and the updater out of it.
        var updater = ReleaseInstallation.UpdaterPath(installation);
        var failure = PlaceUpdater(package.Version, installation, updater);
        if (failure is not null)
        {
            return failure;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 7. Started, and ready.
        return Start(updater, installation, package.Version, installed);
    }

    private UpdateInstallPreparation? PlaceUpdater(ReleaseVersion version, string installation, string updater)
    {
        var snapshot = UpdatePackageLayout.SnapshotPath(_updatesRoot, version);
        ReleasePackage? listed;
        try
        {
            var read = ReleaseManifestReader.Read(File.ReadAllText(snapshot, Encoding.UTF8));
            listed = read.IsValid && read.Manifest!.Version == version ? read.Manifest.PackageFor(ReleaseInstallation.Rid) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, exception.Message);
        }

        if (listed is null)
        {
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, $"{snapshot} is not the manifest of {version}.");
        }

        try
        {
            using var stream = new FileStream(UpdatePackageLayout.PackagePath(_updatesRoot, version, ReleaseInstallation.Rid), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
            if (stream.Length != listed.Size || Convert.ToHexStringLower(SHA256.HashData(stream)) != listed.Sha256)
            {
                return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, "The package is not the one its manifest names.");
            }

            stream.Position = 0;
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
            var contents = ReleasePackageVerifier.Read(archive, version, ReleaseInstallation.Rid);
            if (!contents.IsValid)
            {
                return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, string.Join(" ", contents.Problems.Take(3)));
            }

            if (contents.Files.FirstOrDefault(file => file.File.Path == ReleaseInstallation.UpdaterName) is not { } entry)
            {
                return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, $"The package has no {ReleaseInstallation.UpdaterName}.");
            }

            try
            {
                Directory.CreateDirectory(ReleaseInstallation.UpdaterFolder(installation));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return UpdateInstallPreparation.Failed(UpdateInstallFailure.InstallationNotWritable, exception.Message);
            }

            if (!HasRoom(installation, contents.Inventory!.Files.Sum(file => file.Size) + entry.File.Size))
            {
                return UpdateInstallPreparation.Failed(UpdateInstallFailure.InsufficientSpace);
            }

            var partial = updater + ".partial";
            try
            {
                File.Delete(partial);
                using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if (!ReleasePackageVerifier.TryCopy(entry, output))
                    {
                        return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, ReleasePackageVerifier.NotTheListedFile(entry.File.Path));
                    }
                }

                File.Move(partial, updater, overwrite: true);
            }
            catch (UnauthorizedAccessException exception)
            {
                return UpdateInstallPreparation.Failed(UpdateInstallFailure.InstallationNotWritable, exception.Message);
            }
            catch (IOException exception)
            {
                // A running updater's file cannot be replaced; anything else is the folder refusing.
                return UpdateInstallPreparation.Failed(
                    File.Exists(updater) && LockHeld(installation) ? UpdateInstallFailure.UpdateInProgress : UpdateInstallFailure.InstallationNotWritable,
                    exception.Message);
            }
            finally
            {
                TryDelete(partial);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.PackageInvalid, exception.Message);
        }

        return null;
    }

    private UpdateInstallPreparation Start(string updater, string installation, ReleaseVersion version, ReleaseVersion installed)
    {
        var toUpdater = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var fromUpdater = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        Process? process = null;
        try
        {
            var start = new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(updater)!
            };
            string[] arguments =
            [
                UpdateInstallProtocol.InstallCommand,
                UpdateInstallProtocol.ProtocolOption, UpdateInstallProtocol.Version.ToString(CultureInfo.InvariantCulture),
                UpdateInstallProtocol.InstallDirectoryOption, installation,
                UpdateInstallProtocol.UpdatesRootOption, _updatesRoot,
                UpdateInstallProtocol.VersionOption, version.ToString(),
                UpdateInstallProtocol.FromVersionOption, installed.ToString(),
                UpdateInstallProtocol.ProcessIdOption, _target.ProcessId.ToString(CultureInfo.InvariantCulture),
                UpdateInstallProtocol.ProcessStartOption, _target.ProcessStartUtcTicks.ToString(CultureInfo.InvariantCulture),
                UpdateInstallProtocol.PipeInOption, toUpdater.GetClientHandleAsString(),
                UpdateInstallProtocol.PipeOutOption, fromUpdater.GetClientHandleAsString()
            ];
            foreach (var argument in arguments)
            {
                start.ArgumentList.Add(argument);
            }

            process = Process.Start(start) ?? throw new InvalidOperationException("The updater did not start.");
            toUpdater.DisposeLocalCopyOfClientHandle();
            fromUpdater.DisposeLocalCopyOfClientHandle();

            var reader = new StreamReader(fromUpdater);
            var line = Task.Run(reader.ReadLine);
            if (!line.Wait(_readyTimeout) || line.Result is not { } said)
            {
                throw new InvalidOperationException(line.IsCompleted ? "The updater stopped before it was ready." : "The updater did not say it was ready in time.");
            }

            if (said != UpdateInstallProtocol.Ready)
            {
                var reason = said.StartsWith(UpdateInstallProtocol.ErrorPrefix, StringComparison.Ordinal) ? said[UpdateInstallProtocol.ErrorPrefix.Length..] : said;
                throw new InvalidOperationException(reason);
            }

            return new UpdateInstallPreparation(new Handoff(process, toUpdater, fromUpdater, reader), null, null);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            Stop(process);
            toUpdater.Dispose();
            fromUpdater.Dispose();
            return UpdateInstallPreparation.Failed(UpdateInstallFailure.UpdaterNotReady, exception.Message);
        }
    }

    // YATs other than this one running this executable; one that cannot be looked at counts.
    private int OthersRunning()
    {
        var count = 0;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ReleaseInstallation.ExecutableName)))
        {
            using (process)
            {
                if (process.Id == _target.ProcessId)
                {
                    continue;
                }

                try
                {
                    if (!process.HasExited && (process.MainModule?.FileName is not { } path
                        || string.Equals(Path.GetFullPath(path), _target.Executable, StringComparison.OrdinalIgnoreCase)))
                    {
                        count++;
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    count++;
                }
            }
        }

        return count;
    }

    // Whether an updater holds the installation's lock (which it opens unshared and deletes when it closes).
    private static bool LockHeld(string installation)
    {
        var path = ReleaseInstallation.LockPath(installation);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool TryReadInventory(string installation, out ReleaseInventory? inventory)
    {
        inventory = null;
        try
        {
            var path = ReleaseInstallation.InventoryPath(installation);
            return File.Exists(path)
                && new FileInfo(path).Length <= ReleasePackageVerifier.MaximumInventoryBytes
                && ReleaseInventory.TryRead(File.ReadAllText(path, Encoding.UTF8), out inventory, out _);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasRoom(string installation, long bytes)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(installation)!);
            return !drive.IsReady || drive.AvailableFreeSpace >= bytes + (64L * 1024 * 1024);
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void Stop(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
        }

        process.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    // The updater, ready: "go" or "cancel" down the pipe.
    private sealed class Handoff(Process process, AnonymousPipeServerStream toUpdater, AnonymousPipeServerStream fromUpdater, StreamReader reader) : IUpdateHandoff
    {
        private bool _answered;

        public bool Go() => Answer(UpdateInstallProtocol.Go);

        public void Cancel() => Answer(UpdateInstallProtocol.Cancel);

        public void Dispose()
        {
            reader.Dispose();
            toUpdater.Dispose();
            fromUpdater.Dispose();
            process.Dispose();
        }

        private bool Answer(string line)
        {
            if (_answered)
            {
                return false;
            }

            _answered = true;
            try
            {
                if (process.HasExited)
                {
                    return false;
                }

                var bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                toUpdater.Write(bytes);
                toUpdater.Flush();
                return !process.HasExited;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}

// <installation>\.yat-update\updated.json (Task #051.C): written by the updater when it has installed a version, taken
// - read and deleted - by that version of YAT when it starts, so it says "YAT has been updated" exactly once. Taking it
// also removes the updater it was installed with, which has exited by then (best effort).
public static class UpdateInstallNotice
{
    // The version just installed, when it is this YAT's and the notice could be removed (so it is never shown twice).
    public static string? Take(string installation, string runningVersion)
    {
        var path = ReleaseInstallation.SuccessMarkerPath(installation);
        if (!File.Exists(path) || !ReleaseVersion.TryParse(runningVersion, out var running))
        {
            return null;
        }

        string? version = null;
        try
        {
            using (var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
            {
                if (document.RootElement.TryGetProperty("version", out var element) && element.ValueKind == JsonValueKind.String
                    && ReleaseVersion.TryParse(element.GetString(), out var installed) && installed == running)
                {
                    version = installed.ToString();
                }
            }

            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }

        try
        {
            Directory.Delete(ReleaseInstallation.UpdaterFolder(installation), recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Still in use, or already gone; the next preparation replaces it.
        }

        return version;
    }
}
