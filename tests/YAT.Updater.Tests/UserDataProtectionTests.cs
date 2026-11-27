using System.IO.Compression;
using System.Text;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using static YAT.Updater.Tests.Releases;

namespace YAT.Updater.Tests;

public sealed class UserDataProtectionTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly FakeHost _host = new();

    private static readonly Dictionary<string, byte[]> PortableUserDataFiles = new(StringComparer.Ordinal)
    {
        ["UserData/ImportProfiles/11111111-1111-1111-1111-111111111111.json"] = Bytes("""{"schemaVersion":1,"name":"FT_Prod"}"""),
        ["UserData/ImportProfiles/nested/extra.json"] = Bytes("""{"nested":true}"""),
        ["UserData/custom-notes.bin"] = [0x00, 0xFF, 0x10, 0x20, 0x7F]
    };

    private static FileMover QuickMover => new() { Attempts = 3, Delay = TimeSpan.FromMilliseconds(10) };

    public void Dispose() => _sandbox.Dispose();

    private Installer CreateInstaller(
        Action<int, PlannedMove>? beforeMove = null,
        Action<int, PlannedMove>? beforeUndo = null,
        Action? afterCommit = null) =>
        new(_sandbox.Installation, _sandbox.Updates, _host, _sandbox.Log, QuickMover)
        {
            BeforeMove = beforeMove,
            BeforeUndo = beforeUndo,
            AfterCommit = afterCommit
        };

    private void SeedUserData()
    {
        foreach (var (relative, data) in PortableUserDataFiles)
        {
            var path = _sandbox.InInstallation(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }

    private void AssertUserDataByteForByteIntact()
    {
        foreach (var (relative, expected) in PortableUserDataFiles)
        {
            var path = _sandbox.InInstallation(relative);
            Assert.True(File.Exists(path), $"Expected protected file '{relative}' to remain intact.");
            Assert.Equal(expected, File.ReadAllBytes(path));
        }
    }

    [Fact]
    public void SuccessfulFullUpdatePreservesPortableUserDataByteForByte()
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();
        Package(_sandbox.Updates, V3);

        var outcome = CreateInstaller().Install(V2, V3, isDelta: false);

        Assert.Equal(InstallOutcome.Installed, outcome);
        AssertUserDataByteForByteIntact();
    }

    [Fact]
    public void SuccessfulDeltaUpdatePreservesPortableUserDataByteForByte()
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();
        CreateDeltaPackage(_sandbox.Updates, V2, V3);

        var outcome = CreateInstaller().Install(V2, V3, isDelta: true);

        Assert.Equal(InstallOutcome.Installed, outcome);
        AssertUserDataByteForByteIntact();
    }

    [Fact]
    public void EmptyUserDataDirectoryRemainsIntactAcrossFullDeltaAndRollback()
    {
        Install(_sandbox.Installation, V2);
        var userDataDir = ReleaseInstallation.UserDataFolder(_sandbox.Installation);
        Directory.CreateDirectory(userDataDir);

        // 1. Failed update + rollback preserves empty UserData/
        Package(_sandbox.Updates, V3);
        var failed = CreateInstaller(beforeMove: (index, _) =>
        {
            if (index == 2)
            {
                throw new IOException("simulated failure");
            }
        }).Install(V2, V3, isDelta: false);
        Assert.Equal(InstallOutcome.RolledBack, failed);
        Assert.True(Directory.Exists(userDataDir));

        // 2. Successful Delta update preserves empty UserData/
        CreateDeltaPackage(_sandbox.Updates, V2, V3);
        var deltaOk = CreateInstaller().Install(V2, V3, isDelta: true);
        Assert.Equal(InstallOutcome.Installed, deltaOk);
        Assert.True(Directory.Exists(userDataDir));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullPackageTargetingUserDataIsRejectedBeforeMutationWhetherOrNotUserDataExists(bool userDataExists)
    {
        Install(_sandbox.Installation, V2);
        if (userDataExists)
        {
            SeedUserData();
        }

        var before = Snapshot(_sandbox.Installation);
        var files = Files(V3);
        var injectedPath = "UserData/ImportProfiles/injected.json";
        var injectedBytes = Bytes("""{"injected":true}""");
        var validInv = Inventory(V3, files);
        var badInvJson = InjectInventoryEntry(validInv, injectedPath, injectedBytes);

        Package(_sandbox.Updates, V3, files,
            edit: (archive, top) => Add(archive, top + injectedPath, injectedBytes),
            inventory: Encoding.UTF8.GetBytes(badInvJson));

        var outcome = CreateInstaller().Install(V2, V3, isDelta: false);

        Assert.Equal(InstallOutcome.Unchanged, outcome);
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        if (userDataExists)
        {
            AssertUserDataByteForByteIntact();
        }
        else
        {
            Assert.False(Directory.Exists(ReleaseInstallation.UserDataFolder(_sandbox.Installation)));
        }
    }

    [Fact]
    public void DeltaToFullFallbackPreservesUserDataAndEnforcesSameProtectionPolicy()
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();
        var before = Snapshot(_sandbox.Installation);

        // 1. Malicious Delta package attempting to add/replace UserData is rejected before mutation.
        CreateDeltaPackageWithInjectedUserData(_sandbox.Updates, V2, V3, "UserData/ImportProfiles/11111111-1111-1111-1111-111111111111.json");
        var deltaOutcome = CreateInstaller().Install(V2, V3, isDelta: true);
        Assert.Equal(InstallOutcome.Unchanged, deltaOutcome);
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        AssertUserDataByteForByteIntact();

        // 2. Fallback to valid Full package succeeds and preserves UserData byte-for-byte.
        Package(_sandbox.Updates, V3);
        var fullOutcome = CreateInstaller().Install(V2, V3, isDelta: false);
        Assert.Equal(InstallOutcome.Installed, fullOutcome);
        AssertUserDataByteForByteIntact();
    }

    [Fact]
    public void FailedInstallationRollbackAndInterruptedRecoveryPreserveUserData()
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();
        var before = Snapshot(_sandbox.Installation);
        Package(_sandbox.Updates, V3);

        // 1. Mid-installation failure -> automatic rollback preserves UserData.
        var rollbackOutcome = CreateInstaller(beforeMove: (index, _) =>
        {
            if (index == 3)
            {
                throw new IOException("disk error during move");
            }
        }).Install(V2, V3, isDelta: false);
        Assert.Equal(InstallOutcome.RolledBack, rollbackOutcome);
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        AssertUserDataByteForByteIntact();

        // 2. Simulated crash -> Recover() restores V2 and preserves UserData.
        Assert.Throws<SimulatedCrashException>(() =>
            CreateInstaller(beforeMove: (index, _) =>
            {
                if (index == 4)
                {
                    throw new SimulatedCrashException();
                }
            }).Install(V2, V3, isDelta: false));

        var recoveryOutcome = CreateInstaller().Recover();
        Assert.Equal(InstallOutcome.RolledBack, recoveryOutcome);
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        AssertUserDataByteForByteIntact();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnsafeRecoveryJournalTargetingUserDataIsRejectedAndPreservesUserDataAndEvidence(bool targetInMoves)
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();

        var workDir = ReleaseInstallation.WorkFolder(_sandbox.Installation);
        var backupDir = ReleaseInstallation.BackupFolder(_sandbox.Installation);
        Directory.CreateDirectory(workDir);
        Directory.CreateDirectory(backupDir);
        var backupEvidence = Path.Combine(backupDir, "YAT.exe");
        File.WriteAllBytes(backupEvidence, Bytes("backup evidence"));

        var journalPath = ReleaseInstallation.JournalPath(_sandbox.Installation);
        var movesJson = targetInMoves
            ? $$"""
                [
                  { "kind": "Backup", "path": "UserData/ImportProfiles/11111111-1111-1111-1111-111111111111.json", "size": 36, "sha256": "{{new string('a', 64)}}" }
                ]
                """
            : "[]";
        var dirsJson = targetInMoves ? "[]" : """["UserData", "UserData/ImportProfiles"]""";
        var unsafeJournalJson = $$"""
            {
              "schemaVersion": 1,
              "from": "0.2.0",
              "to": "0.3.0",
              "state": "Replacing",
              "moves": {{movesJson}},
              "createdDirectories": {{dirsJson}}
            }
            """;
        File.WriteAllText(journalPath, unsafeJournalJson, new UTF8Encoding(false));

        Assert.Throws<UpdaterException>(() => InstallJournal.Read(journalPath));

        var recoveryOutcome = CreateInstaller().Recover();
        Assert.Equal(InstallOutcome.RollbackFailed, recoveryOutcome);
        AssertUserDataByteForByteIntact();
        Assert.True(File.Exists(journalPath));
        Assert.True(File.Exists(backupEvidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyInventoryListingUserDataFailsClosedWithoutMutatingInstallationOrUserData(bool isDelta)
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();

        var targetProfile = "UserData/ImportProfiles/11111111-1111-1111-1111-111111111111.json";
        var legacyInvJson = InjectInventoryEntry(
            Inventory(V2, Files(V2)),
            targetProfile,
            PortableUserDataFiles[targetProfile]);
        File.WriteAllText(ReleaseInstallation.InventoryPath(_sandbox.Installation), legacyInvJson, new UTF8Encoding(false));

        var before = Snapshot(_sandbox.Installation);
        if (isDelta)
        {
            CreateDeltaPackage(_sandbox.Updates, V2, V3);
        }
        else
        {
            Package(_sandbox.Updates, V3);
        }

        var outcome = CreateInstaller().Install(V2, V3, isDelta: isDelta);

        Assert.Equal(InstallOutcome.Unchanged, outcome);
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        AssertUserDataByteForByteIntact();
    }

    [Fact]
    public void ReparsePointOnWorkFolderStillBlocksRecoveryWhilePreservingUserData()
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();

        var externalTarget = Path.Combine(_sandbox.Root, "external-work");
        Directory.CreateDirectory(externalTarget);
        var workFolder = ReleaseInstallation.WorkFolder(_sandbox.Installation);
        Directory.CreateSymbolicLink(workFolder, externalTarget);

        var outcome = CreateInstaller().Recover();

        Assert.Equal(InstallOutcome.RollbackFailed, outcome);
        AssertUserDataByteForByteIntact();
    }

    [Fact]
    public void IndirectDirectoryDeletionCannotDeleteInstallationRootAncestorOrUserData()
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();

        var replacement = new Replacement(_sandbox.Installation, _sandbox.Log, QuickMover, _host.FileVersionOf);
        var userDataRoot = ReleaseInstallation.UserDataFolder(_sandbox.Installation);
        var userDataSubdir = Path.Combine(userDataRoot, "ImportProfiles");
        var parentOfInstall = Path.GetDirectoryName(_sandbox.Installation)!;

        // DeleteTree must refuse installation root, ancestor of installation root, UserData, and UserData descendants.
        Assert.Throws<UpdaterException>(() => replacement.DeleteTree(_sandbox.Installation));
        Assert.Throws<UpdaterException>(() => replacement.DeleteTree(parentOfInstall));
        Assert.Throws<UpdaterException>(() => replacement.DeleteTree(userDataRoot));
        Assert.Throws<UpdaterException>(() => replacement.DeleteTree(userDataSubdir));

        // DeleteIfEmpty must ignore installation root, ancestor, and empty UserData directory.
        var emptyProtectedChild = Path.Combine(userDataRoot, "EmptyChild");
        Directory.CreateDirectory(emptyProtectedChild);
        replacement.DeleteIfEmpty(_sandbox.Installation);
        replacement.DeleteIfEmpty(parentOfInstall);
        replacement.DeleteIfEmpty(userDataRoot);
        replacement.DeleteIfEmpty(emptyProtectedChild);

        Assert.True(Directory.Exists(_sandbox.Installation));
        Assert.True(Directory.Exists(userDataRoot));
        Assert.True(Directory.Exists(emptyProtectedChild));
        AssertUserDataByteForByteIntact();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedRecoveryJournalWithLegitimateAndProtectedEntriesCausesZeroPartialMutation(bool protectedInMoves)
    {
        Install(_sandbox.Installation, V2);
        SeedUserData();

        // Simulate an interrupted installation where YAT.exe was moved into .yat-update\backup.
        var backupDir = ReleaseInstallation.BackupFolder(_sandbox.Installation);
        Directory.CreateDirectory(backupDir);
        var installedExe = ReleaseInstallation.Executable(_sandbox.Installation);
        var backupExe = Path.Combine(backupDir, "YAT.exe");
        var oldExeBytes = File.ReadAllBytes(installedExe);
        File.Move(installedExe, backupExe);

        var journalPath = ReleaseInstallation.JournalPath(_sandbox.Installation);
        var movesJson = protectedInMoves
            ? $$"""
                [
                  { "kind": "Backup", "path": "YAT.exe", "size": {{oldExeBytes.LongLength}}, "sha256": "{{Sha(oldExeBytes)}}" },
                  { "kind": "Backup", "path": "UserData/ImportProfiles/11111111-1111-1111-1111-111111111111.json", "size": 36, "sha256": "{{new string('a', 64)}}" }
                ]
                """
            : $$"""
                [
                  { "kind": "Backup", "path": "YAT.exe", "size": {{oldExeBytes.LongLength}}, "sha256": "{{Sha(oldExeBytes)}}" }
                ]
                """;
        var dirsJson = protectedInMoves ? "[]" : """["lang/zh-Hant", "UserData/ImportProfiles"]""";
        var mixedJournalJson = $$"""
            {
              "schemaVersion": 1,
              "from": "0.2.0",
              "to": "0.3.0",
              "state": "Replacing",
              "moves": {{movesJson}},
              "createdDirectories": {{dirsJson}}
            }
            """;
        File.WriteAllText(journalPath, mixedJournalJson, new UTF8Encoding(false));

        var outcome = CreateInstaller().Recover();

        // Must reject before replaying the legitimate YAT.exe backup move (zero partial mutation).
        Assert.Equal(InstallOutcome.RollbackFailed, outcome);
        Assert.False(File.Exists(installedExe));
        Assert.True(File.Exists(backupExe));
        Assert.Equal(oldExeBytes, File.ReadAllBytes(backupExe));
        Assert.True(File.Exists(journalPath));
        AssertUserDataByteForByteIntact();
    }

    private static string InjectInventoryEntry(ReleaseInventory validInventory, string extraPath, byte[] extraBytes)
    {
        var originalJson = Encoding.UTF8.GetString(validInventory.ToJson());
        var marker = "\"files\": [";
        var index = originalJson.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0);
        var entry = $$"""
            {
                  "path": "{{extraPath}}",
                  "size": {{extraBytes.LongLength}},
                  "sha256": "{{Sha(extraBytes)}}"
                },
            """;
        return originalJson.Insert(index + marker.Length, Environment.NewLine + "    " + entry);
    }

    private static void CreateDeltaPackage(string updates, ReleaseVersion from, ReleaseVersion to)
    {
        var fromFiles = Files(from);
        var toFiles = Files(to);
        var fromInv = Inventory(from, fromFiles);
        var toInv = Inventory(to, toFiles);
        var baseInvSha = Sha(fromInv.ToJson());

        var changed = toFiles
            .Where(kv => !fromFiles.TryGetValue(kv.Key, out var oldBytes) || !oldBytes.AsSpan().SequenceEqual(kv.Value))
            .Select(kv => kv.Key)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        var removed = fromFiles.Keys
            .Where(k => !toFiles.ContainsKey(k))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        var delta = new ReleaseDeltaManifest(from, to, Rid, baseInvSha, changed, removed);
        var top = to.PackageName(Rid) + "/";
        var zip = UpdatePackageLayout.DeltaPackagePath(updates, from, to, Rid);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Add(archive, top + ReleaseDeltaManifest.FileName, delta.ToJson());
            Add(archive, top + ReleaseInventory.FileName, toInv.ToJson());
            foreach (var path in changed)
            {
                Add(archive, top + path, toFiles[path]);
            }
        }

        var zipBytes = File.ReadAllBytes(zip);
        var fullDummy = new ReleasePackage(Rid, new Uri($"https://example.test/{to.Tag}/{to.PackageName(Rid)}.zip"), new string('f', 64), 1024);
        var deltaEntry = new ReleaseDeltaPackage(Rid, from, new Uri($"https://example.test/{to.Tag}/{Path.GetFileName(zip)}"), Sha(zipBytes), zipBytes.LongLength);
        var manifest = new ReleaseManifest(to, [fullDummy], DateTimeOffset.UtcNow, null, [deltaEntry]);
        File.WriteAllBytes(UpdatePackageLayout.DeltaSnapshotPath(updates, from, to), manifest.ToJson());
    }

    private static void CreateDeltaPackageWithInjectedUserData(string updates, ReleaseVersion from, ReleaseVersion to, string userDataPath)
    {
        var fromFiles = Files(from);
        var toFiles = Files(to);
        var fromInv = Inventory(from, fromFiles);
        var toInv = Inventory(to, toFiles);
        var baseInvSha = Sha(fromInv.ToJson());
        var maliciousBytes = Bytes("""{"malicious":true}""");

        var rawDeltaJson = $$"""
            {
              "schemaVersion": 1,
              "fromVersion": "{{from}}",
              "toVersion": "{{to}}",
              "rid": "{{Rid}}",
              "baseInventorySha256": "{{baseInvSha}}",
              "changedFiles": ["{{userDataPath}}", "YAT.exe"],
              "removedFiles": []
            }
            """;
        var rawTargetInvJson = InjectInventoryEntry(toInv, userDataPath, maliciousBytes);

        var top = to.PackageName(Rid) + "/";
        var zip = UpdatePackageLayout.DeltaPackagePath(updates, from, to, Rid);
        Directory.CreateDirectory(Path.GetDirectoryName(zip)!);
        File.Delete(zip);
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Add(archive, top + ReleaseDeltaManifest.FileName, Encoding.UTF8.GetBytes(rawDeltaJson));
            Add(archive, top + ReleaseInventory.FileName, Encoding.UTF8.GetBytes(rawTargetInvJson));
            Add(archive, top + "YAT.exe", toFiles["YAT.exe"]);
            Add(archive, top + userDataPath, maliciousBytes);
        }

        var zipBytes = File.ReadAllBytes(zip);
        var fullDummy = new ReleasePackage(Rid, new Uri($"https://example.test/{to.Tag}/{to.PackageName(Rid)}.zip"), new string('f', 64), 1024);
        var deltaEntry = new ReleaseDeltaPackage(Rid, from, new Uri($"https://example.test/{to.Tag}/{Path.GetFileName(zip)}"), Sha(zipBytes), zipBytes.LongLength);
        var manifest = new ReleaseManifest(to, [fullDummy], DateTimeOffset.UtcNow, null, [deltaEntry]);
        File.WriteAllBytes(UpdatePackageLayout.DeltaSnapshotPath(updates, from, to), manifest.ToJson());
    }
}
