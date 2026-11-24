using System.IO.Compression;
using System.Text;
using YAT.Application.Distribution;
using YAT.Infrastructure.Distribution;

namespace YAT.Infrastructure.Tests;

public sealed class DeltaReleaseArtifactsTests : IDisposable
{
    private const string Rid = "win-x64";
    private static readonly ReleaseVersion BaseVersion = ReleaseVersion.Parse("0.4.2");
    private static readonly ReleaseVersion TargetVersion = ReleaseVersion.Parse("0.4.3");
    private const string BaseUrl = "https://releases.example.test/yat/v0.4.3/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "yat-delta-artifacts", Guid.NewGuid().ToString("N"));

    public DeltaReleaseArtifactsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private (string ZipPath, string Sha256) BuildReleaseZip(ReleaseVersion version, Dictionary<string, string> files, string? ridOverride = null)
    {
        var rid = ridOverride ?? Rid;
        var name = version.PackageName(rid);
        var folder = Path.Combine(_root, name);
        var zipPath = Path.Combine(_root, name + ".zip");
        Directory.CreateDirectory(folder);

        foreach (var (relative, content) in files)
        {
            var full = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content, new UTF8Encoding(false));
        }

        var inventory = ReleaseArtifacts.Scan(folder, version, rid);
        ReleaseArtifacts.WriteInventory(folder, inventory);

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                         .Select(p => (Path: p, Entry: name + "/" + Path.GetRelativePath(folder, p).Replace('\\', '/')))
                         .OrderBy(x => x.Entry, StringComparer.Ordinal))
            {
                archive.CreateEntryFromFile(file.Path, file.Entry);
            }
        }

        var sha = ReleaseArtifacts.WriteChecksum(zipPath);
        return (zipPath, sha);
    }

    [Fact]
    public void GeneratesVerifiesAndPackagesDifferentialZipAlongsideFullZip()
    {
        var (baseZip, baseSha) = BuildReleaseZip(BaseVersion, new Dictionary<string, string>
        {
            ["YAT.exe"] = "exe-0.4.2",
            ["duckdb.dll"] = "duckdb-same",
            ["obsolete.dll"] = "removed-in-0.4.3"
        });

        var (targetZip, _) = BuildReleaseZip(TargetVersion, new Dictionary<string, string>
        {
            ["YAT.exe"] = "exe-0.4.3",
            ["duckdb.dll"] = "duckdb-same",
            ["new-feature.dll"] = "added-in-0.4.3"
        });

        var deltaZip = Path.Combine(_root, ReleaseArtifacts.DeltaPackageName(BaseVersion, TargetVersion, Rid));
        var deltaManifest = ReleaseArtifacts.CreateDeltaPackage(baseZip, BaseVersion, baseSha, targetZip, TargetVersion, Rid, deltaZip);

        Assert.Equal(["YAT.exe", "new-feature.dll"], deltaManifest.ChangedFiles);
        Assert.Equal(["obsolete.dll"], deltaManifest.RemovedFiles);

        // Kept file must NOT be inside the differential ZIP
        using (var archive = ZipFile.OpenRead(deltaZip))
        {
            var entries = archive.Entries.Select(e => e.FullName).ToList();
            Assert.DoesNotContain(entries, e => e.EndsWith("duckdb.dll", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(entries, e => e.EndsWith("obsolete.dll", StringComparison.OrdinalIgnoreCase));
            Assert.Contains($"YAT-v{TargetVersion}-win-x64/{ReleaseDeltaManifest.FileName}", entries);
            Assert.Contains($"YAT-v{TargetVersion}-win-x64/{ReleaseInventory.FileName}", entries);
        }

        var deltaEntry = ReleaseArtifacts.CreateDeltaManifestEntry(BaseVersion, Rid, deltaZip, BaseUrl);
        var manifest = ReleaseArtifacts.CreateManifest(TargetVersion, Rid, targetZip, BaseUrl, null, DateTimeOffset.UtcNow, [deltaEntry]);
        ReleaseArtifacts.WriteManifest(Path.Combine(_root, ReleaseManifest.FileName), manifest);

        var problems = ReleaseArtifacts.Verify(_root, TargetVersion, Rid, requireManifest: true, baseZip, baseSha);
        Assert.Empty(problems);
    }

    [Fact]
    public void RejectsBaseShaMismatchBaseVersionMismatchAndModifiedBaseFile()
    {
        var (baseZip, baseSha) = BuildReleaseZip(BaseVersion, new Dictionary<string, string>
        {
            ["YAT.exe"] = "exe-0.4.2",
            ["duckdb.dll"] = "duckdb-same"
        });
        var (targetZip, _) = BuildReleaseZip(TargetVersion, new Dictionary<string, string>
        {
            ["YAT.exe"] = "exe-0.4.3",
            ["duckdb.dll"] = "duckdb-same"
        });

        var deltaZip = Path.Combine(_root, ReleaseArtifacts.DeltaPackageName(BaseVersion, TargetVersion, Rid));

        // Base SHA mismatch
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseArtifacts.CreateDeltaPackage(baseZip, BaseVersion, new string('0', 64), targetZip, TargetVersion, Rid, deltaZip));

        // Base version >= Target version
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseArtifacts.CreateDeltaPackage(baseZip, TargetVersion, baseSha, targetZip, TargetVersion, Rid, deltaZip));

        // Base RID mismatch
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseArtifacts.CreateDeltaPackage(baseZip, BaseVersion, baseSha, targetZip, TargetVersion, "linux-x64", deltaZip));

        // Tamper with a file inside Base ZIP without updating its SHA-256
        var tamperedZip = Path.Combine(_root, "tampered-base.zip");
        File.Copy(baseZip, tamperedZip, overwrite: true);
        using (var archive = ZipFile.Open(tamperedZip, ZipArchiveMode.Update))
        {
            archive.GetEntry($"YAT-v{BaseVersion}-win-x64/duckdb.dll")!.Delete();
            using var s = archive.CreateEntry($"YAT-v{BaseVersion}-win-x64/duckdb.dll").Open();
            s.Write(Encoding.UTF8.GetBytes("corrupted"));
        }

        var tamperedSha = ReleaseArtifacts.Sha256(tamperedZip);
        Assert.Throws<InvalidOperationException>(() =>
            ReleaseArtifacts.CreateDeltaPackage(tamperedZip, BaseVersion, tamperedSha, targetZip, TargetVersion, Rid, deltaZip));
    }

    [Fact]
    public void ReconstructionVerificationCatchesMissingExtraCorruptedAndInvalidDeltaPayloads()
    {
        var (baseZip, baseSha) = BuildReleaseZip(BaseVersion, new Dictionary<string, string>
        {
            ["YAT.exe"] = "exe-0.4.2",
            ["duckdb.dll"] = "duckdb-same"
        });
        var (targetZip, _) = BuildReleaseZip(TargetVersion, new Dictionary<string, string>
        {
            ["YAT.exe"] = "exe-0.4.3",
            ["duckdb.dll"] = "duckdb-same"
        });

        var deltaZip = Path.Combine(_root, ReleaseArtifacts.DeltaPackageName(BaseVersion, TargetVersion, Rid));
        ReleaseArtifacts.CreateDeltaPackage(baseZip, BaseVersion, baseSha, targetZip, TargetVersion, Rid, deltaZip);

        // 1. Extra payload in delta ZIP
        var extraZip = Path.Combine(_root, "extra.zip");
        File.Copy(deltaZip, extraZip, overwrite: true);
        using (var a = ZipFile.Open(extraZip, ZipArchiveMode.Update))
        {
            using var s = a.CreateEntry($"YAT-v{TargetVersion}-win-x64/extra.dll").Open();
            s.Write(Encoding.UTF8.GetBytes("unexpected"));
        }

        Assert.NotEmpty(ReleaseArtifacts.VerifyDeltaReconstruction(baseZip, baseSha, extraZip, targetZip, BaseVersion, TargetVersion, Rid));

        // 2. Missing changed payload in delta ZIP
        var missingZip = Path.Combine(_root, "missing.zip");
        File.Copy(deltaZip, missingZip, overwrite: true);
        using (var a = ZipFile.Open(missingZip, ZipArchiveMode.Update))
        {
            a.GetEntry($"YAT-v{TargetVersion}-win-x64/YAT.exe")!.Delete();
        }

        Assert.NotEmpty(ReleaseArtifacts.VerifyDeltaReconstruction(baseZip, baseSha, missingZip, targetZip, BaseVersion, TargetVersion, Rid));

        // 3. Path traversal entry in delta ZIP
        var traversalZip = Path.Combine(_root, "traversal.zip");
        File.Copy(deltaZip, traversalZip, overwrite: true);
        using (var a = ZipFile.Open(traversalZip, ZipArchiveMode.Update))
        {
            using var s = a.CreateEntry($"YAT-v{TargetVersion}-win-x64/../evil.dll").Open();
            s.Write(Encoding.UTF8.GetBytes("evil"));
        }

        Assert.NotEmpty(ReleaseArtifacts.VerifyDeltaReconstruction(baseZip, baseSha, traversalZip, targetZip, BaseVersion, TargetVersion, Rid));

        // 4. Corrupted delta ZIP bytes
        var corruptZip = Path.Combine(_root, "corrupt.zip");
        File.WriteAllText(corruptZip, "not-a-zip");
        Assert.NotEmpty(ReleaseArtifacts.VerifyDeltaReconstruction(baseZip, baseSha, corruptZip, targetZip, BaseVersion, TargetVersion, Rid));
    }
}
