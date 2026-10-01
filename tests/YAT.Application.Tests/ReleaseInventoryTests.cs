using System.Text;
using YAT.Application.Distribution;

namespace YAT.Application.Tests;

// The release file inventory (Task #051): relative '/' paths only - never absolute, never "." or "..", never twice
// (ignoring case) - in ordinal order, so the same release always gives the same inventory, byte for byte.
public class ReleaseInventoryTests
{
    private static readonly ReleaseVersion Version = ReleaseVersion.Parse("0.2.0");
    private const string Sha = "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a";

    private static ReleaseFile File(string path, long size = 1) => new(path, size, Sha);

    [Theory]
    [InlineData("YAT.exe", "YAT.exe")]
    [InlineData("licenses/SkiaSharp/LICENSE.txt", "licenses/SkiaSharp/LICENSE.txt")]
    [InlineData(@"licenses\SkiaSharp\LICENSE.txt", "licenses/SkiaSharp/LICENSE.txt")]
    [InlineData("runtimes/win-x64/native/duckdb.dll", "runtimes/win-x64/native/duckdb.dll")]
    [InlineData("晶圓/讀我.txt", "晶圓/讀我.txt")]
    [InlineData("a b/c d.dll", "a b/c d.dll")]
    public void ARelativePathIsKeptWithSlashes(string path, string normalized)
    {
        Assert.Equal(normalized, ReleasePath.Normalize(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("/YAT.exe")]
    [InlineData(@"\YAT.exe")]
    [InlineData(@"C:\YAT\YAT.exe")]
    [InlineData("C:YAT.exe")]
    [InlineData(@"\\server\share\YAT.exe")]
    [InlineData("../YAT.exe")]
    [InlineData("licenses/../../YAT.exe")]
    [InlineData("./YAT.exe")]
    [InlineData("licenses//LICENSE.txt")]
    [InlineData("licenses/")]
    [InlineData("YAT.exe:stream")]
    [InlineData("YAT?.exe")]
    [InlineData("YAT.exe ")]
    [InlineData("YAT.")]
    [InlineData("a\0b")]
    public void APathOutsideOrUnsafeIsRefused(string? path)
    {
        Assert.Null(ReleasePath.Normalize(path));
    }

    [Fact]
    public void FilesAreListedInOrdinalOrderWhateverOrderTheyCameIn()
    {
        var files = new[] { "b.dll", "YAT.exe", "a/z.txt", "a.dll", "B.txt", "licenses/x/LICENSE" };

        var forward = new ReleaseInventory(Version, "win-x64", files.Select(path => File(path)));
        var backward = new ReleaseInventory(Version, "win-x64", files.Reverse().Select(path => File(path)));

        Assert.Equal(["B.txt", "YAT.exe", "a.dll", "a/z.txt", "b.dll", "licenses/x/LICENSE"], forward.Files.Select(file => file.Path));
        Assert.Equal(forward.ToJson(), backward.ToJson());
    }

    [Fact]
    public void APathListedTwiceIgnoringCaseIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ReleaseInventory(Version, "win-x64", [File("YAT.exe"), File("yat.EXE")]));
    }

    [Fact]
    public void APathNotWrittenTheOneWayIsRefused()
    {
        Assert.Throws<ArgumentException>(() => new ReleaseInventory(Version, "win-x64", [File(@"licenses\LICENSE")]));
        Assert.Throws<ArgumentException>(() => new ReleaseInventory(Version, "win-x64", [File("../YAT.exe")]));
    }

    [Fact]
    public void TheInventoryNeverListsItself()
    {
        Assert.Throws<ArgumentException>(() => new ReleaseInventory(Version, "win-x64", [File("yat-files.json")]));
    }

    [Fact]
    public void AFileNeedsASizeAndALowerCaseSha256()
    {
        Assert.Throws<ArgumentException>(() => new ReleaseInventory(Version, "win-x64", [File("YAT.exe", size: -1)]));
        Assert.Throws<ArgumentException>(() => new ReleaseInventory(Version, "win-x64", [new ReleaseFile("YAT.exe", 1, Sha.ToUpperInvariant())]));
        Assert.Throws<ArgumentException>(() => new ReleaseInventory(Version, "win-x64", [new ReleaseFile("YAT.exe", 1, "abc")]));
    }

    [Fact]
    public void AWrittenInventoryReadsBackAsItWas()
    {
        var inventory = new ReleaseInventory(Version, "win-x64", [File("YAT.exe", 123), File("licenses/a/LICENSE", 0)]);

        Assert.True(ReleaseInventory.TryRead(Encoding.UTF8.GetString(inventory.ToJson()), out var read, out var problem));

        Assert.Null(problem);
        Assert.Equal(inventory.Version, read!.Version);
        Assert.Equal("win-x64", read.Rid);
        Assert.Equal(inventory.Files, read.Files);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("""{ "schemaVersion": 2, "version": "0.2.0", "rid": "win-x64", "files": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2", "rid": "win-x64", "files": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2.0", "files": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2.0", "rid": "win-x64" }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2.0", "rid": "win-x64", "files": [ { "path": "../x", "size": 1, "sha256": "0b2cc160b7493cc048cad79dafcbdba0eaae6115f1be92e6e6aec25baa46be1a" } ] }""")]
    [InlineData("""{ "schemaVersion": 1, "version": "0.2.0", "rid": "win-x64", "files": [ { "path": "x", "size": 1 } ] }""")]
    public void TextThatIsNoInventoryIsRefusedWithAReason(string json)
    {
        Assert.False(ReleaseInventory.TryRead(json, out var inventory, out var problem));
        Assert.Null(inventory);
        Assert.False(string.IsNullOrEmpty(problem));
    }
}
