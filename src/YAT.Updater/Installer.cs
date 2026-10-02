using YAT.Application.Distribution;

namespace YAT.Updater;

internal enum InstallOutcome
{
    // The new release is installed (whether or not it could be started).
    Installed,

    // Nothing was changed.
    Unchanged,

    // The installation failed and the old release was restored.
    RolledBack,

    // The installation failed and the old release could not be restored: everything is kept for recovery.
    RollbackFailed
}

// What the updater needs of Windows, so the installation can be tested without message boxes or new processes.
internal interface IUpdaterHost
{
    // Tells the user (a message box).
    void Notify(string message, bool error);

    // Starts YAT; false when it could not be started.
    bool Start(string executable, string workingDirectory);

    // The file version of an executable, or null.
    string? FileVersionOf(string executable);

    // The processes, other than the one YAT asked about, that run this executable.
    IReadOnlyList<int> OthersRunning(string executable);
}

// Installs a downloaded release into an installation whose YAT has exited (Task #051.C), under the install lock:
//
//     an interrupted earlier installation is recovered first;
//     no YAT may still run from the installation;
//     the installed release is the version YAT said, and the new one is newer (no downgrade);
//     the package is verified again and extracted to staging (StagedRelease);
//     the move plan is made - an ownership collision stops here (InstallPlan);
//     the files are replaced, validated and committed - or rolled back (Replacement).
//
// Then YAT is started again: the new one, checked to be the new version first; the old one when nothing was installed
// and it is whole. The user is told what happened whenever it is not simply the new YAT starting.
internal sealed class Installer
{
    private readonly string _installation;
    private readonly string? _updatesRoot;
    private readonly IUpdaterHost _host;
    private readonly UpdaterLog _log;
    private readonly FileMover _mover;

    // updatesRoot: where the package is (UpdatePackageLayout); not needed to recover.
    public Installer(string installation, string? updatesRoot, IUpdaterHost host, UpdaterLog log, FileMover? mover = null)
    {
        _installation = Path.GetFullPath(installation);
        _updatesRoot = updatesRoot is null ? null : Path.GetFullPath(updatesRoot);
        _host = host;
        _log = log;
        _mover = mover ?? new FileMover();
    }

    // Test seams (see Replacement and StagedRelease).
    internal Action<int, PlannedMove>? BeforeMove { get; init; }

    internal Action<int, PlannedMove>? BeforeUndo { get; init; }

    internal Action<string>? AfterFileExtracted { get; init; }

    internal Action? AfterCommit { get; init; }

    private string Executable => ReleaseInstallation.Executable(_installation);

    public InstallOutcome Install(ReleaseVersion from, ReleaseVersion to)
    {
        _log.Write($"Installing {to} over {from} in {_installation}.");
        var replacement = NewReplacement();
        var started = DateTime.UtcNow;
        try
        {
            if (new DirectoryInfo(ReleaseInstallation.WorkFolder(_installation)).LinkTarget is not null)
            {
                throw new UpdaterException($"{ReleaseInstallation.WorkFolder(_installation)} is a link.");
            }

            switch (replacement.Recover())
            {
                case RecoveryResult.Failed:
                    return Tell(InstallOutcome.RollbackFailed, to, from, "An earlier update was interrupted and could not be undone.");
                case RecoveryResult.RolledBack or RecoveryResult.Completed:
                    _log.Write("An earlier interrupted update was recovered.");
                    break;
            }

            if (_host.OthersRunning(Executable).Count > 0)
            {
                _log.Write("YAT is still running from this folder.");
                _host.Notify($"YAT could not be updated to {to}: YAT is still running from {_installation}.\n\nClose every YAT window, then install the update again.", error: true);
                return InstallOutcome.Unchanged;
            }

            var installed = InstalledRelease.Read(_installation, from);
            if (to <= from)
            {
                throw new UpdaterException($"{to} is not newer than the installed {from}.");
            }

            DeleteLeftovers();
            var staged = StagedRelease.Prepare(_installation, _updatesRoot ?? throw new InvalidOperationException("No updates folder was given."), to, _log, AfterFileExtracted);
            _log.Write($"Timing: staging {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms.");
            InstallPlan plan;
            try
            {
                plan = InstallPlan.Create(installed, staged, _log);
            }
            catch
            {
                StagedRelease.Remove(staged.Folder);
                throw;
            }

            var replacing = DateTime.UtcNow;
            replacement.Install(plan, staged);
            _log.Write($"Timing: replacement {(DateTime.UtcNow - replacing).TotalMilliseconds:F0} ms; total {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms.");
        }
        catch (UpdaterException exception)
        {
            _log.Write($"Not installed: {exception.Message}");
            return Tell(InstallOutcome.Unchanged, to, from, exception.Message);
        }
        catch (InstallFailedException exception)
        {
            return Tell(exception.Restored ? InstallOutcome.RolledBack : InstallOutcome.RollbackFailed, to, from, exception.Message);
        }

        // Installed. The new YAT starts only once it is checked to be the new version.
        var version = _host.FileVersionOf(Executable);
        if (version == to.FileVersion && _host.Start(Executable, _installation))
        {
            _log.Write($"Started YAT {to}.");
        }
        else
        {
            _log.Write($"YAT {to} was installed but could not be started (version {version ?? "unknown"}).");
            _host.Notify($"YAT has been updated to {to}, but it could not be started.\n\nStart {Executable} to use it.", error: false);
        }

        return InstallOutcome.Installed;
    }

    // `YAT.Updater recover`: undoes an interrupted installation, or finishes a committed one.
    public InstallOutcome Recover()
    {
        var replacement = NewReplacement();
        RecoveryResult result;
        try
        {
            if (_host.OthersRunning(Executable).Count > 0)
            {
                _host.Notify($"YAT is running from {_installation}. Close every YAT window, then run the recovery again.", error: true);
                return InstallOutcome.Unchanged;
            }

            result = replacement.Recover();
        }
        catch (UpdaterException exception)
        {
            _log.Write($"Recovery failed: {exception.Message}");
            _host.Notify($"YAT's update could not be recovered.\n\n{exception.Message}\n\nNothing has been deleted. Details: {ReleaseInstallation.LogPath(_installation)}", error: true);
            return InstallOutcome.RollbackFailed;
        }

        switch (result)
        {
            case RecoveryResult.Nothing:
                _host.Notify("There is no interrupted YAT update to recover.", error: false);
                return InstallOutcome.Unchanged;
            case RecoveryResult.RolledBack:
                _host.Notify("The interrupted YAT update was undone. You can start YAT again.", error: false);
                return InstallOutcome.RolledBack;
            case RecoveryResult.Completed:
                _host.Notify("The YAT update was completed. You can start YAT again.", error: false);
                return InstallOutcome.Installed;
            default:
                _host.Notify(RecoveryInstructions("YAT's interrupted update could not be undone."), error: true);
                return InstallOutcome.RollbackFailed;
        }
    }

    private Replacement NewReplacement() =>
        new(_installation, _log, _mover, _host.FileVersionOf) { BeforeMove = BeforeMove, BeforeUndo = BeforeUndo, AfterCommit = AfterCommit };

    // Backup and staging folders that no journal speaks for: left by an attempt that finished its work but not its
    // cleanup. Only the updater's own folders.
    private void DeleteLeftovers()
    {
        foreach (var folder in new[] { ReleaseInstallation.BackupFolder(_installation), Path.Combine(ReleaseInstallation.WorkFolder(_installation), "staging") })
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new UpdaterException($"The leftovers of an earlier update ({folder}) cannot be removed: {exception.Message}", exception);
            }
        }
    }

    private InstallOutcome Tell(InstallOutcome outcome, ReleaseVersion to, ReleaseVersion from, string reason)
    {
        switch (outcome)
        {
            case InstallOutcome.Unchanged or InstallOutcome.RolledBack:
                // The old YAT is whole: it starts again, and the user hears why it is still the old one.
                var restarted = File.Exists(Executable) && _host.Start(Executable, _installation);
                _log.Write(restarted ? $"Started YAT {from} again." : "The old YAT could not be started.");
                _host.Notify(
                    $"YAT could not be updated to {to}.\n\n{reason}\n\n"
                    + (outcome == InstallOutcome.RolledBack ? $"YAT {from} has been restored." : $"YAT {from} has not been changed."),
                    error: true);
                break;
            default:
                _log.Write($"Rollback failed: {reason}");
                _host.Notify(RecoveryInstructions($"YAT could not be updated to {to}, and YAT {from} could not be restored automatically.\n\n{reason}"), error: true);
                break;
        }

        return outcome;
    }

    private string RecoveryInstructions(string what) =>
        $"{what}\n\nNothing has been deleted: the files are kept in {ReleaseInstallation.WorkFolder(_installation)}. "
        + "Close every program that may use YAT's folder, then run:\n\n"
        + $"\"{ReleaseInstallation.UpdaterPath(_installation)}\" recover --protocol 1 --install-dir \"{_installation}\"\n\n"
        + $"Details: {ReleaseInstallation.LogPath(_installation)}";
}
