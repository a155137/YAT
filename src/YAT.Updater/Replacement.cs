using System.Text.Json;
using YAT.Application.Distribution;

namespace YAT.Updater;

// The installation failed; whether the old release was restored.
internal sealed class InstallFailedException(string message, bool restored, Exception? inner = null) : Exception(message, inner)
{
    public bool Restored { get; } = restored;
}

// Test seam: a fault that stands for the updater dying where it is thrown - nothing is rolled back, nothing cleaned up.
internal sealed class SimulatedCrashException() : Exception("Simulated crash.");

internal enum RecoveryResult
{
    // No installation was interrupted.
    Nothing,

    // An interrupted installation was undone: the old release is back.
    RolledBack,

    // An installation that had been committed was cleaned up: the new release is in place.
    Completed,

    // Undoing failed; everything is kept for another try.
    Failed
}

// Replaces the installed release with the staged one by renames on one volume (Task #051.C, strategy C):
//
//     1. the journal is written;
//     2. the old release's changed and removed files are moved into .yat-update\backup (YAT.exe first);
//     3. the new release's files are moved from the staging folder into the installation (YAT.exe last);
//     4. the installation is validated: every new file there with its size, the new yat-files.json, YAT.exe of the new
//        version;
//     5. the journal says Committed; YAT is told once (updated.json); backup, staging and journal are removed.
//
// Any failure in 2-4 rolls back at once: the moves made are undone, newest first, and the folders created removed when
// empty. A rollback that cannot finish keeps the journal (RollbackFailed), the backup and the staging folder - nothing is
// deleted - and Recover tries again. Recover also finishes the cleanup of a committed installation. Both are idempotent.
internal sealed class Replacement
{
    private readonly string _installation;
    private readonly UpdaterLog _log;
    private readonly FileMover _mover;
    private readonly Func<string, string?> _fileVersionOf;

    public Replacement(string installation, UpdaterLog log, FileMover mover, Func<string, string?> fileVersionOf)
    {
        _installation = Path.GetFullPath(installation);
        _log = log;
        _mover = mover;
        _fileVersionOf = fileVersionOf;
    }

    // Test seams: called before each move of an installation, and before each undo of a rollback.
    internal Action<int, PlannedMove>? BeforeMove { get; init; }

    internal Action<int, PlannedMove>? BeforeUndo { get; init; }

    // Test seam: called once the installation is committed, before its cleanup.
    internal Action? AfterCommit { get; init; }

    private string JournalPath => ReleaseInstallation.JournalPath(_installation);

    private string BackupFolder => ReleaseInstallation.BackupFolder(_installation);

    // Throws InstallFailedException when the installation failed (Restored: the old release is back).
    public void Install(InstallPlan plan, StagedRelease staged)
    {
        var journal = InstallJournal.For(plan);
        try
        {
            journal.Write(JournalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StagedRelease.Remove(staged.Folder);
            throw new InstallFailedException($"The installation journal cannot be written: {exception.Message}", restored: true, exception);
        }

        _log.Write($"Journal written: {journal.Moves.Count} moves.");
        try
        {
            for (var index = 0; index < journal.Moves.Count; index++)
            {
                var move = journal.Moves[index];
                BeforeMove?.Invoke(index, move);
                if (move.Kind == MoveKind.Backup)
                {
                    _mover.Move(Installed(move.Path), Backup(move.Path));
                }
                else
                {
                    _mover.Move(Staged(journal, move.Path), Installed(move.Path));
                }
            }

            Validate(staged);
        }
        catch (Exception exception) when (exception is not SimulatedCrashException)
        {
            _log.Write($"Installation failed: {exception.Message}. Rolling back.");
            var restored = Rollback(journal);
            throw new InstallFailedException(exception is UpdaterException ? exception.Message : $"Replacing YAT's files failed: {exception.Message}", restored, exception);
        }

        journal = journal.With(JournalState.Committed);
        try
        {
            journal.Write(JournalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Not recorded as committed: undone, like any other failure before it.
            _log.Write($"The journal cannot be committed: {exception.Message}. Rolling back.");
            var restored = Rollback(journal.With(JournalState.Replacing));
            throw new InstallFailedException($"The installation could not be recorded: {exception.Message}", restored, exception);
        }

        _log.Write($"Committed {journal.To}.");
        AfterCommit?.Invoke();
        Finish(journal);
    }

    // Undoes an interrupted installation, or finishes the cleanup of a committed one.
    public RecoveryResult Recover()
    {
        if (!File.Exists(JournalPath))
        {
            return RecoveryResult.Nothing;
        }

        var journal = InstallJournal.Read(JournalPath);
        _log.Write($"Recovering an installation of {journal.To} over {journal.From} ({journal.State}).");
        if (journal.State == JournalState.Committed)
        {
            Finish(journal);
            return RecoveryResult.Completed;
        }

        return Rollback(journal) ? RecoveryResult.RolledBack : RecoveryResult.Failed;
    }

    // The moves made, undone newest first - read from the files, so undoing twice is undoing once. True when the old
    // release is back and nothing is left of the attempt but the log.
    private bool Rollback(InstallJournal journal)
    {
        var failures = new List<string>();
        for (var index = journal.Moves.Count - 1; index >= 0; index--)
        {
            var move = journal.Moves[index];
            try
            {
                BeforeUndo?.Invoke(index, move);
                if (move.Kind == MoveKind.Place)
                {
                    var installed = Installed(move.Path);
                    var staged = Staged(journal, move.Path);
                    if (!File.Exists(staged) && File.Exists(installed)
                        && InstallPlan.IsFile(installed, new ReleaseFile(move.Path, move.Size, move.Sha256)))
                    {
                        _mover.Move(installed, staged);
                    }
                }
                else
                {
                    var backup = Backup(move.Path);
                    if (File.Exists(backup))
                    {
                        _mover.Move(backup, Installed(move.Path));
                    }
                }
            }
            catch (Exception exception) when (exception is not SimulatedCrashException)
            {
                failures.Add($"{move.Kind} {move.Path}: {exception.Message}");
            }
        }

        foreach (var directory in journal.CreatedDirectories)
        {
            DeleteIfEmpty(ReleaseInstallation.PathOf(_installation, directory));
        }

        if (failures.Count > 0)
        {
            _log.Write($"Rollback failed ({failures.Count}): {string.Join("; ", failures)}");
            TryWrite(journal.With(JournalState.RollbackFailed));
            return false;
        }

        // The old release is back. The staging folder and the (now empty) backup go; the journal last.
        StagedRelease.Remove(ReleaseInstallation.StagingFolder(_installation, journal.To));
        if (Directory.Exists(BackupFolder) && Directory.EnumerateFiles(BackupFolder, "*", SearchOption.AllDirectories).Any())
        {
            _log.Write("Rollback left files in the backup folder; they are kept.");
            TryWrite(journal.With(JournalState.RollbackFailed));
            return false;
        }

        try
        {
            DeleteTree(BackupFolder);
            File.Delete(JournalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The old release is back; what is left is cleaned up by the next recovery (which finds nothing to undo).
            _log.Write($"Cleanup after the rollback did not finish: {exception.Message}");
        }

        _log.Write($"Rolled back: {journal.From} restored.");
        return true;
    }

    // The new release is in place: YAT is told once, and backup, staging and journal go (the journal last, so an
    // interrupted cleanup is finished by Recover). Folders the old release's removed files leave empty go too.
    private void Finish(InstallJournal journal)
    {
        try
        {
            var marker = ReleaseInstallation.SuccessMarkerPath(_installation);
            if (!File.Exists(marker))
            {
                WriteMarker(marker, journal.To);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Write($"The update notice could not be written: {exception.Message}");
        }

        try
        {
            DeleteTree(BackupFolder);
            StagedRelease.Remove(ReleaseInstallation.StagingFolder(_installation, journal.To));
            foreach (var move in journal.Moves.Where(move => move.Kind == MoveKind.Backup))
            {
                var parts = move.Path.Split('/');
                for (var depth = parts.Length - 1; depth >= 1; depth--)
                {
                    DeleteIfEmpty(ReleaseInstallation.PathOf(_installation, string.Join('/', parts.Take(depth))));
                }
            }

            File.Delete(JournalPath);
            _log.Write("Cleanup finished.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Write($"Cleanup did not finish ({exception.Message}); recovery will finish it.");
        }
    }

    // The new release, as the installation now holds it.
    private void Validate(StagedRelease staged)
    {
        foreach (var file in staged.Inventory.Files)
        {
            var path = Installed(file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Size)
            {
                throw new UpdaterException($"After replacing, {file.Path} is not the new release's file.");
            }
        }

        if (!File.ReadAllBytes(ReleaseInstallation.InventoryPath(_installation)).AsSpan().SequenceEqual(staged.InventoryJson))
        {
            throw new UpdaterException($"After replacing, {ReleaseInventory.FileName} is not the new release's.");
        }

        var version = _fileVersionOf(ReleaseInstallation.Executable(_installation));
        if (version != staged.Version.FileVersion)
        {
            throw new UpdaterException($"After replacing, {ReleaseInstallation.ExecutableName} is version {version ?? "unknown"}, not {staged.Version.FileVersion}.");
        }
    }

    public static void WriteMarker(string path, ReleaseVersion version)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("version", version.ToString());
            writer.WriteEndObject();
        }

        File.WriteAllBytes(path, buffer.ToArray());
    }

    private string Installed(string releasePath) => StagedRelease.Inside(_installation, releasePath);

    private string Backup(string releasePath) => StagedRelease.Inside(BackupFolder, releasePath);

    private string Staged(InstallJournal journal, string releasePath) =>
        StagedRelease.Inside(ReleaseInstallation.StagingFolder(_installation, journal.To), releasePath);

    private void TryWrite(InstallJournal journal)
    {
        try
        {
            journal.Write(JournalPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _log.Write($"The journal could not be updated: {exception.Message}");
        }
    }

    private static void DeleteIfEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteTree(string folder)
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
