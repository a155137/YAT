using System.Globalization;
using System.Text;
using System.Text.Json;
using YAT.Application.Abstractions.Settings;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.Infrastructure.Settings;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

// The user's graph palettes in their JSON file (Task #050), always in a folder of the test's own - never the user's
// profile: forgiving reads palette by palette, a damaged file set aside rather than lost, a newer file never
// overwritten, and a save that either replaces the file whole or leaves the last good one exactly as it was.
public sealed class JsonGraphPaletteLibraryStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 30, 15, TimeSpan.Zero);

    private static readonly GraphPalette RedBlue = new([new GraphColor(0xD6, 0x27, 0x28), new GraphColor(0x1F, 0x77, 0xB4)]);
    private static readonly GraphPalette Green = new([new GraphColor(0x2C, 0xA0, 0x2C)]);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "yat-tests", Guid.NewGuid().ToString("N"), "YAT");
    private readonly FixedTimeProvider _time = new(Now);

    private string FilePath => Path.Combine(_directory, JsonGraphPaletteLibraryStore.FileName);

    private JsonGraphPaletteLibraryStore Store() => new(FilePath, _time);

    private void Write(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, json, new UTF8Encoding(false));
    }

    private string Stamp => _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

    private string[] OtherFiles() =>
        Directory.Exists(_directory)
            ? [.. Directory.GetFiles(_directory).Select(Path.GetFileName).Where(name => name != JsonGraphPaletteLibraryStore.FileName).Order()!]
            : [];

    public void Dispose()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.SetAttributes(FilePath, FileAttributes.Normal);
            }

            Directory.Delete(Path.GetDirectoryName(_directory)!, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static GraphPaletteLibrary Library()
    {
        var library = GraphPaletteLibrary.Empty.Add("Presentation", RedBlue).Library;
        var added = library.Add("Green", Green);
        return added.Library.SetDefault(added.PaletteId).Library;
    }

    private static string Palette(Guid id, string name, params string[] colors) =>
        $$"""{ "id": "{{id}}", "name": "{{name}}", "colors": [{{string.Join(", ", colors.Select(color => $"\"{color}\""))}}] }""";

    // ---- Where ----

    [Fact]
    public void TheFileIsInYatsFolderOfTheUsersRoamingApplicationData()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YAT", "graph-palettes.json"),
            JsonGraphPaletteLibraryStore.DefaultFilePath);
    }

    // ---- First run ----

    [Fact]
    public void WithoutAFileTheLibraryIsEmptyAndNothingIsWritten()
    {
        var loaded = Store().Load();

        Assert.Same(GraphPaletteLibrary.Empty, loaded.Library);
        Assert.False(loaded.IsReadOnly);
        Assert.Empty(loaded.Warnings);
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void LoadingAFileNeverWritesIt()
    {
        Write("""{ "schemaVersion": 1, "palettes": [], "extra": true }""");
        var before = File.GetLastWriteTimeUtc(FilePath);
        var bytes = File.ReadAllBytes(FilePath);

        Store().Load();

        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Equal(before, File.GetLastWriteTimeUtc(FilePath));
        Assert.Empty(OtherFiles());
    }

    // ---- Saving and loading ----

    [Fact]
    public void ASavedLibraryIsLoadedAsItWasAfterARestart()
    {
        var library = Library();

        Store().Save(library);
        var loaded = new JsonGraphPaletteLibraryStore(FilePath, _time).Load();

        Assert.False(loaded.IsReadOnly);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(library.DefaultPaletteId, loaded.Library.DefaultPaletteId);
        Assert.Equal(library.CustomPalettes, loaded.Library.CustomPalettes);
        Assert.Empty(OtherFiles());
    }

    [Fact]
    public void TheFileIsSchemaVersion1WithColoursWrittenAsHex()
    {
        var library = Library();

        Store().Save(library);

        using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(library.DefaultPaletteId.ToString(), root.GetProperty("defaultPaletteId").GetString());
        var first = root.GetProperty("palettes")[0];
        Assert.Equal(library.CustomPalettes[0].Id.ToString(), first.GetProperty("id").GetString());
        Assert.Equal("Presentation", first.GetProperty("name").GetString());
        Assert.Equal(["#D62728", "#1F77B4"], first.GetProperty("colors").EnumerateArray().Select(color => color.GetString()));
    }

    [Fact]
    public void YatDefaultIsWrittenAsANullDefault()
    {
        Store().Save(GraphPaletteLibrary.Empty.Add("A", RedBlue).Library);

        using var document = JsonDocument.Parse(File.ReadAllBytes(FilePath));
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("defaultPaletteId").ValueKind);
        Assert.Null(Store().Load().Library.DefaultPaletteId);
    }

    [Fact]
    public void SavingMakesTheFolderWhenItIsNotThere()
    {
        Store().Save(Library());

        Assert.True(File.Exists(FilePath));
    }

    [Fact]
    public void ASaveReplacesTheWholeFile()
    {
        var store = Store();
        store.Save(Library());

        store.Save(GraphPaletteLibrary.Empty);

        Assert.Empty(Store().Load().Library.CustomPalettes);
        Assert.Empty(OtherFiles());
    }

    // ---- Forgiving reads ----

    [Fact]
    public void MissingFieldsAreTheirDefaults()
    {
        Write("{}");

        var loaded = Store().Load();

        Assert.Empty(loaded.Library.CustomPalettes);
        Assert.Null(loaded.Library.DefaultPaletteId);
        Assert.False(loaded.IsReadOnly);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void AFileWithoutASchemaVersionIsReadAsVersion1()
    {
        var id = Guid.NewGuid();
        Write($$"""{ "palettes": [ {{Palette(id, "A", "#112233")}} ], "defaultPaletteId": "{{id}}" }""");

        var loaded = Store().Load();

        Assert.Equal(id, loaded.Library.DefaultPaletteId);
        Assert.False(loaded.IsReadOnly);
    }

    [Fact]
    public void UnknownFieldsAreIgnored()
    {
        var id = Guid.NewGuid();
        Write($$"""
            { "schemaVersion": 1, "theme": "dark", "defaultPaletteId": null,
              "palettes": [ { "id": "{{id}}", "name": "A", "colors": ["#112233"], "favourite": true, "notes": { "x": 1 } } ] }
            """);

        var loaded = Store().Load();

        Assert.Equal("A", Assert.Single(loaded.Library.CustomPalettes).Name);
        Assert.Empty(loaded.Warnings);
    }

    [Fact]
    public void NamesAreReadTrimmedAndColoursInEitherCase()
    {
        var id = Guid.NewGuid();
        Write($$"""{ "palettes": [ { "id": "{{id}}", "name": "  A  ", "colors": ["#a1b2c3", "D4E5F6"] } ] }""");

        var palette = Assert.Single(Store().Load().Library.CustomPalettes);

        Assert.Equal("A", palette.Name);
        Assert.Equal(new GraphPalette([new GraphColor(0xA1, 0xB2, 0xC3), new GraphColor(0xD4, 0xE5, 0xF6)]), palette.Palette);
    }

    [Fact]
    public void APaletteThatBreaksARuleIsLeftOutAndTheRestAreRead()
    {
        var good = Guid.NewGuid();
        var other = Guid.NewGuid();
        Write($$"""
            { "palettes": [
                {{Palette(Guid.NewGuid(), "Bad colour", "#112233", "red")}},
                {{Palette(Guid.NewGuid(), "No colours")}},
                {{Palette(Guid.NewGuid(), "Too many", [.. Enumerable.Repeat("#112233", 17)])}},
                { "name": "No id", "colors": ["#112233"] },
                { "id": "not a guid", "name": "Bad id", "colors": ["#112233"] },
                {{Palette(Guid.Empty, "Empty id", "#112233")}},
                { "id": "{{Guid.NewGuid()}}", "name": "Colours not a list", "colors": "#112233" },
                {{Palette(Guid.NewGuid(), "   ", "#112233")}},
                42,
                {{Palette(good, "Good", "#112233")}},
                {{Palette(other, "Sixteen", [.. Enumerable.Repeat("#445566", 16)])}}
            ] }
            """);

        var loaded = Store().Load();

        Assert.Equal([good, other], loaded.Library.CustomPalettes.Select(palette => palette.Id));
        Assert.Equal(9, loaded.Warnings.Count);
        Assert.All(loaded.Warnings, warning => Assert.Equal(GraphPaletteLoadWarningKind.PaletteSkipped, warning.Kind));
        Assert.Contains(loaded.Warnings, warning => warning.Detail.Contains("'red' is not a colour", StringComparison.Ordinal));
        Assert.False(loaded.IsReadOnly);
    }

    [Fact]
    public void AnIdReadBeforeWinsAndTheLaterPaletteIsLeftOut()
    {
        var id = Guid.NewGuid();
        Write($$"""{ "palettes": [ {{Palette(id, "First", "#111111")}}, {{Palette(id, "Second", "#222222")}} ] }""");

        var loaded = Store().Load();

        Assert.Equal("First", Assert.Single(loaded.Library.CustomPalettes).Name);
        Assert.Contains("id is another palette's", Assert.Single(loaded.Warnings).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ANameReadBeforeWinsWhateverItsCase()
    {
        Write($$"""{ "palettes": [ {{Palette(Guid.NewGuid(), "Report", "#111111")}}, {{Palette(Guid.NewGuid(), " REPORT ", "#222222")}} ] }""");

        var loaded = Store().Load();

        Assert.Equal(new GraphColor(0x11, 0x11, 0x11), Assert.Single(loaded.Library.CustomPalettes).Palette.Colors[0]);
        Assert.Contains("name is another palette's", Assert.Single(loaded.Warnings).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void APaletteNamedYatDefaultIsLeftOut()
    {
        Write($$"""{ "palettes": [ {{Palette(Guid.NewGuid(), "yat default", "#111111")}} ] }""");

        var loaded = Store().Load();

        Assert.Empty(loaded.Library.CustomPalettes);
        Assert.Contains("YAT Default", Assert.Single(loaded.Warnings).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultThatIsNotAmongThePalettesIsYatDefault()
    {
        var kept = Guid.NewGuid();
        Write($$"""{ "defaultPaletteId": "{{Guid.NewGuid()}}", "palettes": [ {{Palette(kept, "A", "#111111")}} ] }""");

        var loaded = Store().Load();

        Assert.Null(loaded.Library.DefaultPaletteId);
        Assert.Single(loaded.Library.CustomPalettes);
        Assert.Equal(GraphPaletteLoadWarningKind.DefaultPaletteMissing, Assert.Single(loaded.Warnings).Kind);
        Assert.False(loaded.IsReadOnly);
    }

    [Fact]
    public void ADefaultOfAPaletteThatWasLeftOutIsYatDefault()
    {
        var bad = Guid.NewGuid();
        Write($$"""{ "defaultPaletteId": "{{bad}}", "palettes": [ {{Palette(bad, "A", "nope")}} ] }""");

        var loaded = Store().Load();

        Assert.Null(loaded.Library.DefaultPaletteId);
        Assert.Contains(loaded.Warnings, warning => warning.Kind == GraphPaletteLoadWarningKind.DefaultPaletteMissing);
    }

    [Fact]
    public void ADefaultThatIsNoIdIsYatDefault()
    {
        Write("""{ "defaultPaletteId": 7, "palettes": [] }""");

        var loaded = Store().Load();

        Assert.Null(loaded.Library.DefaultPaletteId);
        Assert.Equal(GraphPaletteLoadWarningKind.DefaultPaletteMissing, Assert.Single(loaded.Warnings).Kind);
    }

    // ---- A file that is no palette library ----

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("\"palettes\"")]
    [InlineData("""{ "palettes": {} }""")]
    [InlineData("""{ "schemaVersion": "one" }""")]
    [InlineData("""{ "schemaVersion": 0 }""")]
    [InlineData("""{ "schemaVersion": 1.5 }""")]
    public void AFileThatIsNoPaletteLibraryIsKeptAsideAndTheLibraryStartsEmpty(string content)
    {
        Write(content);

        var loaded = Store().Load();

        Assert.Same(GraphPaletteLibrary.Empty, loaded.Library);
        Assert.False(loaded.IsReadOnly);
        var warning = Assert.Single(loaded.Warnings);
        Assert.Equal(GraphPaletteLoadWarningKind.FileCorrupt, warning.Kind);
        var preserved = $"graph-palettes.corrupt-{Stamp}.json";
        Assert.Contains(preserved, warning.Detail, StringComparison.Ordinal);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(content, File.ReadAllText(Path.Combine(_directory, preserved)));
    }

    [Fact]
    public void ASecondDamagedFileIsKeptBesideTheFirst()
    {
        Write("{ first");
        Store().Load();
        Write("{ second");

        Store().Load();

        Assert.Equal([$"graph-palettes.corrupt-{Stamp}-2.json", $"graph-palettes.corrupt-{Stamp}.json"], OtherFiles());
        Assert.Equal("{ second", File.ReadAllText(Path.Combine(_directory, $"graph-palettes.corrupt-{Stamp}-2.json")));
    }

    [Fact]
    public void AfterADamagedFileTheFirstSaveWritesAGoodOneAndKeepsTheDamagedOne()
    {
        Write("{ damaged");
        var store = Store();
        store.Load();

        store.Save(Library());

        Assert.Equal(2, Store().Load().Library.CustomPalettes.Count);
        Assert.Equal([$"graph-palettes.corrupt-{Stamp}.json"], OtherFiles());
    }

    [Fact]
    public void ADamagedFileThatCannotBeKeptAsideStaysAndIsNotOverwritten()
    {
        Write("{ damaged");

        GraphPaletteLibraryLoadResult loaded;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            // Open without sharing deletion: it can be read, but not moved.
            loaded = Store().Load();
        }

        Assert.True(loaded.IsReadOnly);
        Assert.Equal(GraphPaletteLoadWarningKind.FileCorruptNotPreserved, Assert.Single(loaded.Warnings).Kind);
        Assert.Equal("{ damaged", File.ReadAllText(FilePath));
        Assert.Empty(OtherFiles());
    }

    [Fact]
    public void AFileThatCannotBeReadGivesAnEmptyReadOnlyLibrary()
    {
        Write("""{ "palettes": [] }""");

        GraphPaletteLibraryLoadResult loaded;
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            loaded = Store().Load();
        }

        Assert.Same(GraphPaletteLibrary.Empty, loaded.Library);
        Assert.True(loaded.IsReadOnly);
        Assert.Equal(GraphPaletteLoadWarningKind.FileUnreadable, Assert.Single(loaded.Warnings).Kind);
        Assert.Empty(OtherFiles());
    }

    // ---- A newer YAT's file ----

    [Fact]
    public void ANewerSchemaIsReadAsFarAsItIsUnderstoodAndIsReadOnly()
    {
        var id = Guid.NewGuid();
        Write($$"""
            { "schemaVersion": 2, "defaultPaletteId": "{{id}}", "perGraphTypeDefaults": { "Histogram": "x" },
              "palettes": [ { "id": "{{id}}", "name": "A", "colors": ["#112233"], "kind": "gradient" } ] }
            """);

        var loaded = Store().Load();

        Assert.True(loaded.IsReadOnly);
        Assert.Equal(id, loaded.Library.DefaultPaletteId);
        Assert.Equal("A", Assert.Single(loaded.Library.CustomPalettes).Name);
        var warning = Assert.Single(loaded.Warnings);
        Assert.Equal((GraphPaletteLoadWarningKind.NewerSchema, "2"), (warning.Kind, warning.Detail));
    }

    [Fact]
    public void ASessionOnANewerFileNeverOverwritesIt()
    {
        Write("""{ "schemaVersion": 3, "palettes": [], "future": [1, 2, 3] }""");
        var bytes = File.ReadAllBytes(FilePath);
        var service = new GraphPaletteLibraryService(Store(), RedBlue);

        var added = service.Add("Mine", Green);
        service.SetDefault(added.Result.PaletteId);

        Assert.Equal(GraphPaletteSaveStatus.ReadOnly, added.Save);
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
        Assert.Empty(OtherFiles());
    }

    // ---- A save that fails ----

    [Theory]
    [InlineData("WritingTemporaryFile")]
    [InlineData("BeforeReplace")]
    public void ASaveThatFailsLeavesTheLastGoodFileAsItWasAndNoTemporaryFile(string step)
    {
        var phase = Enum.Parse<PaletteWritePhase>(step);
        var store = Store();
        store.Save(Library());
        var good = File.ReadAllBytes(FilePath);
        store.WritePhaseHook = at =>
        {
            if (at == phase)
            {
                throw new IOException("The disk is full.");
            }
        };

        var exception = Assert.Throws<GraphPaletteStoreException>(() => store.Save(GraphPaletteLibrary.Empty));

        Assert.IsType<IOException>(exception.InnerException);
        Assert.Equal(good, File.ReadAllBytes(FilePath));
        Assert.Empty(OtherFiles());
        Assert.Equal(2, Store().Load().Library.CustomPalettes.Count);
    }

    [Fact]
    public void AFileThatCannotBeReplacedIsKeptAsItWas()
    {
        var store = Store();
        store.Save(Library());
        var good = File.ReadAllBytes(FilePath);
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);

        Assert.Throws<GraphPaletteStoreException>(() => store.Save(GraphPaletteLibrary.Empty));

        Assert.Equal(good, File.ReadAllBytes(FilePath));
        Assert.Empty(OtherFiles());
    }

    [Fact]
    public void AFolderThatCannotBeMadeFailsTheSave()
    {
        // The folder's own path is taken by a file.
        Directory.CreateDirectory(Path.GetDirectoryName(_directory)!);
        File.WriteAllText(_directory, "not a folder");

        try
        {
            Assert.Throws<GraphPaletteStoreException>(() => Store().Save(Library()));
        }
        finally
        {
            File.Delete(_directory);
        }
    }

    [Fact]
    public void AfterAFailedSaveTheServiceKeepsItsPalettesAndTheFileItsOwn()
    {
        var store = Store();
        var service = new GraphPaletteLibraryService(store, RedBlue);
        service.Add("Saved", Green);
        var good = File.ReadAllBytes(FilePath);
        store.WritePhaseHook = _ => throw new IOException("Access denied.");

        var change = service.Add("Unsaved", RedBlue);

        Assert.Equal(GraphPaletteSaveStatus.Failed, change.Save);
        Assert.Contains("Access denied.", change.SaveError, StringComparison.Ordinal);
        Assert.Equal(["Saved", "Unsaved"], service.Library.CustomPalettes.Select(palette => palette.Name));
        Assert.Equal(good, File.ReadAllBytes(FilePath));
        Assert.Equal(["Saved"], Store().Load().Library.CustomPalettes.Select(palette => palette.Name));
    }

    [Fact]
    public void TheFirstChangeOfAServiceWritesTheFile()
    {
        var service = new GraphPaletteLibraryService(Store(), RedBlue);
        Assert.False(File.Exists(FilePath));

        service.Duplicate(null, "My Default");

        Assert.True(File.Exists(FilePath));
        Assert.Equal(RedBlue, Assert.Single(Store().Load().Library.CustomPalettes).Palette);
    }
}
