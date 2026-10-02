namespace YAT.Application.Updates;

// Why a verified update cannot be installed now (Task #051.C). YAT stays open whenever one of these is the answer.
public enum UpdateInstallFailure
{
    // This YAT does not run from an unpacked release (no yat-files.json of its version beside YAT.exe).
    NotAReleaseInstallation,

    // Another YAT runs from the same folder.
    OtherYatRunning,

    // YAT's folder cannot be written (for example under Program Files, or read-only).
    InstallationNotWritable,

    InsufficientSpace,

    // The downloaded package, or its manifest, is not the verified release any more.
    PackageInvalid,

    // An update of this YAT is already being installed.
    UpdateInProgress,

    // The updater could not be started, or did not say it was ready.
    UpdaterNotReady
}

// The updater, started and ready (Task #051.C): it holds the installation's lock and waits. Go once YAT is closing - it
// installs when YAT has exited; Cancel when YAT is not closing after all - it changes nothing. Disposing without either
// tells it YAT went away: it changes nothing either.
public interface IUpdateHandoff : IDisposable
{
    // False when the updater is no longer there to hear it.
    bool Go();

    void Cancel();
}

public sealed record UpdateInstallPreparation(IUpdateHandoff? Handoff, UpdateInstallFailure? Failure, string? Detail)
{
    public bool Succeeded => Handoff is not null;

    public static UpdateInstallPreparation Failed(UpdateInstallFailure failure, string? detail = null) => new(null, failure, detail);
}

// Prepares the installation of a verified package (Task #051.C): checks that this YAT can be updated in place, verifies
// the package again, puts the updater from it next to the installation and starts it - and returns once the updater is
// ready, or why it is not. Nothing of the installation changes here. Never throws for a failure; cancellable.
public interface IUpdateInstaller
{
    // Whether this YAT can install updates at all: it runs from an unpacked release (a quick check; PrepareAsync checks
    // everything again).
    bool IsAvailable { get; }

    Task<UpdateInstallPreparation> PrepareAsync(VerifiedUpdatePackage package, CancellationToken cancellationToken);
}
