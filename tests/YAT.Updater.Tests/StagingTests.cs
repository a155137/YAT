using System.IO.Compression;
using YAT.Application.Distribution;
using YAT.Application.Updates;
using static YAT.Updater.Tests.Releases;

namespace YAT.Updater.Tests;

// The package verified again and extracted to staging (Task #051.C) - from the files themselves, offline. Every refusal
// leaves no staging folder and the installation untouched.
public sealed class StagingTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public StagingTests() => Install(_sandbox.Installation, V2);

    public void Dispose() => _sandbox.Dispose();

    private string StagingFolder => ReleaseInstallation.StagingFolder(_sandbox.Installation, V3);

    private StagedRelease Stage(Action<string>? afterFile = null) => StagedRelease.Prepare(_sandbox.Installation, _sandbox.Updates, V3, _sandbox.Log, afterFile);

    private string Refused(Action<string>? afterFile = null)
    {
        var before = Snapshot(_sandbox.Installation);
        var exception = Assert.Throws<UpdaterException>(() => Stage(afterFile));
        Assert.False(Directory.Exists(StagingFolder), "no staging folder is left");
        Assert.Equal(before, Snapshot(_sandbox.Installation));
        return exception.Message;
    }

    [Fact]
    public void AVerifiedPackageIsStagedFileByFile()
    {
        Package(_sandbox.Updates, V3);

        var staged = Stage();

        Assert.Equal(Path.GetFullPath(StagingFolder), staged.Folder);
        Assert.Equal(Expected(V3), Snapshot(staged.Folder));
        Assert.Equal(Inventory(V3, Files(V3)).ToJson(), staged.InventoryJson);
        Assert.Equal(Inventory(V3, Files(V3)).Files, staged.Inventory.Files);
        Assert.Equal(Expected(V2), Snapshot(_sandbox.Installation));
    }

    [Fact]
    public void AStaleStagingFolderIsReplaced()
    {
        Package(_sandbox.Updates, V3);
        Directory.CreateDirectory(StagingFolder);
        File.WriteAllText(Path.Combine(StagingFolder, "stale.dll"), "left by an interrupted attempt");

        Assert.Equal(Expected(V3), Snapshot(Stage().Folder));
    }

    // ---- The manifest snapshot ----

    [Fact]
    public void WithoutItsManifestNothingIsStaged()
    {
        Package(_sandbox.Updates, V3);
        File.Delete(UpdatePackageLayout.SnapshotPath(_sandbox.Updates, V3));

        Assert.Contains("cannot be read", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABrokenManifestIsRefused()
    {
        Package(_sandbox.Updates, V3);
        File.WriteAllText(UpdatePackageLayout.SnapshotPath(_sandbox.Updates, V3), "{ not json");

        Assert.Contains("not valid", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestOfAnotherVersionIsRefused()
    {
        Package(_sandbox.Updates, V3, manifest: written => written with { Version = ReleaseVersion.Parse("0.4.0") });

        Assert.Contains("is for 0.4.0, not 0.3.0", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestWithoutAWinX64PackageIsRefused()
    {
        Package(_sandbox.Updates, V3, manifest: written => written with { Packages = [written.Packages[0] with { Rid = "win-arm64" }] });

        Assert.Contains("no win-x64 package", Refused(), StringComparison.Ordinal);
    }

    // ---- The package file ----

    [Fact]
    public void APackageChangedAfterItWasVerifiedIsRefused()
    {
        var zip = Package(_sandbox.Updates, V3);
        var bytes = File.ReadAllBytes(zip);
        bytes[bytes.Length / 2] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);

        Assert.Contains("does not have the SHA-256", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void APackageOfAnotherSizeIsRefused()
    {
        var zip = Package(_sandbox.Updates, V3);
        File.AppendAllText(zip, "x");

        Assert.Contains("bytes, not the", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingPackageIsRefused()
    {
        File.Delete(Package(_sandbox.Updates, V3));

        Assert.Contains("cannot be opened", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileThatIsNotAZipIsRefused()
    {
        var zip = Package(_sandbox.Updates, V3);
        var garbage = new byte[4096];
        new Random(51).NextBytes(garbage);
        File.WriteAllBytes(zip, garbage);
        File.WriteAllBytes(
            UpdatePackageLayout.SnapshotPath(_sandbox.Updates, V3),
            new ReleaseManifest(V3, [new ReleasePackage(Rid, new Uri("https://example.test/p.zip"), Sha(garbage), garbage.Length)]).ToJson());

        Assert.Contains("not a valid ZIP", Refused(), StringComparison.Ordinal);
    }

    // ---- The package's contents (the release package rules) ----

    [Fact]
    public void ABrokenInventoryIsRefused()
    {
        Package(_sandbox.Updates, V3, inventory: Bytes("{ broken"));

        Assert.Contains("not an inventory", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInventoryOfAnotherVersionIsRefused()
    {
        Package(_sandbox.Updates, V3, inventory: Inventory(V2, Files(V3)).ToJson());

        Assert.Contains("inventory is 0.2.0", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileTheInventoryDoesNotListIsRefused()
    {
        Package(_sandbox.Updates, V3, edit: (archive, top) => Add(archive, top + "unexpected.dll", Bytes("x")));

        Assert.Contains("unexpected.dll, which its inventory does not list", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileListedTwiceIsRefused()
    {
        Package(_sandbox.Updates, V3, edit: (archive, top) => Add(archive, top + "yat.DLL", Bytes("again")));

        Assert.Contains("twice", Refused(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("YAT-v0.3.0-win-x64/../evil.dll")]
    [InlineData("YAT-v0.3.0-win-x64/lang/../../evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("YAT-v0.3.0-win-x64/C:evil.dll")]
    [InlineData(@"YAT-v0.3.0-win-x64\..\evil.dll")]
    [InlineData("other/evil.dll")]
    public void AnEntryThatCouldLeaveTheFolderIsRefused(string entry)
    {
        Package(_sandbox.Updates, V3, edit: (archive, _) => Add(archive, entry, Bytes("evil")));

        Refused();
        Assert.False(File.Exists(Path.Combine(_sandbox.Root, "evil.dll")));
        Assert.False(File.Exists(Path.Combine(_sandbox.Installation, ".yat-update", "staging", "evil.dll")));
    }

    [Fact]
    public void AnInventoryThatClaimsTheUpdatersFolderIsRefused()
    {
        var files = Files(V3);
        files[".yat-update/journal.json"] = Bytes("{}");
        Package(_sandbox.Updates, V3, files);

        Assert.Contains("belongs to the updater", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileNotTheOneListedIsRefusedAsItIsWritten()
    {
        var files = Files(V3);
        var inventory = Inventory(V3, files);
        files["YAT.dll"] = Bytes("YAT library 0.3.0, but not the one listed");
        Package(_sandbox.Updates, V3, files, inventory: inventory.ToJson());

        Assert.Contains("YAT.dll is not the file its inventory lists", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void MoreBytesThanListedAreNeverWritten()
    {
        var files = Files(V3);
        var listed = Inventory(V3, files);
        files["coreclr.dll"] = [.. files["coreclr.dll"], .. new byte[100_000]];
        Package(_sandbox.Updates, V3, files, inventory: listed.ToJson());

        Assert.Contains("coreclr.dll is not the file its inventory lists", Refused(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInterruptedExtractionLeavesNothing()
    {
        Package(_sandbox.Updates, V3);
        var extracted = 0;

        var message = Refused(_ =>
        {
            if (++extracted == 3)
            {
                throw new IOException("The disk was removed.");
            }
        });

        Assert.Contains("could not be extracted: The disk was removed.", message, StringComparison.Ordinal);
    }
}
