using System.Text;
using YAT.Application.Distribution;

namespace YAT.Application.Tests;

// The release contract's pure rules (Task #051): a stable Major.Minor.Patch version, the release manifest - read
// forgivingly for what it does not know, strictly for what it must say, and a newer schema told apart from a broken
// manifest - and the release file inventory, deterministic and with safe paths only.
public class ReleaseManifestTests
{
    private const string Sha = "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a";

    private static string Manifest(string packages = $$"""[ { "rid": "win-x64", "url": "https://example.test/YAT-v0.2.0-win-x64.zip", "sha256": "{{Sha}}", "size": 123 } ]""", string extra = "") =>
        $$"""{ "schemaVersion": 1, "version": "0.2.0"{{extra}}, "packages": {{packages}} }""";

    private static string Package(string rid = "win-x64", string url = "https://example.test/p.zip", string sha = Sha, string size = "123") =>
        $$"""{ "rid": "{{rid}}", "url": "{{url}}", "sha256": "{{sha}}", "size": {{size}} }""";

    private static ReleaseManifestReadResult Read(string json) => ReleaseManifestReader.Read(json);

    // ---- Versions ----

    [Theory]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData("1.0.0", 1, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void AStableVersionIsRead(string text, int major, int minor, int patch)
    {
        Assert.True(ReleaseVersion.TryParse(text, out var version));
        Assert.Equal(new ReleaseVersion(major, minor, patch), version);
        Assert.Equal(text, version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("0.2")]
    [InlineData("0.2.0.0")]
    [InlineData("0.2.0-beta.1")]
    [InlineData("0.2.0+abc")]
    [InlineData("v0.2.0")]
    [InlineData("00.2.0")]
    [InlineData("0.02.0")]
    [InlineData("0.2.x")]
    [InlineData(" 0.2.0")]
    [InlineData("-1.2.0")]
    [InlineData("1234567890.0.0")]
    [InlineData(null)]
    public void AnythingElseIsNotAStableVersion(string? text)
    {
        Assert.False(ReleaseVersion.TryParse(text, out _));
    }

    [Fact]
    public void VersionsCompareByMajorMinorPatch()
    {
        var versions = new[] { "1.0.0", "0.10.0", "0.2.1", "0.2.0", "0.1.9" }.Select(ReleaseVersion.Parse).Order().Select(v => v.ToString());

        Assert.Equal(["0.1.9", "0.2.0", "0.2.1", "0.10.0", "1.0.0"], versions);
        Assert.True(ReleaseVersion.Parse("0.2.0") > ReleaseVersion.Parse("0.1.0"));
        Assert.True(ReleaseVersion.Parse("0.2.0") == ReleaseVersion.Parse("0.2.0"));
    }

    [Fact]
    public void AVersionNamesItsTagFileVersionAndPackage()
    {
        var version = ReleaseVersion.Parse("0.2.0");

        Assert.Equal("v0.2.0", version.Tag);
        Assert.Equal("0.2.0.0", version.FileVersion);
        Assert.Equal("YAT-v0.2.0-win-x64", version.PackageName("win-x64"));
    }

    // ---- The manifest ----

    [Fact]
    public void AValidManifestIsRead()
    {
        var result = Read(Manifest());

        Assert.Equal(ReleaseManifestStatus.Valid, result.Status);
        Assert.Empty(result.Problems);
        var manifest = result.Manifest!;
        Assert.Equal(ReleaseVersion.Parse("0.2.0"), manifest.Version);
        var package = Assert.Single(manifest.Packages);
        Assert.Equal(("win-x64", "https://example.test/YAT-v0.2.0-win-x64.zip", Sha, 123L), (package.Rid, package.Url.AbsoluteUri, package.Sha256, package.Size));
        Assert.Null(manifest.PublishedAt);
        Assert.Null(manifest.ReleaseNotesUrl);
        Assert.Same(package, manifest.PackageFor("WIN-X64"));
        Assert.Null(manifest.PackageFor("win-arm64"));
    }

    [Fact]
    public void OptionalFieldsAreReadWhenGiven()
    {
        var result = Read(Manifest(extra: """, "publishedAt": "2026-10-02T08:30:00Z", "releaseNotesUrl": "https://example.test/notes" """));

        Assert.True(result.IsValid);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.Zero), result.Manifest!.PublishedAt);
        Assert.Equal("https://example.test/notes", result.Manifest.ReleaseNotesUrl!.AbsoluteUri);
    }

    [Fact]
    public void NullOptionalFieldsAreAbsent()
    {
        Assert.True(Read(Manifest(extra: """, "publishedAt": null, "releaseNotesUrl": null """)).IsValid);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var result = Read($$"""
            { "schemaVersion": 1, "version": "0.2.0", "channel": "stable", "mandatory": false, "signature": { "x": 1 },
              "packages": [ { "rid": "win-x64", "url": "https://example.test/p.zip", "sha256": "{{Sha}}", "size": 1, "kind": "zip" } ] }
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void TheSha256IsKeptInLowerCase()
    {
        var result = Read(Manifest($"[ {Package(sha: Sha.ToUpperInvariant())} ]"));

        Assert.Equal(Sha, result.Manifest!.Packages[0].Sha256);
    }

    [Fact]
    public void SeveralRuntimesAreRead()
    {
        var result = Read(Manifest($"[ {Package()}, {Package(rid: "win-arm64")} ]"));

        Assert.Equal(["win-x64", "win-arm64"], result.Manifest!.Packages.Select(package => package.Rid));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"manifest\"")]
    public void TextThatIsNoManifestIsInvalid(string json)
    {
        var result = Read(json);

        Assert.Equal(ReleaseManifestStatus.Invalid, result.Status);
        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Problems);
    }

    [Theory]
    [InlineData("""{ "version": "0.2.0", "packages": [] }""")]
    [InlineData("""{ "schemaVersion": "1", "version": "0.2.0", "packages": [] }""")]
    [InlineData("""{ "schemaVersion": 0, "version": "0.2.0", "packages": [] }""")]
    [InlineData("""{ "schemaVersion": 1.5, "version": "0.2.0", "packages": [] }""")]
    public void AManifestWithoutAUsableSchemaVersionIsInvalid(string json)
    {
        Assert.Equal(ReleaseManifestStatus.Invalid, Read(json).Status);
    }

    [Fact]
    public void ANewerSchemaIsToldApartFromABrokenManifest()
    {
        var result = Read("""{ "schemaVersion": 2, "whatever": "a later YAT knows" }""");

        Assert.Equal(ReleaseManifestStatus.NewerSchema, result.Status);
        Assert.Equal(2, result.SchemaVersion);
        Assert.Null(result.Manifest);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "packages": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2.0" }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2.0", "packages": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2.0", "packages": {} }""")]
    public void AManifestMissingWhatItMustSayIsInvalid(string json)
    {
        var result = Read(json);

        Assert.Equal(ReleaseManifestStatus.Invalid, result.Status);
        Assert.NotEmpty(result.Problems);
    }

    [Theory]
    [InlineData("0.2.0-beta.1")]
    [InlineData("0.2")]
    [InlineData("v0.2.0")]
    public void AVersionThatIsNotStableIsInvalid(string version)
    {
        var result = Read(Manifest().Replace("\"0.2.0\"", $"\"{version}\"", StringComparison.Ordinal));

        Assert.Contains("stable", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ARuntimeListedTwiceIsInvalid()
    {
        var result = Read(Manifest($"[ {Package()}, {Package(rid: "WIN-X64", url: "https://example.test/other.zip")} ]"));

        Assert.Equal(ReleaseManifestStatus.Invalid, result.Status);
        Assert.Contains("listed twice", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.test/p.zip")]
    [InlineData("ftp://example.test/p.zip")]
    [InlineData("file:///C:/p.zip")]
    [InlineData("/relative/p.zip")]
    [InlineData("not a url")]
    public void APackageUrlThatIsNotHttpsIsInvalid(string url)
    {
        var result = Read(Manifest($"[ {Package(url: url)} ]"));

        Assert.Contains("https", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseNotesThatAreNotHttpsAreInvalid()
    {
        var result = Read(Manifest(extra: """, "releaseNotesUrl": "http://example.test/notes" """));

        Assert.Contains("releaseNotesUrl", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1")]
    [InlineData("0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1aa")]
    [InlineData("0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46bezz")]
    [InlineData("")]
    public void ASha256ThatIsNot64HexDigitsIsInvalid(string sha)
    {
        var result = Read(Manifest($"[ {Package(sha: sha)} ]"));

        Assert.Contains("sha256", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"123\"")]
    public void ASizeThatIsNotAboveZeroIsInvalid(string size)
    {
        var result = Read(Manifest($"[ {Package(size: size)} ]"));

        Assert.Contains("size", Assert.Single(result.Problems), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" win-x64")]
    public void APackageWithoutARuntimeIsInvalid(string rid)
    {
        Assert.Contains("runtime id", Assert.Single(Read(Manifest($"[ {Package(rid: rid)} ]")).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void APublishedAtThatIsNoDateIsInvalid()
    {
        Assert.Contains("publishedAt", Assert.Single(Read(Manifest(extra: """, "publishedAt": "yesterday" """)).Problems), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryProblemIsSaid()
    {
        var result = Read(Manifest($"[ {Package(url: "http://x.test/p.zip", sha: "x", size: "0")} ]").Replace("\"0.2.0\"", "\"0.2\"", StringComparison.Ordinal));

        Assert.Equal(4, result.Problems.Count);
    }

    [Fact]
    public void AWrittenManifestReadsBackAsItWas()
    {
        var manifest = new ReleaseManifest(
            ReleaseVersion.Parse("0.2.0"),
            [new ReleasePackage("win-x64", new Uri("https://example.test/v0.2.0/YAT-v0.2.0-win-x64.zip"), Sha, 63556342)],
            new DateTimeOffset(2026, 10, 2, 8, 30, 0, TimeSpan.Zero),
            new Uri("https://example.test/notes"));

        var json = Encoding.UTF8.GetString(manifest.ToJson());
        var read = Read(json).Manifest!;

        Assert.Equal(manifest.Version, read.Version);
        Assert.Equal(manifest.Packages, read.Packages);
        Assert.Equal(manifest.PublishedAt, read.PublishedAt);
        Assert.Equal(manifest.ReleaseNotesUrl, read.ReleaseNotesUrl);
        Assert.StartsWith("{\n  \"schemaVersion\": 1,\n  \"version\": \"0.2.0\"", json.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
    }
}
