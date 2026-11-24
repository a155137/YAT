using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using YAT.Application.Distribution;

namespace YAT.Application.Tests;

public sealed class DeltaDistributionContractTests
{
    private const string ShaA = "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a";
    private const string ShaB = "1111111111111111111111111111111111111111111111111111111111111111";

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    [Fact]
    public void ManifestWithValidDeltasRoundTripsAndOmitsDeltasWhenEmpty()
    {
        var v1 = ReleaseVersion.Parse("0.4.2");
        var v2 = ReleaseVersion.Parse("0.5.0");
        var full = new ReleasePackage("win-x64", new Uri("https://example.test/YAT-v0.5.0-win-x64.zip"), ShaA, 95_000_000);
        var delta = new ReleaseDeltaPackage("win-x64", v1, new Uri("https://example.test/YAT-v0.5.0-from-v0.4.2-win-x64-diff.zip"), ShaB, 800_000);

        var withoutDeltas = new ReleaseManifest(v2, [full]);
        var jsonNoDeltas = Encoding.UTF8.GetString(withoutDeltas.ToJson());
        Assert.DoesNotContain("\"deltas\"", jsonNoDeltas, StringComparison.Ordinal);

        var withDeltas = new ReleaseManifest(v2, [full], Deltas: [delta]);
        var jsonWithDeltas = Encoding.UTF8.GetString(withDeltas.ToJson());
        var parsed = ReleaseManifestReader.Read(jsonWithDeltas, strictDeltas: true);

        Assert.True(parsed.IsValid);
        Assert.Empty(parsed.DeltaProblems!);
        Assert.Equal(delta, parsed.Manifest!.DeltaFor("WIN-X64", v1));
        Assert.Null(parsed.Manifest.DeltaFor("win-x64", ReleaseVersion.Parse("0.4.1")));
    }

    [Fact]
    public void InvalidDeltasAreSafelyIgnoredInClientModeWhileInvalidFullPackageStillFails()
    {
        var json = $$"""
            {
              "schemaVersion": 1,
              "version": "0.5.0",
              "packages": [
                { "rid": "win-x64", "url": "https://example.test/full.zip", "sha256": "{{ShaA}}", "size": 1000 }
              ],
              "deltas": [
                { "rid": "win-x64", "baseVersion": "0.4.2", "url": "https://example.test/ok-diff.zip", "sha256": "{{ShaB}}", "size": 500 },
                { "rid": "win-x64", "baseVersion": "0.4.2", "url": "https://example.test/dup-diff.zip", "sha256": "{{ShaB}}", "size": 500 },
                { "rid": "win-x64", "baseVersion": "0.5.0", "url": "https://example.test/same-ver.zip", "sha256": "{{ShaB}}", "size": 500 },
                { "rid": "win-x64", "baseVersion": "0.4.1", "url": "http://example.test/insecure.zip", "sha256": "{{ShaB}}", "size": 500 },
                { "rid": "win-x64", "baseVersion": "0.4.0", "url": "https://example.test/zero.zip", "sha256": "{{ShaB}}", "size": 0 }
              ]
            }
            """;

        // Client mode: keeps valid Full package + valid first delta, ignores invalid/duplicate deltas
        var clientResult = ReleaseManifestReader.Read(json);
        Assert.True(clientResult.IsValid);
        Assert.NotNull(clientResult.Manifest!.PackageFor("win-x64"));
        var keptDelta = Assert.Single(clientResult.Manifest.DeltaPackages);
        Assert.Equal(ReleaseVersion.Parse("0.4.2"), keptDelta.BaseVersion);
        Assert.Equal(4, clientResult.DeltaProblems!.Count);

        // Strict mode (publish verification): rejects invalid deltas
        var strictResult = ReleaseManifestReader.Read(json, strictDeltas: true);
        Assert.False(strictResult.IsValid);
        Assert.Equal(ReleaseManifestStatus.Invalid, strictResult.Status);

        // Invalid Full package still fails even if deltas are valid
        var badFullJson = $$"""
            {
              "schemaVersion": 1,
              "version": "0.5.0",
              "packages": [],
              "deltas": [
                { "rid": "win-x64", "baseVersion": "0.4.2", "url": "https://example.test/ok-diff.zip", "sha256": "{{ShaB}}", "size": 500 }
              ]
            }
            """;
        var badFullResult = ReleaseManifestReader.Read(badFullJson);
        Assert.False(badFullResult.IsValid);
    }

    [Fact]
    public void DeltaManifestContractRoundTripsAndRejectsUnsafeOrOverlappingPaths()
    {
        var from = ReleaseVersion.Parse("0.4.2");
        var to = ReleaseVersion.Parse("0.5.0");
        var valid = new ReleaseDeltaManifest(from, to, "win-x64", ShaA, ["YAT.dll", "YAT.exe"], ["old.dll"]);

        var json = Encoding.UTF8.GetString(valid.ToJson());
        Assert.True(ReleaseDeltaManifest.TryRead(json, out var parsed, out var problem), problem);
        Assert.Equal(from, parsed!.FromVersion);
        Assert.Equal(to, parsed.ToVersion);
        Assert.Equal(ShaA, parsed.BaseInventorySha256);
        Assert.Equal(["YAT.dll", "YAT.exe"], parsed.ChangedFiles);
        Assert.Equal(["old.dll"], parsed.RemovedFiles);

        Assert.Throws<ArgumentException>(() => new ReleaseDeltaManifest(to, from, "win-x64", ShaA, ["YAT.dll"], []));
        Assert.Throws<ArgumentException>(() => new ReleaseDeltaManifest(from, to, "win-x64", ShaA, ["../evil.dll"], []));
        Assert.Throws<ArgumentException>(() => new ReleaseDeltaManifest(from, to, "win-x64", ShaA, [".yat-update/updater/YAT.Updater.exe"], []));
        Assert.Throws<ArgumentException>(() => new ReleaseDeltaManifest(from, to, "win-x64", ShaA, ["graph-palettes.json"], []));
        Assert.Throws<ArgumentException>(() => new ReleaseDeltaManifest(from, to, "win-x64", ShaA, ["yat-files.json"], []));
        Assert.Throws<ArgumentException>(() => new ReleaseDeltaManifest(from, to, "win-x64", ShaA, ["YAT.dll"], ["yat.dll"]));
    }

    [Fact]
    public void VerifierRejectsOversizedEntryCountOversizedInventoryAndSymlinks()
    {
        var v = ReleaseVersion.Parse("0.5.0");

        // Entry count > MaximumEntryCount (4096)
        using (var tooManyBuffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(tooManyBuffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var i = 0; i <= ReleasePackageVerifier.MaximumEntryCount; i++)
                {
                    archive.CreateEntry($"YAT-v0.5.0-win-x64/f{i}.txt");
                }
            }

            tooManyBuffer.Position = 0;
            using var readArchive = new ZipArchive(tooManyBuffer, ZipArchiveMode.Read);
            var contents = ReleasePackageVerifier.Read(readArchive, v, "win-x64");
            Assert.False(contents.IsValid);
            Assert.Contains(contents.Problems, p => p.Contains("exceeding the limit", StringComparison.Ordinal));
        }

        // Streaming inventory size limit (> 16 MB)
        using (var bombBuffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(bombBuffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("YAT-v0.5.0-win-x64/yat-files.json", CompressionLevel.Fastest);
                using var stream = entry.Open();
                var zeros = new byte[1024 * 1024];
                for (var i = 0; i < 17; i++)
                {
                    stream.Write(zeros);
                }
            }

            bombBuffer.Position = 0;
            using var readArchive = new ZipArchive(bombBuffer, ZipArchiveMode.Read);
            var contents = ReleasePackageVerifier.Read(readArchive, v, "win-x64");
            Assert.False(contents.IsValid);
            Assert.Contains(contents.Problems, p => p.Contains("16 MB", StringComparison.Ordinal));
        }

        // Unix symlink external attributes
        using (var linkBuffer = new MemoryStream())
        {
            using (var archive = new ZipArchive(linkBuffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                var linkEntry = archive.CreateEntry("YAT-v0.5.0-win-x64/link.dll");
                linkEntry.ExternalAttributes = 0xA000 << 16;
            }

            linkBuffer.Position = 0;
            using var readArchive = new ZipArchive(linkBuffer, ZipArchiveMode.Read);
            var contents = ReleasePackageVerifier.Read(readArchive, v, "win-x64");
            Assert.False(contents.IsValid);
            Assert.Contains(contents.Problems, p => p.Contains("symbolic link", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ReadDeltaValidatesDeltaArchiveStructureAndContents()
    {
        var baseVersion = ReleaseVersion.Parse("0.4.2");
        var targetVersion = ReleaseVersion.Parse("0.5.0");
        var yatDll = Encoding.UTF8.GetBytes("yat-0.5.0");
        var unchangedDll = Encoding.UTF8.GetBytes("duckdb-unchanged");
        var targetInventory = new ReleaseInventory(
            targetVersion,
            "win-x64",
            [
                new ReleaseFile("YAT.dll", yatDll.Length, Sha(yatDll)),
                new ReleaseFile("duckdb.dll", unchangedDll.Length, Sha(unchangedDll))
            ]);
        var deltaManifest = new ReleaseDeltaManifest(baseVersion, targetVersion, "win-x64", ShaA, ["YAT.dll"], ["obsolete.dll"]);

        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string relative, byte[] data)
            {
                using var s = archive.CreateEntry($"YAT-v0.5.0-win-x64/{relative}").Open();
                s.Write(data);
            }

            Add("yat-delta.json", deltaManifest.ToJson());
            Add("yat-files.json", targetInventory.ToJson());
            Add("YAT.dll", yatDll);
        }

        buffer.Position = 0;
        using var readArchive = new ZipArchive(buffer, ZipArchiveMode.Read);
        var valid = ReleasePackageVerifier.ReadDelta(readArchive, baseVersion, targetVersion, "win-x64", ShaA, verifyContents: true);
        Assert.True(valid.IsValid, string.Join("; ", valid.Problems));
        Assert.Equal(["YAT.dll"], valid.ChangedFiles.Select(f => f.File.Path));

        var wrongBaseSha = ReleasePackageVerifier.ReadDelta(readArchive, baseVersion, targetVersion, "win-x64", ShaB, verifyContents: false);
        Assert.False(wrongBaseSha.IsValid);
    }
}
