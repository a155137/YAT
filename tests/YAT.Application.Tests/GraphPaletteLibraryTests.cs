using YAT.Application.Graphs;

namespace YAT.Application.Tests;

// The user's graph palette library (Task #050): stable ids, trimmed names unique ignoring case and never YAT Default's,
// 1 to 16 colours, YAT Default as no palette at all, and every change a new library - so what a setup took from it
// stays as it was.
public class GraphPaletteLibraryTests
{
    private static readonly GraphColor Red = new(0xD6, 0x27, 0x28);
    private static readonly GraphColor Blue = new(0x1F, 0x77, 0xB4);
    private static readonly GraphColor Green = new(0x2C, 0xA0, 0x2C);

    private static GraphPalette Colors(params GraphColor[] colors) => new(colors);

    private static GraphPalette Count(int count) => new([.. Enumerable.Range(0, count).Select(index => new GraphColor((byte)index, 0, 0))]);

    private static (GraphPaletteLibrary Library, Guid Id) With(string name, GraphPalette palette, GraphPaletteLibrary? library = null)
    {
        var result = (library ?? GraphPaletteLibrary.Empty).Add(name, palette);
        Assert.True(result.Succeeded);
        return (result.Library, result.PaletteId!.Value);
    }

    // ---- YAT Default ----

    [Fact]
    public void AnEmptyLibraryHasNoPalettesAndYatDefault()
    {
        var library = GraphPaletteLibrary.Empty;

        Assert.Empty(library.CustomPalettes);
        Assert.Null(library.DefaultPaletteId);
        Assert.Null(library.DefaultPalette);
    }

    [Fact]
    public void YatDefaultsAppearanceIsTheThemesOwnLookItself()
    {
        Assert.Same(GraphAppearanceOptions.Default, GraphPaletteLibrary.Empty.DefaultAppearance);
        Assert.Null(GraphPaletteLibrary.Empty.DefaultAppearance.Palette);
    }

    // ---- Adding ----

    [Fact]
    public void APaletteIsAddedWithAnIdOfItsOwn()
    {
        var result = GraphPaletteLibrary.Empty.Add("My Palette", Colors(Red, Blue));

        Assert.True(result.Succeeded);
        var palette = Assert.Single(result.Library.CustomPalettes);
        Assert.Equal(result.PaletteId, palette.Id);
        Assert.NotEqual(Guid.Empty, palette.Id);
        Assert.Equal("My Palette", palette.Name);
        Assert.Equal(Colors(Red, Blue), palette.Palette);
        Assert.Empty(GraphPaletteLibrary.Empty.CustomPalettes);
    }

    [Fact]
    public void PalettesKeepTheOrderTheyWereAddedIn()
    {
        var (library, _) = With("B", Colors(Red));
        (library, _) = With("A", Colors(Blue), library);

        Assert.Equal(["B", "A"], library.CustomPalettes.Select(palette => palette.Name));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    public void OneToSixteenColoursMakeAPalette(int count)
    {
        Assert.True(GraphPaletteLibrary.Empty.Add("P", Count(count)).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(17)]
    public void NoColoursOrMoreThanSixteenAreRefused(int count)
    {
        var result = GraphPaletteLibrary.Empty.Add("P", Count(count));

        Assert.Equal(GraphPaletteLibraryProblem.PaletteInvalid, result.Problem);
        Assert.Same(GraphPaletteLibrary.Empty, result.Library);
    }

    [Fact]
    public void ANameIsKeptTrimmed()
    {
        var (library, _) = With("  Presentation \t", Colors(Red));

        Assert.Equal("Presentation", library.CustomPalettes[0].Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankNameIsRefused(string? name)
    {
        Assert.Equal(GraphPaletteLibraryProblem.NameMissing, GraphPaletteLibrary.Empty.Add(name!, Colors(Red)).Problem);
    }

    [Theory]
    [InlineData("YAT Default")]
    [InlineData("yat default")]
    [InlineData("  YAT DEFAULT  ")]
    public void YatDefaultsNameIsReserved(string name)
    {
        Assert.Equal(GraphPaletteLibraryProblem.NameReserved, GraphPaletteLibrary.Empty.Add(name, Colors(Red)).Problem);
    }

    [Theory]
    [InlineData("My Palette")]
    [InlineData("my palette")]
    [InlineData(" MY PALETTE ")]
    public void ANameAnotherPaletteHasIsRefusedWhateverItsCase(string name)
    {
        var (library, _) = With("My Palette", Colors(Red));

        var result = library.Add(name, Colors(Blue));

        Assert.Equal(GraphPaletteLibraryProblem.NameTaken, result.Problem);
        Assert.Same(library, result.Library);
    }

    [Fact]
    public void AnIdAnotherPaletteHasIsRefused()
    {
        var (library, id) = With("A", Colors(Red));

        Assert.Equal(GraphPaletteLibraryProblem.IdTaken, library.Add(id, "B", Colors(Blue)).Problem);
    }

    [Fact]
    public void TheLibraryKeepsItsOwnCopyOfTheColours()
    {
        var colors = new List<GraphColor> { Red };
        var (library, _) = With("A", new GraphPalette(colors));

        colors.Add(Blue);

        Assert.Equal([Red], library.CustomPalettes[0].Palette.Colors);
    }

    // ---- Renaming and new colours keep the id ----

    [Fact]
    public void ARenamedPaletteKeepsItsIdAndColours()
    {
        var (library, id) = With("Old", Colors(Red, Blue));

        var result = library.Rename(id, "  New ");

        Assert.True(result.Succeeded);
        var palette = Assert.Single(result.Library.CustomPalettes);
        Assert.Equal((id, "New"), (palette.Id, palette.Name));
        Assert.Equal(Colors(Red, Blue), palette.Palette);
        Assert.Equal("Old", library.CustomPalettes[0].Name);
    }

    [Fact]
    public void APaletteMayBeRenamedToItsOwnNameInAnotherCase()
    {
        var (library, id) = With("my palette", Colors(Red));

        Assert.Equal("My Palette", library.Rename(id, "My Palette").Library.CustomPalettes[0].Name);
    }

    [Fact]
    public void ARenameToAnotherPalettesNameOrYatDefaultsIsRefused()
    {
        var (library, _) = With("A", Colors(Red));
        (library, var b) = With("B", Colors(Blue), library);

        Assert.Equal(GraphPaletteLibraryProblem.NameTaken, library.Rename(b, "a").Problem);
        Assert.Equal(GraphPaletteLibraryProblem.NameReserved, library.Rename(b, "YAT Default").Problem);
        Assert.Equal(GraphPaletteLibraryProblem.NameMissing, library.Rename(b, " ").Problem);
        Assert.Equal(GraphPaletteLibraryProblem.PaletteNotFound, library.Rename(Guid.NewGuid(), "C").Problem);
    }

    [Fact]
    public void NewColoursKeepTheIdAndName()
    {
        var (library, id) = With("A", Colors(Red));

        var palette = Assert.Single(library.EditColors(id, Colors(Green, Blue, Red)).Library.CustomPalettes);

        Assert.Equal((id, "A"), (palette.Id, palette.Name));
        Assert.Equal(Colors(Green, Blue, Red), palette.Palette);
    }

    [Fact]
    public void ColoursThatAreNoPaletteAreRefused()
    {
        var (library, id) = With("A", Colors(Red));

        Assert.Equal(GraphPaletteLibraryProblem.PaletteInvalid, library.EditColors(id, Count(17)).Problem);
        Assert.Equal(GraphPaletteLibraryProblem.PaletteInvalid, library.EditColors(id, Count(0)).Problem);
        Assert.Equal(GraphPaletteLibraryProblem.PaletteNotFound, library.EditColors(Guid.NewGuid(), Colors(Red)).Problem);
    }

    // ---- Deleting ----

    [Fact]
    public void ADeletedPaletteIsGone()
    {
        var (library, a) = With("A", Colors(Red));
        (library, var b) = With("B", Colors(Blue), library);

        var result = library.Delete(a);

        Assert.Equal([b], result.Library.CustomPalettes.Select(palette => palette.Id));
        Assert.Equal(GraphPaletteLibraryProblem.PaletteNotFound, result.Library.Delete(a).Problem);
    }

    [Fact]
    public void DeletingTheDefaultPaletteMakesYatDefaultTheDefault()
    {
        var (library, a) = With("A", Colors(Red));
        library = library.SetDefault(a).Library;

        var result = library.Delete(a);

        Assert.Null(result.Library.DefaultPaletteId);
        Assert.Same(GraphAppearanceOptions.Default, result.Library.DefaultAppearance);
    }

    [Fact]
    public void DeletingAnotherPaletteKeepsTheDefault()
    {
        var (library, a) = With("A", Colors(Red));
        (library, var b) = With("B", Colors(Blue), library);
        library = library.SetDefault(a).Library;

        Assert.Equal(a, library.Delete(b).Library.DefaultPaletteId);
    }

    // ---- The default ----

    [Fact]
    public void APaletteBecomesTheDefault()
    {
        var (library, a) = With("A", Colors(Red, Blue));

        var result = library.SetDefault(a);

        Assert.Equal(a, result.Library.DefaultPaletteId);
        Assert.Equal("A", result.Library.DefaultPalette!.Name);
        Assert.Equal(Colors(Red, Blue), result.Library.DefaultAppearance.Palette);
    }

    [Fact]
    public void TheDefaultsAppearanceChangesOnlyThePalette()
    {
        var (library, a) = With("A", Colors(Red));

        Assert.Equal(GraphAppearanceOptions.Default with { Palette = Colors(Red) }, library.SetDefault(a).Library.DefaultAppearance);
    }

    [Fact]
    public void ResetMakesYatDefaultTheDefaultAndKeepsThePalettes()
    {
        var (library, a) = With("A", Colors(Red));
        library = library.SetDefault(a).Library;

        var result = library.ResetDefault();

        Assert.Null(result.Library.DefaultPaletteId);
        Assert.Single(result.Library.CustomPalettes);
        Assert.Same(GraphAppearanceOptions.Default, result.Library.DefaultAppearance);
    }

    [Fact]
    public void SetDefaultNullIsYatDefault()
    {
        var (library, a) = With("A", Colors(Red));

        Assert.Null(library.SetDefault(a).Library.SetDefault(null).Library.DefaultPaletteId);
    }

    [Fact]
    public void APaletteThatIsNotThereCannotBeTheDefault()
    {
        Assert.Equal(GraphPaletteLibraryProblem.PaletteNotFound, GraphPaletteLibrary.Empty.SetDefault(Guid.NewGuid()).Problem);
    }

    // ---- Duplicating ----

    [Fact]
    public void ADuplicateIsANewPaletteOfTheSameColours()
    {
        var (library, a) = With("A", Colors(Red, Green));

        var result = library.Duplicate(a, "A copy");

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Library.CustomPalettes.Count);
        var copy = result.Library.Find(result.PaletteId!.Value)!;
        Assert.NotEqual(a, copy.Id);
        Assert.Equal("A copy", copy.Name);
        Assert.Equal(Colors(Red, Green), copy.Palette);
    }

    [Fact]
    public void ADuplicateNeedsANameOfItsOwnAndASource()
    {
        var (library, a) = With("A", Colors(Red));

        Assert.Equal(GraphPaletteLibraryProblem.NameTaken, library.Duplicate(a, "a").Problem);
        Assert.Equal(GraphPaletteLibraryProblem.PaletteNotFound, library.Duplicate(Guid.NewGuid(), "B").Problem);
    }

    // ---- A library made from stored palettes ----

    [Fact]
    public void ALibraryOfPalettesThatBreakARuleCannotBeMade()
    {
        var id = Guid.NewGuid();
        var a = new GraphPaletteDefinition(id, "A", Colors(Red));

        Assert.Throws<ArgumentException>(() => new GraphPaletteLibrary([a, a with { Name = "B" }], null));
        Assert.Throws<ArgumentException>(() => new GraphPaletteLibrary([a, new GraphPaletteDefinition(Guid.NewGuid(), "a", Colors(Red))], null));
        Assert.Throws<ArgumentException>(() => new GraphPaletteLibrary([new GraphPaletteDefinition(id, "YAT Default", Colors(Red))], null));
        Assert.Throws<ArgumentException>(() => new GraphPaletteLibrary([new GraphPaletteDefinition(id, " A ", Colors(Red))], null));
        Assert.Throws<ArgumentException>(() => new GraphPaletteLibrary([new GraphPaletteDefinition(id, "A", Count(17))], null));
        Assert.Throws<ArgumentException>(() => new GraphPaletteLibrary([a], Guid.NewGuid()));
    }

    // ---- What a setup took stays as it was ----

    [Fact]
    public void TheDefaultsAppearanceKeepsItsColoursWhateverTheLibraryDoesNext()
    {
        var (library, a) = With("A", Colors(Red, Blue));
        library = library.SetDefault(a).Library;
        var snapshot = library.DefaultAppearance;

        library = library.EditColors(a, Colors(Green)).Library;
        library = library.Rename(a, "Renamed").Library;
        library = library.ResetDefault().Library;
        library = library.Delete(a).Library;

        Assert.Equal(Colors(Red, Blue), snapshot.Palette);
        Assert.Empty(library.CustomPalettes);
    }
}
