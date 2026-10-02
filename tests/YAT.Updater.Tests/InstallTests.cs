using YAT.Application.Distribution;
using static YAT.Updater.Tests.Releases;

namespace YAT.Updater.Tests;

// Installing 0.3.0 over a fake 0.2.0 installation (Task #051.C): the plan, the renames, validation and commit - and every
// way it can fail: refused before anything moves, rolled back after a failure at any move, recovered after a crash at
// any move, a rollback that itself fails keeping all its evidence. The installation is always either the whole old
// release or the whole new one, and the user's own files are never touched.
public sealed class InstallTests : IDisposable
{
    private static readonly Dictionary<string, byte[]> UserFiles = new()
    {
        ["my project.yat"] = Bytes("the user's project, saved next to YAT"),
        ["notes/today.txt"] = Bytes("the user's notes"),
        ["licenses/my-own.txt"] = Bytes("the user's file in a release folder")
    };

    private readonly Sandbox _sandbox = new();
    private readonly FakeHost _host = new();

    public InstallTests()
    {
        Install(_sandbox.Installation, V2);
        foreach (var file in UserFiles)
        {
            var path = _sandbox.InInstallation(file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }

        Package(_sandbox.Updates, V3);
        Original = Snapshot(_sandbox.Installation);
        OriginalFolders = Folders(_sandbox.Installation);
    }

    private SortedDictionary<string, string> Original { get; }

    private SortedSet<string> OriginalFolders { get; }

    private static FileMover QuickMover => new() { Attempts = 3, Delay = TimeSpan.FromMilliseconds(10) };

    private string WorkFolder => ReleaseInstallation.WorkFolder(_sandbox.Installation);

    public void Dispose() => _sandbox.Dispose();

    private Installer Installer(
        Action<int, PlannedMove>? beforeMove = null,
        Action<int, PlannedMove>? beforeUndo = null,
        Action? afterCommit = null,
        FileMover? mover = null) =>
        new(_sandbox.Installation, _sandbox.Updates, _host, _sandbox.Log, mover ?? QuickMover)
        {
            BeforeMove = beforeMove,
            BeforeUndo = beforeUndo,
            AfterCommit = afterCommit
        };

    private InstallOutcome Run(Installer? installer = null) => (installer ?? Installer()).Install(V2, V3);

    private int MoveCount()
    {
        var count = 0;
        Assert.Equal(InstallOutcome.Installed, Run(Installer(beforeMove: (index, _) => count = Math.Max(count, index + 1))));
        return count;
    }

    private void AssertOld()
    {
        Assert.Equal(Original, Snapshot(_sandbox.Installation));
        Assert.Equal(OriginalFolders, Folders(_sandbox.Installation));
    }

    private void AssertNew()
    {
        Assert.Equal(Expected(V3, user: UserFiles), Snapshot(_sandbox.Installation));
    }

    // Nothing of the attempt is left but the log (and the lock, which the session holds).
    private void AssertCleanWorkFolder()
    {
        var left = Directory.Exists(WorkFolder)
            ? Directory.EnumerateFileSystemEntries(WorkFolder).Select(Path.GetFileName).Where(name => name is not ("update.log" or "updated.json")).ToList()
            : [];
        Assert.True(left.Count == 0, "left: " + string.Join(", ", left));
    }

    // ---- Success ----

    [Fact]
    public void TheNewReleaseReplacesTheOldOneAndTheUsersFilesStay()
    {
        var keptBefore = File.GetLastWriteTimeUtc(_sandbox.InInstallation("coreclr.dll"));
        var userBefore = File.GetLastWriteTimeUtc(_sandbox.InInstallation("my project.yat"));

        Assert.Equal(InstallOutcome.Installed, Run());

        AssertNew();
        Assert.False(File.Exists(_sandbox.InInstallation("old-only.dll")), "a file only the old release had is removed");
        Assert.False(Directory.Exists(_sandbox.InInstallation("plugins")), "a folder it leaves empty is removed");
        Assert.True(File.Exists(_sandbox.InInstallation("notes/today.txt")));
        Assert.Equal(keptBefore, File.GetLastWriteTimeUtc(_sandbox.InInstallation("coreclr.dll")));
        Assert.Equal(userBefore, File.GetLastWriteTimeUtc(_sandbox.InInstallation("my project.yat")));
        AssertCleanWorkFolder();
        Assert.Contains("\"version\":\"0.3.0\"", File.ReadAllText(ReleaseInstallation.SuccessMarkerPath(_sandbox.Installation)), StringComparison.Ordinal);
        Assert.Equal([$"exe 0.3.0 in {_sandbox.Installation}"], _host.Started);
        Assert.Empty(_host.Notices);
    }

    [Fact]
    public void ThePlanKeepsIdenticalFilesAndMovesYatExeFirstOutAndLastIn()
    {
        var installed = InstalledRelease.Read(_sandbox.Installation, V2);
        var staged = StagedRelease.Prepare(_sandbox.Installation, _sandbox.Updates, V3, _sandbox.Log);

        var plan = InstallPlan.Create(installed, staged, _sandbox.Log);

        Assert.Equal(["coreclr.dll", "licenses/SkiaSharp/LICENSE.txt"], plan.Kept.Order(StringComparer.Ordinal));
        Assert.Equal(
            [
                "Backup YAT.exe", "Backup README.txt", "Backup YAT.Updater.exe", "Backup YAT.dll", "Backup old-only.dll", "Backup plugins/legacy/old.dll",
                "Backup yat-files.json", "Place README.txt", "Place YAT.Updater.exe", "Place YAT.dll", "Place lang/zh-Hant/YAT.resources.dll",
                "Place new-only.dll", "Place yat-files.json", "Place YAT.exe"
            ],
            plan.Moves.Select(move => $"{move.Kind} {move.Path}"));
        Assert.Equal(["lang/zh-Hant", "lang"], plan.CreatedDirectories);
        AssertOld();
    }

    [Fact]
    public void AnIdenticalFileChangedOnDiskIsReplacedToo()
    {
        File.WriteAllText(_sandbox.InInstallation("coreclr.dll"), "damaged on disk");

        Assert.Equal(InstallOutcome.Installed, Run());

        AssertNew();
    }

    [Fact]
    public void AMissingOldFileIsSimplyPlaced()
    {
        File.Delete(_sandbox.InInstallation("YAT.dll"));
        File.Delete(_sandbox.InInstallation("coreclr.dll"));

        Assert.Equal(InstallOutcome.Installed, Run());

        AssertNew();
    }

    [Fact]
    public async Task ALockedFileReleasedWhileRetryingDoesNotStopTheInstallation()
    {
        var locked = new FileStream(_sandbox.InInstallation("YAT.dll"), FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(150, TestContext.Current.CancellationToken);
            await locked.DisposeAsync();
        }, TestContext.Current.CancellationToken);

        Assert.Equal(InstallOutcome.Installed, Run(Installer(mover: new FileMover { Attempts = 50, Delay = TimeSpan.FromMilliseconds(20) })));

        await release;
        AssertNew();
    }

    [Fact]
    public void YatThatCannotBeStartedIsStillInstalled()
    {
        _host.CanStart = false;

        Assert.Equal(InstallOutcome.Installed, Run());

        AssertNew();
        var notice = Assert.Single(_host.Notices);
        Assert.Contains("has been updated to 0.3.0, but it could not be started", notice.Message, StringComparison.Ordinal);
        Assert.False(notice.Error);
    }

    // ---- Refused before anything moves ----

    private string Unchanged(Installer? installer = null)
    {
        Assert.Equal(InstallOutcome.Unchanged, Run(installer));
        AssertOld();
        AssertCleanWorkFolder();
        return Assert.Single(_host.Notices).Message;
    }

    [Fact]
    public void AUsersFileWhereTheNewReleaseNeedsOneStopsTheUpdate()
    {
        File.WriteAllText(_sandbox.InInstallation("new-only.dll"), "the user's own new-only.dll");
        Original["new-only.dll"] = Sha(File.ReadAllBytes(_sandbox.InInstallation("new-only.dll")));

        var message = Unchanged();

        Assert.Contains("not YAT's own: new-only.dll", message, StringComparison.Ordinal);
        Assert.Contains("YAT 0.2.0 has not been changed", message, StringComparison.Ordinal);
        Assert.Equal([$"exe 0.2.0 in {_sandbox.Installation}"], _host.Started);
    }

    [Fact]
    public void AUsersFolderWhereTheNewReleaseNeedsAFileStopsTheUpdate()
    {
        Directory.CreateDirectory(_sandbox.InInstallation("new-only.dll"));
        OriginalFolders.Add("new-only.dll");

        Assert.Contains("not YAT's own: new-only.dll", Unchanged(), StringComparison.Ordinal);
    }

    [Fact]
    public void AUsersFileWhereTheNewReleaseNeedsAFolderStopsTheUpdate()
    {
        File.WriteAllText(_sandbox.InInstallation("lang"), "the user's file named lang");
        Original["lang"] = Sha(File.ReadAllBytes(_sandbox.InInstallation("lang")));

        Assert.Contains("not YAT's own: lang", Unchanged(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALinkInTheInstallationStopsTheUpdate()
    {
        var elsewhere = Path.Combine(_sandbox.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "keep.txt"), "outside YAT");
        Directory.Delete(_sandbox.InInstallation("licenses/SkiaSharp"), recursive: true);
        Junction(_sandbox.InInstallation("licenses/SkiaSharp"), elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "LICENSE.txt"), "MIT, the same in both");

        Assert.Equal(InstallOutcome.Unchanged, Run());

        Assert.Contains("is a link", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
        Assert.Equal(["LICENSE.txt", "keep.txt"], Directory.EnumerateFiles(elsewhere).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Directory.Delete(_sandbox.InInstallation("licenses/SkiaSharp"));
    }

    [Fact]
    public void AYatStillRunningFromTheFolderStopsTheUpdateWithoutStartingAnother()
    {
        _host.Running.Add(4242);

        Assert.Contains("still running", Unchanged(), StringComparison.Ordinal);
        Assert.Empty(_host.Started);
    }

    [Fact]
    public void AnOlderVersionIsNotInstalled()
    {
        var older = ReleaseVersion.Parse("0.1.0");
        Package(_sandbox.Updates, older);

        Assert.Equal(InstallOutcome.Unchanged, Installer().Install(V2, older));

        Assert.Contains("0.1.0 is not newer than the installed 0.2.0", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
        AssertOld();
    }

    [Fact]
    public void TheSameVersionIsNotInstalledAgain()
    {
        Package(_sandbox.Updates, V2);

        Assert.Equal(InstallOutcome.Unchanged, Installer().Install(V2, V2));

        Assert.Contains("is not newer than the installed 0.2.0", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
        AssertOld();
    }

    [Fact]
    public void AnInstallationOfAnotherVersionThanYatSaidIsNotUpdated()
    {
        Assert.Equal(InstallOutcome.Unchanged, Installer().Install(ReleaseVersion.Parse("0.1.0"), V3));

        Assert.Contains("is 0.2.0 win-x64, not 0.1.0", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
        AssertOld();
    }

    [Fact]
    public void AnInstallationWithoutAnInventoryIsNotUpdated()
    {
        File.Delete(ReleaseInstallation.InventoryPath(_sandbox.Installation));
        Original.Remove(ReleaseInventory.FileName);

        Assert.Contains("cannot be read", Unchanged(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADamagedPackageChangesNothing()
    {
        var zip = UpdatePackageLayoutPath();
        var bytes = File.ReadAllBytes(zip);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        Assert.Contains("does not have the SHA-256", Unchanged(), StringComparison.Ordinal);
    }

    [Fact]
    public void LeftoversOfAnEarlierAttemptAreRemovedFirst()
    {
        Directory.CreateDirectory(Path.Combine(ReleaseInstallation.BackupFolder(_sandbox.Installation), "sub"));
        File.WriteAllText(Path.Combine(ReleaseInstallation.BackupFolder(_sandbox.Installation), "sub", "stale.dll"), "stale");

        Assert.Equal(InstallOutcome.Installed, Run());

        AssertNew();
        AssertCleanWorkFolder();
    }

    // ---- A failure while replacing: rolled back ----

    private void AssertRolledBack()
    {
        AssertOld();
        AssertCleanWorkFolder();
        Assert.Equal([$"exe 0.2.0 in {_sandbox.Installation}"], _host.Started);
        var notice = Assert.Single(_host.Notices);
        Assert.True(notice.Error);
        Assert.Contains("YAT 0.2.0 has been restored", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureAtAnyMoveRollsBackToTheOldRelease()
    {
        var moves = MoveCount();
        Assert.True(moves >= 10);
        for (var failing = 0; failing < moves; failing++)
        {
            using var sandbox = new InstallTests();
            var at = failing;
            Assert.Equal(InstallOutcome.RolledBack, sandbox.Run(sandbox.Installer(beforeMove: (index, _) =>
            {
                if (index == at)
                {
                    throw new IOException($"Simulated failure at move {at}.");
                }
            })));

            sandbox.AssertRolledBack();
        }
    }

    [Fact]
    public void ALockedFileRollsBackAfterRetrying()
    {
        using (new FileStream(_sandbox.InInstallation("YAT.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Equal(InstallOutcome.RolledBack, Run());
        }

        AssertRolledBack();
    }

    [Fact]
    public void AFileThatMayNotBeMovedRollsBack()
    {
        _sandbox.DenyDelete(_sandbox.InInstallation("plugins/legacy/old.dll"));

        Assert.Equal(InstallOutcome.RolledBack, Run());

        AssertRolledBack();
    }

    [Fact]
    public void AnUnwritableBackupRollsBack()
    {
        Directory.CreateDirectory(WorkFolder);
        File.WriteAllText(ReleaseInstallation.BackupFolder(_sandbox.Installation), "a file where the backup folder must be");

        Assert.Equal(InstallOutcome.RolledBack, Run());

        AssertOld();
        Assert.Contains("YAT 0.2.0 has been restored", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewYatThatIsNotTheNewVersionRollsBack()
    {
        _host.Version = path => File.ReadAllText(path) == "exe 0.3.0" ? "0.3.0.1" : null;

        Assert.Equal(InstallOutcome.RolledBack, Run());

        AssertOld();
        Assert.Contains("is version 0.3.0.1, not 0.3.0.0", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
    }

    // ---- A crash while replacing: recovered from the journal ----

    private Installer Recovering(Action<int, PlannedMove>? beforeUndo = null) => Installer(beforeUndo: beforeUndo);

    [Fact]
    public void ACrashAtAnyMoveIsRecoveredToTheOldReleaseAndRecoveringAgainIsSafe()
    {
        var moves = MoveCount();
        for (var crashing = 0; crashing < moves; crashing++)
        {
            using var sandbox = new InstallTests();
            var at = crashing;
            Assert.Throws<SimulatedCrashException>(() => sandbox.Run(sandbox.Installer(beforeMove: (index, _) =>
            {
                if (index == at)
                {
                    throw new SimulatedCrashException();
                }
            })));
            Assert.True(File.Exists(ReleaseInstallation.JournalPath(sandbox._sandbox.Installation)), "the journal survives the crash");
            Assert.Equal(at == 0, File.Exists(sandbox._sandbox.InInstallation("YAT.exe")));

            Assert.Equal(InstallOutcome.RolledBack, sandbox.Recovering().Recover());
            sandbox.AssertOld();
            sandbox.AssertCleanWorkFolder();

            Assert.Equal(InstallOutcome.Unchanged, sandbox.Recovering().Recover());
            sandbox.AssertOld();
        }
    }

    [Fact]
    public void ACrashWhileRollingBackIsRecovered()
    {
        var moves = MoveCount();
        for (var crashing = 0; crashing < moves - 1; crashing += 3)
        {
            using var sandbox = new InstallTests();
            var at = crashing;
            Assert.Throws<SimulatedCrashException>(() => sandbox.Run(sandbox.Installer(
                beforeMove: (index, _) =>
                {
                    if (index == moves - 1)
                    {
                        throw new IOException("Simulated failure at the last move.");
                    }
                },
                beforeUndo: (index, _) =>
                {
                    if (index == at)
                    {
                        throw new SimulatedCrashException();
                    }
                })));

            Assert.Equal(InstallOutcome.RolledBack, sandbox.Recovering().Recover());
            sandbox.AssertOld();
            sandbox.AssertCleanWorkFolder();
        }
    }

    [Fact]
    public void ACrashAfterTheCommitIsCompletedByRecovery()
    {
        Assert.Throws<SimulatedCrashException>(() => Run(Installer(afterCommit: () => throw new SimulatedCrashException())));
        AssertNew();
        Assert.Contains("Committed", File.ReadAllText(ReleaseInstallation.JournalPath(_sandbox.Installation)), StringComparison.Ordinal);

        Assert.Equal(InstallOutcome.Installed, Recovering().Recover());

        AssertNew();
        AssertCleanWorkFolder();
        Assert.True(File.Exists(ReleaseInstallation.SuccessMarkerPath(_sandbox.Installation)));
    }

    [Fact]
    public void AnInterruptedInstallationIsRecoveredBeforeTheNextOne()
    {
        Assert.Throws<SimulatedCrashException>(() => Run(Installer(beforeMove: (index, _) =>
        {
            if (index == 5)
            {
                throw new SimulatedCrashException();
            }
        })));

        Assert.Equal(InstallOutcome.Installed, Run());

        AssertNew();
        AssertCleanWorkFolder();
    }

    // ---- A rollback that fails: everything kept ----

    [Fact]
    public void ARollbackThatFailsKeepsEveryPieceOfEvidenceAndCanBeRetried()
    {
        var moves = MoveCount();
        using var sandbox = new InstallTests();
        Assert.Equal(InstallOutcome.RollbackFailed, sandbox.Run(sandbox.Installer(
            beforeMove: (index, _) =>
            {
                if (index == moves - 2)
                {
                    throw new IOException("Simulated failure.");
                }
            },
            beforeUndo: (index, move) =>
            {
                if (move.Kind == MoveKind.Backup && move.Path == "YAT.dll")
                {
                    throw new IOException("Simulated failure to restore YAT.dll.");
                }
            })));

        var installation = sandbox._sandbox.Installation;
        Assert.Contains("RollbackFailed", File.ReadAllText(ReleaseInstallation.JournalPath(installation)), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(ReleaseInstallation.BackupFolder(installation), "YAT.dll")), "the backup is kept");
        Assert.True(Directory.Exists(ReleaseInstallation.StagingFolder(installation, V3)), "the staging folder is kept");
        var notice = Assert.Single(sandbox._host.Notices);
        Assert.Contains("could not be restored automatically", notice.Message, StringComparison.Ordinal);
        Assert.Contains("recover --protocol 1 --install-dir", notice.Message, StringComparison.Ordinal);
        Assert.Empty(sandbox._host.Started);

        Assert.Equal(InstallOutcome.RolledBack, sandbox.Recovering().Recover());
        sandbox.AssertOld();
        sandbox.AssertCleanWorkFolder();
    }

    [Fact]
    public void AnUnreadableJournalIsKeptAndReported()
    {
        Directory.CreateDirectory(WorkFolder);
        File.WriteAllText(ReleaseInstallation.JournalPath(_sandbox.Installation), "{ damaged");

        Assert.Equal(InstallOutcome.RollbackFailed, Recovering().Recover());

        Assert.True(File.Exists(ReleaseInstallation.JournalPath(_sandbox.Installation)));
        Assert.Contains("cannot be read", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RecoveringWithNothingToRecoverChangesNothing()
    {
        Assert.Equal(InstallOutcome.Unchanged, Recovering().Recover());

        AssertOld();
        Assert.Contains("no interrupted", Assert.Single(_host.Notices).Message, StringComparison.Ordinal);
    }

    // ---- The journal ----

    [Fact]
    public void TheJournalIsWrittenBeforeTheFirstMoveAndListsEveryMove()
    {
        InstallJournal? seen = null;
        Run(Installer(beforeMove: (index, _) => seen ??= InstallJournal.Read(ReleaseInstallation.JournalPath(_sandbox.Installation))));

        Assert.NotNull(seen);
        Assert.Equal((V2, V3, JournalState.Replacing), (seen.From, seen.To, seen.State));
        Assert.Equal(14, seen.Moves.Count);
        Assert.All(seen.Moves.Where(move => move.Kind == MoveKind.Place && move.Path != ReleaseInventory.FileName),
            move => Assert.Equal(Sha(Files(V3)[move.Path]), move.Sha256));
        Assert.Equal(["lang/zh-Hant", "lang"], seen.CreatedDirectories);
    }

    [Fact]
    public void AJournalNamingAPathOutsideTheReleaseIsRefused()
    {
        Directory.CreateDirectory(WorkFolder);
        var journal = new InstallJournal(V2, V3, JournalState.Replacing, [new PlannedMove(MoveKind.Place, "../outside.dll", 1, new string('a', 64))], []);
        File.WriteAllBytes(ReleaseInstallation.JournalPath(_sandbox.Installation), journal.ToJson());

        Assert.Throws<UpdaterException>(() => InstallJournal.Read(ReleaseInstallation.JournalPath(_sandbox.Installation)));
    }

    // ---- The install lock ----

    [Fact]
    public void OnlyOneUpdaterHoldsAnInstallation()
    {
        using (var first = InstallLock.TryAcquire(_sandbox.Installation))
        {
            Assert.NotNull(first);
            Assert.Null(InstallLock.TryAcquire(_sandbox.Installation));
        }

        Assert.False(File.Exists(ReleaseInstallation.LockPath(_sandbox.Installation)), "the lock goes with its holder");
        using var again = InstallLock.TryAcquire(_sandbox.Installation);
        Assert.NotNull(again);
    }

    private string UpdatePackageLayoutPath() => YAT.Application.Updates.UpdatePackageLayout.PackagePath(_sandbox.Updates, V3, Rid);

    private static void Junction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link) && new DirectoryInfo(link).LinkTarget is not null, "the junction was made");
    }
}
