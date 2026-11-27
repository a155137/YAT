using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;

namespace YAT.Application.Tests;

public sealed class UserDataProtectionTests
{
    private static readonly ReleaseVersion V2 = ReleaseVersion.Parse("0.2.0");
    private static readonly ReleaseVersion V3 = ReleaseVersion.Parse("0.3.0");
    private static readonly string ShaA = new('a', 64);

    [Theory]
    [InlineData("UserData")]
    [InlineData("UserData/")]
    [InlineData("UserData\\")]
    [InlineData("UserData/ImportProfiles/profile.json")]
    [InlineData("userdata/ImportProfiles/profile.json")]
    [InlineData("USERDATA/ImportProfiles/profile.json")]
    [InlineData("UserData\\ImportProfiles\\profile.json")]
    [InlineData("UserData/ImportProfiles\\profile.json")]
    [InlineData("UserData./ImportProfiles/profile.json")]
    [InlineData("UserData /ImportProfiles/profile.json")]
    public void UserDataPathsAreProtectedAcrossCaseSeparatorsAndAliases(string path)
    {
        Assert.True(ReleaseInstallation.IsUserData(path));
        Assert.True(ReleaseInstallation.IsProtected(path));
        Assert.False(ReleaseInstallation.IsReserved(path));
    }

    [Theory]
    [InlineData("UserDatabase.dll")]
    [InlineData("UserDataBackup/file.txt")]
    [InlineData("assets/UserDataExample.txt")]
    [InlineData("assets/UserData/file.txt")]
    [InlineData("YAT.exe")]
    public void UnrelatedPathsWithSimilarPrefixesAreNotClassifiedAsUserData(string path)
    {
        Assert.False(ReleaseInstallation.IsUserData(path));
        Assert.False(ReleaseInstallation.IsProtected(path));
    }

    [Theory]
    [InlineData("UserData./profile.json")]
    [InlineData("UserData /profile.json")]
    [InlineData("../UserData/profile.json")]
    [InlineData("./UserData/profile.json")]
    [InlineData("/UserData/profile.json")]
    [InlineData("C:/UserData/profile.json")]
    [InlineData("UserData//profile.json")]
    [InlineData("UserData:stream")]
    public void UnsafePathRepresentationsAreRejectedByReleasePathNormalization(string path)
    {
        Assert.Null(ReleasePath.Normalize(path));
    }

    [Theory]
    [InlineData(".yat-update")]
    [InlineData(".yat-update/")]
    [InlineData(".YAT-UPDATE/journal.json")]
    [InlineData(".yat-update\\backup\\YAT.exe")]
    public void ExistingDotYatUpdateReservedPathProtectionRemainsEffective(string path)
    {
        Assert.True(ReleaseInstallation.IsReserved(path));
        Assert.True(ReleaseInstallation.IsProtected(path));
        Assert.False(ReleaseInstallation.IsUserData(path));
    }

    [Theory]
    [InlineData("UserData")]
    [InlineData("UserData/ImportProfiles/profile.json")]
    [InlineData("userdata/custom.txt")]
    public void ReleaseInventoryRejectsUserDataEntries(string protectedPath)
    {
        var files = new[]
        {
            new ReleaseFile("YAT.exe", 4, ShaA),
            new ReleaseFile(protectedPath, 4, ShaA)
        };

        var ex = Assert.Throws<ArgumentException>(() => new ReleaseInventory(V3, "win-x64", files));
        Assert.Contains("UserData", ex.Message, StringComparison.OrdinalIgnoreCase);

        var json = $$"""
            {
              "schemaVersion": 1,
              "version": "0.3.0",
              "rid": "win-x64",
              "files": [
                { "path": "YAT.exe", "size": 4, "sha256": "{{ShaA}}" },
                { "path": "{{protectedPath}}", "size": 4, "sha256": "{{ShaA}}" }
              ]
            }
            """;
        Assert.False(ReleaseInventory.TryRead(json, out _, out var problem));
        Assert.Contains("UserData", problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("UserData/ImportProfiles/new.json", true)]
    [InlineData("userdata/ImportProfiles/replaced.json", true)]
    [InlineData("USERDATA/ImportProfiles/removed.json", false)]
    public void DeltaManifestRejectsUserDataAdditionsReplacementsAndRemovals(string protectedPath, bool inChanged)
    {
        var changed = inChanged ? new[] { protectedPath } : Array.Empty<string>();
        var removed = inChanged ? Array.Empty<string>() : new[] { protectedPath };

        var ex = Assert.Throws<ArgumentException>(() =>
            new ReleaseDeltaManifest(V2, V3, "win-x64", ShaA, changed, removed));
        Assert.Contains("UserData", ex.Message, StringComparison.OrdinalIgnoreCase);

        var arrayName = inChanged ? "changedFiles" : "removedFiles";
        var otherName = inChanged ? "removedFiles" : "changedFiles";
        var json = $$"""
            {
              "schemaVersion": 1,
              "fromVersion": "0.2.0",
              "toVersion": "0.3.0",
              "rid": "win-x64",
              "baseInventorySha256": "{{ShaA}}",
              "{{arrayName}}": ["{{protectedPath}}"],
              "{{otherName}}": []
            }
            """;
        Assert.False(ReleaseDeltaManifest.TryRead(json, out _, out var problem));
        Assert.Contains("UserData", problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullReleasePackageVerifierRejectsUserDataFilesAndEmptyUserDataDirectory()
    {
        var exe = Encoding.UTF8.GetBytes("exe 0.3.0");
        var inventory = new ReleaseInventory(V3, "win-x64", [new ReleaseFile("YAT.exe", exe.LongLength, Sha(exe))]);
        var top = V3.PackageName("win-x64") + "/";

        // 1. Package containing a file under UserData/
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddEntry(archive, top + "YAT.exe", exe);
                AddEntry(archive, top + ReleaseInventory.FileName, inventory.ToJson());
                AddEntry(archive, top + "UserData/ImportProfiles/p.json", Encoding.UTF8.GetBytes("{}"));
            }

            ms.Position = 0;
            using var read = new ZipArchive(ms, ZipArchiveMode.Read);
            var contents = ReleasePackageVerifier.Read(read, V3, "win-x64", verifyContents: true);
            Assert.False(contents.IsValid);
            Assert.Contains(contents.Problems, p => p.Contains("protected user data", StringComparison.OrdinalIgnoreCase));
        }

        // 2. Package containing an empty UserData/ directory entry
        using (var ms = new MemoryStream())
        {
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                AddEntry(archive, top + "YAT.exe", exe);
                AddEntry(archive, top + ReleaseInventory.FileName, inventory.ToJson());
                archive.CreateEntry(top + "UserData/");
            }

            ms.Position = 0;
            using var read = new ZipArchive(ms, ZipArchiveMode.Read);
            var contents = ReleasePackageVerifier.Read(read, V3, "win-x64", verifyContents: true);
            Assert.False(contents.IsValid);
            Assert.Contains(contents.Problems, p => p.Contains("protected user data", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void DeltaPackageVerifierRejectsUserDataEntryInArchive()
    {
        var exe = Encoding.UTF8.GetBytes("exe 0.3.0");
        var targetInventory = new ReleaseInventory(V3, "win-x64", [new ReleaseFile("YAT.exe", exe.LongLength, Sha(exe))]);
        var delta = new ReleaseDeltaManifest(V2, V3, "win-x64", ShaA, ["YAT.exe"], []);
        var top = V3.PackageName("win-x64") + "/";

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, top + ReleaseDeltaManifest.FileName, delta.ToJson());
            AddEntry(archive, top + ReleaseInventory.FileName, targetInventory.ToJson());
            AddEntry(archive, top + "YAT.exe", exe);
            AddEntry(archive, top + "userdata/ImportProfiles/p.json", Encoding.UTF8.GetBytes("{}"));
        }

        ms.Position = 0;
        using var read = new ZipArchive(ms, ZipArchiveMode.Read);
        var contents = ReleasePackageVerifier.ReadDelta(read, V2, V3, "win-x64", ShaA, verifyContents: true);
        Assert.False(contents.IsValid);
        Assert.Contains(contents.Problems, p => p.Contains("protected user data", StringComparison.OrdinalIgnoreCase));
    }

    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private static void AddEntry(ZipArchive archive, string name, byte[] data)
    {
        using var stream = archive.CreateEntry(name).Open();
        stream.Write(data);
    }
}
