using YAT.Application.Distribution;

namespace YAT.Updater;

// Why the updater stops; the message is for the user (and the log).
internal sealed class UpdaterException(string message, Exception? inner = null) : Exception(message, inner);

// Held while an updater works on an installation (Task #051.C): <installation>\.yat-update\install.lock, open without
// sharing and deleted when closed - also when the updater dies, since Windows closes its handles. A second updater for
// the same installation cannot get it.
internal sealed class InstallLock : IDisposable
{
    private readonly FileStream _stream;

    private InstallLock(FileStream stream) => _stream = stream;

    // Null when another updater holds it.
    public static InstallLock? TryAcquire(string installation)
    {
        Directory.CreateDirectory(ReleaseInstallation.WorkFolder(installation));
        try
        {
            return new InstallLock(new FileStream(
                ReleaseInstallation.LockPath(installation),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                1,
                FileOptions.DeleteOnClose));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}

// What the updater did, appended to <installation>\.yat-update\update.log (and to `echo`, for tests). Never fails.
internal sealed class UpdaterLog(string? path, Action<string>? echo = null)
{
    public void Write(string message)
    {
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}";
        echo?.Invoke(line);
        if (path is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

// Moves a file within a volume (a rename), retrying a while when another program holds it (an antivirus scanner, the
// search indexer); never replaces a file that is there. Test seams: the attempts, the delay, and a hook before each move.
internal sealed class FileMover
{
    public int Attempts { get; init; } = 10;

    public TimeSpan Delay { get; init; } = TimeSpan.FromMilliseconds(200);

    public void Move(string source, string destination)
    {
        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException($"'{destination}' is already there.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, destination, overwrite: false);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException && attempt < Attempts && File.Exists(source))
            {
                Thread.Sleep(Delay);
            }
        }
    }
}
