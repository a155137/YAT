using YAT.Application.Abstractions.Settings;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;

namespace YAT.Application.Tests;

// The palette library for one run of YAT (Task #050): loaded once, changed in memory, saved after each change that was
// made - unless the session is read-only - and still usable when saving fails.
public class GraphPaletteLibraryServiceTests
{
    private static readonly GraphPalette YatDefault = new([new GraphColor(1, 2, 3), new GraphColor(4, 5, 6)]);
    private static readonly GraphPalette RedBlue = new([new GraphColor(0xD6, 0x27, 0x28), new GraphColor(0x1F, 0x77, 0xB4)]);

    private sealed class Store : IGraphPaletteLibraryStore
    {
        public GraphPaletteLibraryLoadResult Loaded { get; set; } = GraphPaletteLibraryLoadResult.Empty;

        public Exception? LoadFailure { get; set; }

        public Exception? SaveFailure { get; set; }

        public List<GraphPaletteLibrary> Saved { get; } = [];

        public int Loads { get; private set; }

        public GraphPaletteLibraryLoadResult Load()
        {
            Loads++;
            return LoadFailure is { } failure ? throw failure : Loaded;
        }

        public void Save(GraphPaletteLibrary library)
        {
            if (SaveFailure is { } failure)
            {
                throw failure;
            }

            Saved.Add(library);
        }
    }

    [Fact]
    public void TheLibraryIsLoadedOnceWhenTheServiceIsMade()
    {
        var store = new Store();

        var service = new GraphPaletteLibraryService(store, YatDefault);

        Assert.Equal(1, store.Loads);
        Assert.Same(GraphPaletteLibrary.Empty, service.Library);
        Assert.False(service.IsReadOnly);
        Assert.Empty(service.LoadWarnings);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void WithoutAnyPaletteANewGraphStartsWithYatDefault()
    {
        var service = new GraphPaletteLibraryService(new Store(), YatDefault);

        Assert.Same(GraphAppearanceOptions.Default, service.DefaultAppearance);
    }

    [Fact]
    public void EveryChangeThatIsMadeIsSaved()
    {
        var store = new Store();
        var service = new GraphPaletteLibraryService(store, YatDefault);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var added = service.Add("A", RedBlue);
        var id = added.Result.PaletteId!.Value;
        service.SetDefault(id);

        Assert.Equal(GraphPaletteSaveStatus.Saved, added.Save);
        Assert.Equal(2, store.Saved.Count);
        Assert.Same(service.Library, store.Saved[^1]);
        Assert.Equal(id, store.Saved[^1].DefaultPaletteId);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void ARefusedChangeIsNeitherMadeNorSaved()
    {
        var store = new Store();
        var service = new GraphPaletteLibraryService(store, YatDefault);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        var refused = service.Add("YAT Default", RedBlue);

        Assert.False(refused.Succeeded);
        Assert.Null(refused.Save);
        Assert.Empty(store.Saved);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void ACustomDefaultIsANewGraphsPalette()
    {
        var service = new GraphPaletteLibraryService(new Store(), YatDefault);
        service.SetDefault(service.Add("A", RedBlue).Result.PaletteId);

        Assert.Equal(GraphAppearanceOptions.Default with { Palette = RedBlue }, service.DefaultAppearance);
    }

    [Fact]
    public void YatDefaultIsDuplicatedWithItsOwnColours()
    {
        var service = new GraphPaletteLibraryService(new Store(), YatDefault);

        var copy = service.Duplicate(null, "My Default");

        Assert.True(copy.Succeeded);
        Assert.Equal(YatDefault, service.Library.Find(copy.Result.PaletteId!.Value)!.Palette);
        Assert.Null(service.Library.DefaultPaletteId);
    }

    [Fact]
    public void ACustomPaletteIsDuplicated()
    {
        var service = new GraphPaletteLibraryService(new Store(), YatDefault);
        var id = service.Add("A", RedBlue).Result.PaletteId!.Value;

        var copy = service.Duplicate(id, "B");

        Assert.Equal(RedBlue, service.Library.Find(copy.Result.PaletteId!.Value)!.Palette);
    }

    [Fact]
    public void RenameEditDeleteAndResetGoThroughTheLibrary()
    {
        var service = new GraphPaletteLibraryService(new Store(), YatDefault);
        var id = service.Add("A", RedBlue).Result.PaletteId!.Value;
        service.SetDefault(id);

        Assert.True(service.Rename(id, "B").Succeeded);
        Assert.True(service.EditColors(id, YatDefault).Succeeded);
        Assert.Equal(("B", YatDefault), (service.Library.Find(id)!.Name, service.Library.Find(id)!.Palette));
        Assert.True(service.ResetDefault().Succeeded);
        Assert.Null(service.Library.DefaultPaletteId);
        Assert.True(service.Delete(id).Succeeded);
        Assert.Empty(service.Library.CustomPalettes);
    }

    [Fact]
    public void DeletingTheDefaultMakesNewGraphsYatDefaultAgain()
    {
        var service = new GraphPaletteLibraryService(new Store(), YatDefault);
        var id = service.Add("A", RedBlue).Result.PaletteId!.Value;
        service.SetDefault(id);

        service.Delete(id);

        Assert.Same(GraphAppearanceOptions.Default, service.DefaultAppearance);
    }

    [Fact]
    public void AChangeThatCannotBeSavedIsStillMadeAndSaysSo()
    {
        var store = new Store { SaveFailure = new GraphPaletteStoreException("disk full") };
        var service = new GraphPaletteLibraryService(store, YatDefault);

        var added = service.Add("A", RedBlue);

        Assert.True(added.Succeeded);
        Assert.Equal(GraphPaletteSaveStatus.Failed, added.Save);
        Assert.Equal("disk full", added.SaveError);
        Assert.Single(service.Library.CustomPalettes);

        store.SaveFailure = null;
        Assert.Equal(GraphPaletteSaveStatus.Saved, service.Rename(added.Result.PaletteId!.Value, "B").Save);
        Assert.Equal("B", Assert.Single(Assert.Single(store.Saved).CustomPalettes).Name);
    }

    [Fact]
    public void AReadOnlySessionNeverSaves()
    {
        var kept = GraphPaletteLibrary.Empty.Add("Kept", RedBlue).Library;
        var store = new Store { Loaded = new GraphPaletteLibraryLoadResult(kept, true, [new(GraphPaletteLoadWarningKind.NewerSchema, "2")]) };
        var service = new GraphPaletteLibraryService(store, YatDefault);

        var added = service.Add("A", RedBlue);

        Assert.True(service.IsReadOnly);
        Assert.Equal(GraphPaletteSaveStatus.ReadOnly, added.Save);
        Assert.Equal(2, service.Library.CustomPalettes.Count);
        Assert.Empty(store.Saved);
        Assert.Equal(GraphPaletteLoadWarningKind.NewerSchema, Assert.Single(service.LoadWarnings).Kind);
    }

    [Fact]
    public void AStoreThatThrowsOnLoadStillGivesYatDefaultAndIsNotOverwritten()
    {
        var store = new Store { LoadFailure = new InvalidOperationException("broken") };

        var service = new GraphPaletteLibraryService(store, YatDefault);

        Assert.Same(GraphPaletteLibrary.Empty, service.Library);
        Assert.True(service.IsReadOnly);
        Assert.Equal(GraphPaletteLoadWarningKind.FileUnreadable, Assert.Single(service.LoadWarnings).Kind);
        Assert.Equal(GraphPaletteSaveStatus.ReadOnly, service.Add("A", RedBlue).Save);
        Assert.Empty(store.Saved);
    }

    // ---- A whole library at once (the Palette Manager's Save) ----

    [Fact]
    public void AWholeLibraryIsPutInPlaceAndSavedOnce()
    {
        var store = new Store();
        var service = new GraphPaletteLibraryService(store, YatDefault);
        var changes = 0;
        service.Changed += (_, _) => changes++;
        var edited = GraphPaletteLibrary.Empty.Add("A", RedBlue).Library;
        edited = edited.Add("B", YatDefault).Library;
        edited = edited.SetDefault(edited.CustomPalettes[1].Id).Library;

        var saved = service.Replace(edited);

        Assert.Equal(GraphPaletteSaveStatus.Saved, saved.Status);
        Assert.Same(edited, service.Library);
        Assert.Same(edited, Assert.Single(store.Saved));
        Assert.Equal(1, changes);
        Assert.Equal(YatDefault, service.DefaultAppearance.Palette);
    }

    [Fact]
    public void AWholeLibraryThatCannotBeSavedIsStillInUse()
    {
        var store = new Store { SaveFailure = new GraphPaletteStoreException("locked") };
        var service = new GraphPaletteLibraryService(store, YatDefault);
        var edited = GraphPaletteLibrary.Empty.Add("A", RedBlue).Library;

        var saved = service.Replace(edited);

        Assert.Equal(new GraphPaletteSaveResult(GraphPaletteSaveStatus.Failed, "locked"), saved);
        Assert.Same(edited, service.Library);
    }

    [Fact]
    public void AWholeLibraryIsNotSavedInAReadOnlySession()
    {
        var store = new Store { Loaded = new GraphPaletteLibraryLoadResult(GraphPaletteLibrary.Empty, true, []) };
        var service = new GraphPaletteLibraryService(store, YatDefault);

        Assert.Equal(GraphPaletteSaveStatus.ReadOnly, service.Replace(GraphPaletteLibrary.Empty.Add("A", RedBlue).Library).Status);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void AnAppearanceTakenFromTheServiceIsNotChangedByLaterChanges()
    {
        var service = new GraphPaletteLibraryService(new Store(), YatDefault);
        var id = service.Add("A", RedBlue).Result.PaletteId!.Value;
        service.SetDefault(id);
        var snapshot = service.DefaultAppearance;

        service.EditColors(id, YatDefault);
        var second = service.Add("B", YatDefault).Result.PaletteId!.Value;
        service.SetDefault(second);
        service.Delete(id);

        Assert.Equal(RedBlue, snapshot.Palette);
        Assert.Equal(YatDefault, service.DefaultAppearance.Palette);
    }
}
