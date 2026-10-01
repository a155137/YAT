using System.IO.Compression;
using System.Text;
using YAT.Application.Distribution;
using YAT.Infrastructure.Distribution;

namespace YAT.Infrastructure.Tests;

// A YAT release set on disk (Task #051), made in a folder of the test's own: the inventory of a release folder, the
// zip as build/publish.ps1 makes it, its checksum and manifest - and the verification that finds every way the set can
// disagree with itself: a file changed, added or missing, a checksum or manifest of another zip, another version, an
// unsafe entry, user settings.
public sealed class ReleaseArtifactsTests : IDisposable
{
    private static readonly ReleaseVersion Version = ReleaseVersion.Parse("0.2.0");
    private const string Rid = "win-x64";
    private const string BaseUrl = "https://releases.example.test/yat/v0.2.0/";

    private readonly string _output = Path.Combine(Path.GetTempPath(), "yat-tests", Guid.NewGuid().ToString("N"));

    public ReleaseArtifactsTests() => Directory.CreateDirectory(_output);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_output, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Name => Version.PackageName(Rid);

    private string ReleaseFolder => Path.Combine(_output, Name);

    private string Zip => Path.Combine(_output, Name + ".zip");

    private string ManifestPath => Path.Combine(_output, ReleaseManifest.FileName);

    // A small release folder: a program, a library, notices in a sub folder, a file with a space and CJK in its name.
    private void MakeReleaseFolder()
    {
        Directory.CreateDirectory(Path.Combine(ReleaseFolder, "licenses", "SkiaSharp"));
        File.WriteAllText(Path.Combine(ReleaseFolder, "YAT.exe"), "program");
        File.WriteAllText(Path.Combine(ReleaseFolder, "YAT.dll"), "library");
        File.WriteAllText(Path.Combine(ReleaseFolder, "licenses", "SkiaSharp", "LICENSE.txt"), "MIT");
        File.WriteAllText(Path.Combine(ReleaseFolder, "讀 我.txt"), "說明");
    }

    // The zip as build/publish.ps1 writes it: one top folder, '/' separated entries in ordinal order.
    private void MakeZip(Action<ZipArchive>? also = null)
    {
        File.Delete(Zip);
        using var archive = ZipFile.Open(Zip, ZipArchiveMode.Create);
        foreach (var file in Directory.EnumerateFiles(ReleaseFolder, "*", SearchOption.AllDirectories)
                     .Select(path => (Path: path, Entry: Name + "/" + Path.GetRelativePath(ReleaseFolder, path).Replace('\\', '/')))
                     .OrderBy(file => file.Entry, StringComparer.Ordinal))
        {
            archive.CreateEntryFromFile(file.Path, file.Entry);
        }

        also?.Invoke(archive);
    }

    // The whole set, made the way build/publish.ps1 makes it.
    private void MakeRelease(bool withManifest = true)
    {
        MakeReleaseFolder();
        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));
        MakeZip();
        ReleaseArtifacts.WriteChecksum(Zip);
        if (withManifest)
        {
            ReleaseArtifacts.WriteManifest(ManifestPath, ReleaseArtifacts.CreateManifest(Version, Rid, Zip, BaseUrl, null, DateTimeOffset.UnixEpoch));
        }
    }

    private IReadOnlyList<string> Verify(bool requireManifest = false) => ReleaseArtifacts.Verify(_output, Version, Rid, requireManifest);

    private static void AddEntry(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    // ---- Making the set ----

    [Fact]
    public void TheInventoryListsEveryFileWithItsSizeAndSha256()
    {
        MakeReleaseFolder();

        var inventory = ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid);

        Assert.Equal(["YAT.dll", "YAT.exe", "licenses/SkiaSharp/LICENSE.txt", "讀 我.txt"], inventory.Files.Select(file => file.Path));
        var exe = inventory.Files.Single(file => file.Path == "YAT.exe");
        Assert.Equal(7, exe.Size);
        Assert.Equal(ReleaseArtifacts.Sha256(Path.Combine(ReleaseFolder, "YAT.exe")), exe.Sha256);
    }

    [Fact]
    public void TheInventoryIsTheSameEveryTime()
    {
        MakeReleaseFolder();

        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));
        var first = File.ReadAllBytes(Path.Combine(ReleaseFolder, ReleaseInventory.FileName));
        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));

        Assert.Equal(first, File.ReadAllBytes(Path.Combine(ReleaseFolder, ReleaseInventory.FileName)));
    }

    [Theory]
    [InlineData("graph-palettes.json")]
    [InlineData("graph-palettes.corrupt-20261002-093015.json")]
    [InlineData("graph-palettes.json.0123abcd.tmp")]
    public void AReleaseFolderWithUserSettingsIsRefused(string settings)
    {
        MakeReleaseFolder();
        File.WriteAllText(Path.Combine(ReleaseFolder, "licenses", settings), "{}");

        var exception = Assert.Throws<InvalidOperationException>(() => ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));
        Assert.Contains("user settings", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheChecksumFileIsTheZipsSha256AndName()
    {
        MakeRelease();

        Assert.Equal($"{ReleaseArtifacts.Sha256(Zip)}  {Name}.zip\n", File.ReadAllText(Zip + ".sha256"));
    }

    [Fact]
    public void TheManifestIsTheZipsVersionUrlSha256AndSize()
    {
        MakeRelease();

        var manifest = ReleaseManifestReader.Read(File.ReadAllText(ManifestPath)).Manifest!;
        var package = Assert.Single(manifest.Packages);
        Assert.Equal(Version, manifest.Version);
        Assert.Equal((Rid, BaseUrl + Name + ".zip"), (package.Rid, package.Url.AbsoluteUri));
        Assert.Equal(ReleaseArtifacts.Sha256(Zip), package.Sha256);
        Assert.Equal(new FileInfo(Zip).Length, package.Size);
    }

    [Theory]
    [InlineData("https://releases.example.test/yat/v0.2.0")]
    [InlineData("https://releases.example.test/yat/v0.2.0/")]
    public void TheBaseUrlMayEndWithOrWithoutASlash(string baseUrl)
    {
        MakeRelease(withManifest: false);

        var manifest = ReleaseArtifacts.CreateManifest(Version, Rid, Zip, baseUrl, "https://releases.example.test/notes", DateTimeOffset.UnixEpoch);

        Assert.Equal("https://releases.example.test/yat/v0.2.0/YAT-v0.2.0-win-x64.zip", manifest.Packages[0].Url.AbsoluteUri);
        Assert.Equal("https://releases.example.test/notes", manifest.ReleaseNotesUrl!.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://releases.example.test/yat/")]
    [InlineData("releases.example.test/yat/")]
    [InlineData("")]
    public void ABaseUrlThatIsNotHttpsIsRefused(string baseUrl)
    {
        MakeRelease(withManifest: false);

        Assert.Throws<ArgumentException>(() => ReleaseArtifacts.CreateManifest(Version, Rid, Zip, baseUrl, null, DateTimeOffset.UnixEpoch));
        Assert.Throws<ArgumentException>(() => ReleaseArtifacts.CreateManifest(Version, Rid, Zip, BaseUrl, "http://x.test/notes", DateTimeOffset.UnixEpoch));
    }

    // ---- Verifying the set ----

    [Fact]
    public void AConsistentSetHasNoProblems()
    {
        MakeRelease();

        Assert.Empty(Verify(requireManifest: true));
    }

    [Fact]
    public void ASetWithoutAManifestIsConsistentUnlessOneIsRequired()
    {
        MakeRelease(withManifest: false);

        Assert.Empty(Verify());
        Assert.Contains("yat-update.json", Assert.Single(Verify(requireManifest: true)), StringComparison.Ordinal);
    }

    [Fact]
    public void AZipOfAnotherVersionIsNotThisRelease()
    {
        MakeRelease();

        var problems = ReleaseArtifacts.Verify(_output, ReleaseVersion.Parse("0.2.1"), Rid, requireManifest: true);

        Assert.Contains("YAT-v0.2.1-win-x64.zip", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void AChecksumOfAnotherZipIsFound()
    {
        MakeRelease();
        File.WriteAllText(Zip + ".sha256", $"{new string('0', 64)}  {Name}.zip\n");

        Assert.Contains("is not the SHA-256", Assert.Single(Verify()), StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestOfAnotherVersionIsFound()
    {
        MakeRelease();
        File.WriteAllText(ManifestPath, File.ReadAllText(ManifestPath).Replace("\"0.2.0\"", "\"0.3.0\"", StringComparison.Ordinal));

        Assert.Contains("version 0.3.0", Assert.Single(Verify()), StringComparison.Ordinal);
    }

    [Fact]
    public void AManifestOfAnotherZipIsFound()
    {
        MakeRelease();
        var json = File.ReadAllText(ManifestPath);
        var sha = ReleaseArtifacts.Sha256(Zip);
        File.WriteAllText(
            ManifestPath,
            json.Replace(sha, new string('a', 64), StringComparison.Ordinal)
                .Replace($"\"size\": {new FileInfo(Zip).Length}", "\"size\": 1", StringComparison.Ordinal)
                .Replace($"{Name}.zip", "YAT-v0.2.0-win-arm64.zip", StringComparison.Ordinal));

        var problems = Verify();

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("SHA-256", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("size", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("points to", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInvalidManifestIsFound()
    {
        MakeRelease();
        File.WriteAllText(ManifestPath, "{ broken");

        Assert.Contains("not a valid manifest", Assert.Single(Verify()), StringComparison.Ordinal);
    }

    [Fact]
    public void AFileChangedAfterTheInventoryIsFound()
    {
        MakeReleaseFolder();
        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));
        File.WriteAllText(Path.Combine(ReleaseFolder, "YAT.dll"), "library, changed");
        MakeZip();
        ReleaseArtifacts.WriteChecksum(Zip);

        Assert.Contains("YAT.dll is not the file its inventory lists", Assert.Single(Verify()), StringComparison.Ordinal);
    }

    [Fact]
    public void FilesAddedOrMissingAfterTheInventoryAreFound()
    {
        MakeReleaseFolder();
        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));
        File.Delete(Path.Combine(ReleaseFolder, "YAT.dll"));
        File.WriteAllText(Path.Combine(ReleaseFolder, "extra.dll"), "x");
        MakeZip();
        ReleaseArtifacts.WriteChecksum(Zip);

        var problems = Verify();

        Assert.Equal(2, problems.Count);
        Assert.Contains(problems, problem => problem.Contains("no YAT.dll", StringComparison.Ordinal));
        Assert.Contains(problems, problem => problem.Contains("extra.dll, which its inventory does not list", StringComparison.Ordinal));
    }

    [Fact]
    public void AZipWithoutAnInventoryIsFound()
    {
        MakeReleaseFolder();
        MakeZip();
        ReleaseArtifacts.WriteChecksum(Zip);

        Assert.Contains("no yat-files.json", Assert.Single(Verify()), StringComparison.Ordinal);
    }

    [Fact]
    public void AnInventoryOfAnotherVersionIsFound()
    {
        MakeReleaseFolder();
        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, ReleaseVersion.Parse("0.1.0"), Rid));
        MakeZip();
        ReleaseArtifacts.WriteChecksum(Zip);

        Assert.Contains("inventory is 0.1.0", Assert.Single(Verify()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("YAT-v0.2.0-win-x64/../evil.dll")]
    [InlineData("YAT-v0.2.0-win-x64/sub/../../evil.dll")]
    [InlineData("other/evil.dll")]
    [InlineData("/evil.dll")]
    [InlineData("YAT-v0.2.0-win-x64/C:evil.dll")]
    [InlineData(@"YAT-v0.2.0-win-x64\evil.dll")]
    public void AnEntryOutsideTheReleaseOrUnsafeIsFound(string entry)
    {
        MakeReleaseFolder();
        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));
        MakeZip(archive => AddEntry(archive, entry, "x"));
        ReleaseArtifacts.WriteChecksum(Zip);

        Assert.NotEmpty(Verify());
    }

    [Fact]
    public void UserSettingsInTheZipAreFound()
    {
        MakeReleaseFolder();
        ReleaseArtifacts.WriteInventory(ReleaseFolder, ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid));
        MakeZip(archive => AddEntry(archive, Name + "/graph-palettes.json", "{}"));
        ReleaseArtifacts.WriteChecksum(Zip);

        var problems = Verify();

        Assert.Contains(problems, problem => problem.Contains("user settings", StringComparison.Ordinal));
    }

    [Fact]
    public void TheZipsInventoryIsTheReleaseFolders()
    {
        MakeRelease();

        using var archive = ZipFile.OpenRead(Zip);
        using var reader = new StreamReader(archive.GetEntry($"{Name}/{ReleaseInventory.FileName}")!.Open(), Encoding.UTF8);
        Assert.True(ReleaseInventory.TryRead(reader.ReadToEnd(), out var inventory, out _));
        Assert.Equal(ReleaseArtifacts.Scan(ReleaseFolder, Version, Rid).Files, inventory!.Files);
    }
}
