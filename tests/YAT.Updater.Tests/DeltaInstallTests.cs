using System.Diagnostics;
using System.IO.Compression;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using static YAT.Updater.Tests.Releases;

namespace YAT.Updater.Tests;

public sealed class DeltaInstallTests : IDisposable
{
    private static readonly Dictionary<string, byte[]> UserFiles = new()
    {
        ["my project.yat"] = Bytes("the user's project, saved next to YAT"),
        ["notes/today.txt"] = Bytes("the user's notes"),
        ["licenses/my-own.txt"] = Bytes("the user's file in a release folder")
    };

    private readonly Sandbox _sandbox = new();
    private readonly FakeHost _host = new();

    public DeltaInstallTests()
    {
        Install(_sandbox.Installation, V2);
        foreach (var file in UserFiles)
        {
            var path = _sandbox.InInstallation(file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }

        Original = Snapshot(_sandbox.Installation);
        OriginalFolders = Folders(_sandbox.Installation);
    }

    private SortedDictionary<string, string> Original { get; }

    private SortedSet<string> OriginalFolders { get; }

    private static FileMover QuickMover => new() { Attempts = 3, Delay = TimeSpan.FromMilliseconds(10) };

    private string WorkFolder => ReleaseInstallation.WorkFolder(_sandbox.Installation);

    public void Dispose() => _sandbox.Dispose();

    private Installer CreateInstaller(
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

    private static void CreateDeltaPackage(
        string updates,
        ReleaseVersion from,
        ReleaseVersion to,
        IDictionary<string, byte[]>? fromFiles = null,
        IDictionary<string, byte[]>? toFiles = null,
        Func<ReleaseDeltaManifest, ReleaseDeltaManifest>? editDelta = null,
        Func<ReleaseInventory, ReleaseInventory>? editTargetInventory = null,
        Action<string>? tamperZip = null)
    {
        fromFiles ??= Files(from);
        toFiles ??= Files(to);
        var baseInventoryBytes = Inventory(from, fromFiles).ToJson();
        var baseInventorySha256 = Sha(baseInventoryBytes);

        var changed = toFiles
            .Where(kv => !fromFiles.TryGetValue(kv.Key, out var oldBytes) || !oldBytes.AsSpan().SequenceEqual(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();
        var removed = fromFiles.Keys
            .Where(k => !toFiles.ContainsKey(k))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        var delta = new ReleaseDeltaManifest(from, to, Rid, baseInventorySha256, changed, removed);
        if (editDelta is not null)
        {
            delta = editDelta(delta);
        }

        var targetInventory = Inventory(to, toFiles);
        if (editTargetInventory is not null)
        {
            targetInventory = editTargetInventory(targetInventory);
        }

        var top = to.PackageName(Rid) + "/";
        var zip = UpdatePackageLayout.DeltaPackagePath(updates, from, to, Rid);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var entries = delta.ChangedFiles
                .Where(toFiles.ContainsKey)
                .Select(path => (Name: top + path, Data: toFiles[path]))
                .Append((Name: top + ReleaseInventory.FileName, Data: targetInventory.ToJson()))
                .Append((Name: top + ReleaseDeltaManifest.FileName, Data: delta.ToJson()))
                .OrderBy(entry => entry.Name, StringComparer.Ordinal);
            foreach (var (name, data) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        var zipBytes = File.ReadAllBytes(zip);
        var manifest = new ReleaseManifest(
            to,
            [new ReleasePackage(Rid, new Uri($"https://example.test/{to}.zip"), Sha(zipBytes), zipBytes.LongLength)],
            Deltas: [new ReleaseDeltaPackage(Rid, from, new Uri($"https://example.test/{from}-to-{to}.delta.zip"), Sha(zipBytes), zipBytes.LongLength)]);
        File.WriteAllBytes(UpdatePackageLayout.DeltaSnapshotPath(updates, from, to), manifest.ToJson());

        tamperZip?.Invoke(zip);
    }

    private void AssertOld()
    {
        Assert.Equal(Original, Snapshot(_sandbox.Installation));
        Assert.Equal(OriginalFolders, Folders(_sandbox.Installation));
    }

    private void AssertNew(IDictionary<string, byte[]>? targetFiles = null)
    {
        var expected = targetFiles is null
            ? Expected(V3, user: UserFiles)
            : ExpectedCustom(V3, targetFiles, UserFiles);
        Assert.Equal(expected, Snapshot(_sandbox.Installation));
    }

    private static SortedDictionary<string, string> ExpectedCustom(
        ReleaseVersion version,
        IDictionary<string, byte[]> releaseFiles,
        IDictionary<string, byte[]> userFiles)
    {
        var all = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in userFiles)
        {
            all[kv.Key] = Sha(kv.Value);
        }

        foreach (var kv in releaseFiles)
        {
            all[kv.Key] = Sha(kv.Value);
        }

        all[ReleaseInventory.FileName] = Sha(Inventory(version, releaseFiles).ToJson());
        return all;
    }

    private void AssertCleanWorkFolder()
    {
        var left = Directory.Exists(WorkFolder)
            ? Directory.EnumerateFileSystemEntries(WorkFolder).Select(Path.GetFileName).Where(name => name is not ("update.log" or "updated.json")).ToList()
            : [];
        Assert.True(left.Count == 0, "left: " + string.Join(", ", left));
    }

    [Fact]
    public void UpdaterArguments_ParsesOptionalPackageKind_AndRejectsUnknown()
    {
        string[] baseArgs =
        [
            "install",
            "--protocol", "1",
            UpdateInstallProtocol.InstallDirectoryOption, _sandbox.Installation,
            UpdateInstallProtocol.UpdatesRootOption, _sandbox.Updates,
            "--version", "0.3.0",
            "--from-version", "0.2.0",
            "--pid", "123",
            "--pid-start", "456",
            UpdateInstallProtocol.PipeInOption, "100",
            UpdateInstallProtocol.PipeOutOption, "200"
        ];

        Assert.True(UpdaterArguments.TryParse(baseArgs, out var omitted, out var problem), problem);
        Assert.False(omitted!.IsDelta);

        Assert.True(UpdaterArguments.TryParse([.. baseArgs, "--package-kind", "full"], out var full, out problem), problem);
        Assert.False(full!.IsDelta);

        Assert.True(UpdaterArguments.TryParse([.. baseArgs, "--package-kind", "delta"], out var delta, out problem), problem);
        Assert.True(delta!.IsDelta);

        Assert.False(UpdaterArguments.TryParse([.. baseArgs, "--package-kind", "invalid"], out _, out problem));
        Assert.Contains("not supported", problem);

        Assert.False(UpdaterArguments.TryParse(["recover", "--protocol", "1", "--install-dir", _sandbox.Installation, "--package-kind", "delta"], out _, out problem));
        Assert.Contains("Unknown option", problem);
    }

    [Fact]
    public void DeltaInstall_Succeeds_KeepingUnchangedFiles_RemovingOldFiles_AddingNewFiles_AndPreservingUserFiles()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var backedUp = new List<string>();
        var placed = new List<string>();

        var outcome = CreateInstaller(beforeMove: (_, move) =>
        {
            if (move.Kind == MoveKind.Backup)
            {
                backedUp.Add(move.Path);
            }
            else
            {
                placed.Add(move.Path);
            }
        }).Install(V2, V3, isDelta: true);

        Assert.Equal(InstallOutcome.Installed, outcome);
        AssertNew();
        AssertCleanWorkFolder();
        Assert.DoesNotContain("coreclr.dll", backedUp);
        Assert.DoesNotContain("coreclr.dll", placed);
        Assert.DoesNotContain("licenses/SkiaSharp/LICENSE.txt", backedUp);
        Assert.DoesNotContain("licenses/SkiaSharp/LICENSE.txt", placed);
        Assert.Contains("old-only.dll", backedUp);
        Assert.Contains("plugins/legacy/old.dll", backedUp);
        Assert.False(File.Exists(_sandbox.InInstallation("old-only.dll")));
        Assert.False(File.Exists(_sandbox.InInstallation("plugins/legacy/old.dll")));
    }

    [Fact]
    public void DeltaInstall_WhenYatExeIsKept_Succeeds_AndNeverTouchesYatExe()
    {
        var fromFiles = Files(V2);
        var toFiles = Files(V3);
        toFiles["YAT.exe"] = fromFiles["YAT.exe"];
        CreateDeltaPackage(_sandbox.Updates, V2, V3, fromFiles, toFiles);

        var movedPaths = new List<string>();
        _host.Version = _ => V3.FileVersion;
        var outcome = CreateInstaller(beforeMove: (_, move) => movedPaths.Add(move.Path)).Install(V2, V3, isDelta: true);

        Assert.Equal(InstallOutcome.Installed, outcome);
        AssertNew(toFiles);
        Assert.DoesNotContain("YAT.exe", movedPaths);
    }

    [Fact]
    public void DeltaInstall_WhenYatExeIsChanged_BacksUpFirstAndPlacesLast()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var backups = new List<string>();
        var places = new List<string>();

        var outcome = CreateInstaller(beforeMove: (_, move) =>
        {
            if (move.Kind == MoveKind.Backup)
            {
                backups.Add(move.Path);
            }
            else
            {
                places.Add(move.Path);
            }
        }).Install(V2, V3, isDelta: true);

        Assert.Equal(InstallOutcome.Installed, outcome);
        Assert.Equal("YAT.exe", backups[0]);
        Assert.Equal("YAT.exe", places[^1]);
    }

    [Fact]
    public void DeltaInstall_Refuses_WhenDeltaZipSha256Mismatches()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3, tamperZip: zip => File.AppendAllBytes(zip, [0x00]));

        Assert.Equal(InstallOutcome.Unchanged, CreateInstaller().Install(V2, V3, isDelta: true));
        Assert.Contains(_host.Notices, n => n.Error && n.Message.Contains("not the", StringComparison.Ordinal));
        AssertOld();
    }

    [Fact]
    public void DeltaInstall_Refuses_WhenBaseInventorySha256Mismatches()
    {
        CreateDeltaPackage(
            _sandbox.Updates,
            V2,
            V3,
            editDelta: delta => new ReleaseDeltaManifest(delta.FromVersion, delta.ToVersion, delta.Rid, new string('a', 64), delta.ChangedFiles, delta.RemovedFiles));

        Assert.Equal(InstallOutcome.Unchanged, CreateInstaller().Install(V2, V3, isDelta: true));
        Assert.Contains(_host.Notices, n => n.Error && n.Message.Contains("delta package is not a valid", StringComparison.Ordinal));
        AssertOld();
    }

    [Fact]
    public void DeltaInstall_Refuses_WhenKeptFileIsMissingOrTamperedInInstallation()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        File.WriteAllBytes(_sandbox.InInstallation("coreclr.dll"), Bytes("corrupted runtime"));

        Assert.Equal(InstallOutcome.Unchanged, CreateInstaller().Install(V2, V3, isDelta: true));
        Assert.Contains(_host.Notices, n => n.Error && n.Message.Contains("coreclr.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void DeltaInstall_Refuses_WhenNewFileCollidesWithUnmanagedUserFile()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        File.WriteAllBytes(_sandbox.InInstallation("new-only.dll"), Bytes("user file"));

        Assert.Equal(InstallOutcome.Unchanged, CreateInstaller().Install(V2, V3, isDelta: true));
        Assert.Contains(_host.Notices, n => n.Error && n.Message.Contains("new-only.dll", StringComparison.Ordinal));
    }

    [Fact]
    public void DeltaInstall_Refuses_WhenInstallationFolderIsReparsePoint()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var link = Path.Combine(_sandbox.Root, "YAT-junction");
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{_sandbox.Installation}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);

        var installer = new Installer(link, _sandbox.Updates, _host, _sandbox.Log, QuickMover);
        Assert.Equal(InstallOutcome.Unchanged, installer.Install(V2, V3, isDelta: true));
        Assert.Contains(_host.Notices, n => n.Error && n.Message.Contains("link", StringComparison.Ordinal));
        AssertOld();
    }

    [Fact]
    public void DeltaInstall_Refuses_WhenDeltaOmitsRemovedFile()
    {
        CreateDeltaPackage(
            _sandbox.Updates,
            V2,
            V3,
            editDelta: delta => new ReleaseDeltaManifest(delta.FromVersion, delta.ToVersion, delta.Rid, delta.BaseInventorySha256, delta.ChangedFiles, ["old-only.dll"]));

        Assert.Equal(InstallOutcome.Unchanged, CreateInstaller().Install(V2, V3, isDelta: true));
        Assert.Contains(_host.Notices, n => n.Error && n.Message.Contains("plugins/legacy/old.dll", StringComparison.Ordinal));
        AssertOld();
    }

    [Fact]
    public void DeltaInstall_RollsBackCleanly_WhenMoveFailsMidway()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var count = 0;
        Assert.Equal(InstallOutcome.Installed, CreateInstaller(beforeMove: (_, _) => count++).Install(V2, V3, isDelta: true));

        // Reset installation to V2 + UserFiles for rollback test.
        using var fresh = new Sandbox();
        Install(fresh.Installation, V2);
        foreach (var file in UserFiles)
        {
            var path = fresh.InInstallation(file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, file.Value);
        }

        CreateDeltaPackage(fresh.Updates, V2, V3);
        var original = Snapshot(fresh.Installation);
        var installer = new Installer(fresh.Installation, fresh.Updates, _host, fresh.Log, QuickMover)
        {
            BeforeMove = (index, _) =>
            {
                if (index == Math.Max(1, count / 2))
                {
                    throw new IOException("simulated disk failure");
                }
            }
        };

        Assert.Equal(InstallOutcome.RolledBack, installer.Install(V2, V3, isDelta: true));
        Assert.Equal(original, Snapshot(fresh.Installation));
    }

    [Fact]
    public void DeltaInstall_RecoversCleanly_AfterSimulatedCrashMidway()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var installer = CreateInstaller(beforeMove: (index, _) =>
        {
            if (index == 2)
            {
                throw new SimulatedCrashException();
            }
        });

        Assert.Throws<SimulatedCrashException>(() => installer.Install(V2, V3, isDelta: true));

        var recovered = CreateInstaller().Recover();
        Assert.Equal(InstallOutcome.RolledBack, recovered);
        AssertOld();
        AssertCleanWorkFolder();
    }

    [Fact]
    public void DeltaInstall_RollsBack_WhenFinalValidationDetectsTamperedKeptFile()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var installer = CreateInstaller(beforeMove: (_, move) =>
        {
            if (move.Kind == MoveKind.Place && move.Path == "YAT.exe")
            {
                File.WriteAllBytes(_sandbox.InInstallation("coreclr.dll"), Bytes("tampered mid-install"));
            }
        });

        Assert.Equal(InstallOutcome.RollbackFailed, installer.Install(V2, V3, isDelta: true));
        Assert.Empty(_host.Started);
        Assert.Contains("RollbackFailed", File.ReadAllText(ReleaseInstallation.JournalPath(_sandbox.Installation)), StringComparison.Ordinal);
        Assert.False(File.Exists(_sandbox.InInstallation("new-only.dll")));
        Assert.True(File.Exists(_sandbox.InInstallation("old-only.dll")));
        Assert.Equal(Sha(Bytes("exe 0.2.0")), Sha(File.ReadAllBytes(_sandbox.InInstallation("YAT.exe"))));
    }

    [Fact]
    public void DeltaInstall_WhenRollbackFailsMidway_KeepsJournalAndBackup_DoesNotRestartYat_AndRecoversOnRetry()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var installer = CreateInstaller(
            beforeMove: (_, move) =>
            {
                if (move.Kind == MoveKind.Place && move.Path == "YAT.exe")
                {
                    throw new IOException("simulated failure before placing YAT.exe");
                }
            },
            beforeUndo: (_, move) =>
            {
                if (move.Kind == MoveKind.Backup && move.Path == "old-only.dll")
                {
                    throw new IOException("simulated backup restore failure");
                }
            });

        Assert.Equal(InstallOutcome.RollbackFailed, installer.Install(V2, V3, isDelta: true));
        Assert.Empty(_host.Started);
        Assert.True(File.Exists(ReleaseInstallation.JournalPath(_sandbox.Installation)));
        Assert.Contains("RollbackFailed", File.ReadAllText(ReleaseInstallation.JournalPath(_sandbox.Installation)), StringComparison.Ordinal);
        Assert.True(Directory.Exists(ReleaseInstallation.BackupFolder(_sandbox.Installation)));

        Assert.Equal(InstallOutcome.RolledBack, CreateInstaller().Recover());
        AssertOld();
        AssertCleanWorkFolder();
    }

    [Fact]
    public void DeltaInstall_WhenMoveFailsBeforeFinalValidationAndKeptFileWasMutated_ReportsRollbackFailedAndDoesNotRestartYat()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var installer = CreateInstaller(beforeMove: (_, move) =>
        {
            if (move.Kind == MoveKind.Place && move.Path == "YAT.dll")
            {
                File.WriteAllBytes(_sandbox.InInstallation("coreclr.dll"), Bytes("mutated before final validation"));
                throw new IOException("simulated move failure before final validation");
            }
        });

        Assert.Equal(InstallOutcome.RollbackFailed, installer.Install(V2, V3, isDelta: true));
        Assert.Empty(_host.Started);
        Assert.Contains("RollbackFailed", File.ReadAllText(ReleaseInstallation.JournalPath(_sandbox.Installation)), StringComparison.Ordinal);
        Assert.Equal(Sha(Bytes("exe 0.2.0")), Sha(File.ReadAllBytes(_sandbox.InInstallation("YAT.exe"))));
    }

    [Fact]
    public void DeltaInstall_Refuses_WhenDeltaSnapshotIsTampered()
    {
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var snapshotPath = UpdatePackageLayout.DeltaSnapshotPath(_sandbox.Updates, V2, V3);
        var tamperedManifest = new ReleaseManifest(
            V3,
            [new ReleasePackage(Rid, new Uri("https://example.test/0.3.0.zip"), new string('b', 64), 123)],
            Deltas: [new ReleaseDeltaPackage(Rid, V2, new Uri("https://example.test/0.2.0-to-0.3.0.delta.zip"), new string('c', 64), 123)]);
        File.WriteAllBytes(snapshotPath, tamperedManifest.ToJson());

        Assert.Equal(InstallOutcome.Unchanged, CreateInstaller().Install(V2, V3, isDelta: true));
        Assert.Contains(_host.Notices, n => n.Error && n.Message.Contains("not the", StringComparison.Ordinal));
        AssertOld();
    }

    [Fact]
    public void FullInstall_ContinuesToWorkAlongsideDeltaInstall()
    {
        Package(_sandbox.Updates, V3);
        var outcome = CreateInstaller().Install(V2, V3, isDelta: false);
        Assert.Equal(InstallOutcome.Installed, outcome);
        AssertNew();
        AssertCleanWorkFolder();
    }
}
