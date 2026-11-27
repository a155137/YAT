using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;
using YAT.Infrastructure.Distribution;
using YAT.Infrastructure.Updates;

namespace YAT.Infrastructure.Tests;

public sealed class UserDataPackagingTests : IDisposable
{
    private readonly string _root;
    private static readonly ReleaseVersion V2 = ReleaseVersion.Parse("0.2.0");
    private static readonly ReleaseVersion V3 = ReleaseVersion.Parse("0.3.0");

    public UserDataPackagingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "yat-userdata-pkg-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void ReleaseArtifactsScanRejectsUserDataFileAndPreservesOriginalFile()
    {
        var releaseDir = Path.Combine(_root, "release-with-file");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "YAT.exe"), "exe");

        var profilePath = Path.Combine(releaseDir, "UserData", "ImportProfiles", "profile.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        var originalBytes = Encoding.UTF8.GetBytes("""{"name":"keep-me"}""");
        File.WriteAllBytes(profilePath, originalBytes);

        var ex = Assert.Throws<InvalidOperationException>(() => ReleaseArtifacts.Scan(releaseDir, V3, "win-x64"));
        Assert.Contains("UserData", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Scan must fail closed without deleting or modifying the UserData file.
        Assert.True(File.Exists(profilePath));
        Assert.Equal(originalBytes, File.ReadAllBytes(profilePath));
    }

    [Fact]
    public void ReleaseArtifactsScanRejectsEmptyUserDataDirectory()
    {
        var releaseDir = Path.Combine(_root, "release-with-empty-dir");
        Directory.CreateDirectory(releaseDir);
        File.WriteAllText(Path.Combine(releaseDir, "YAT.exe"), "exe");
        var userDataDir = Path.Combine(releaseDir, "UserData");
        Directory.CreateDirectory(userDataDir);

        var ex = Assert.Throws<InvalidOperationException>(() => ReleaseArtifacts.Scan(releaseDir, V3, "win-x64"));
        Assert.Contains("UserData", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(userDataDir));
    }

    [Fact]
    public async Task LocalInstallationDeltaPreVerifierFailsClosedOnLegacyInventoryListingUserData()
    {
        var installDir = Path.Combine(_root, "legacy-install");
        Directory.CreateDirectory(installDir);

        var exeBytes = Encoding.UTF8.GetBytes("exe 0.2.0");
        File.WriteAllBytes(Path.Combine(installDir, "YAT.exe"), exeBytes);

        var profilePath = Path.Combine(installDir, "UserData", "ImportProfiles", "legacy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        var profileBytes = Encoding.UTF8.GetBytes("""{"id":"legacy"}""");
        File.WriteAllBytes(profilePath, profileBytes);

        var exeSha = Convert.ToHexStringLower(SHA256.HashData(exeBytes));
        var profileSha = Convert.ToHexStringLower(SHA256.HashData(profileBytes));
        var legacyInventoryJson = $$"""
            {
              "schemaVersion": 1,
              "version": "0.2.0",
              "rid": "win-x64",
              "files": [
                { "path": "UserData/ImportProfiles/legacy.json", "size": {{profileBytes.Length}}, "sha256": "{{profileSha}}" },
                { "path": "YAT.exe", "size": {{exeBytes.Length}}, "sha256": "{{exeSha}}" }
              ]
            }
            """;
        var legacyBytes = Encoding.UTF8.GetBytes(legacyInventoryJson);
        File.WriteAllBytes(ReleaseInstallation.InventoryPath(installDir), legacyBytes);

        var preVerifier = new LocalInstallationDeltaPreVerifier();
        Assert.False(preVerifier.IsLocalInventoryEligible(installDir, V2, "win-x64"));

        var targetInventory = new ReleaseInventory(V3, "win-x64", [new ReleaseFile("YAT.exe", exeBytes.LongLength, exeSha)]);
        var deltaManifest = new ReleaseDeltaManifest(V2, V3, "win-x64", Convert.ToHexStringLower(SHA256.HashData(legacyBytes)), ["YAT.exe"], []);

        var result = await preVerifier.VerifyLocalAsync(
            installDir,
            V2,
            "win-x64",
            deltaManifest,
            targetInventory,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsValid);
        Assert.Contains("UserData", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(profileBytes, File.ReadAllBytes(profilePath));
    }
}
