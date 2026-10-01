using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Settings;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.app.ViewModels;
using YAT.Infrastructure.Settings;

namespace YAT.App.Tests;

// The Palette Manager (Task #050): every change made to a working copy - New, Duplicate (YAT Default too), Rename,
// colours added, removed, moved and edited, Delete, Set as Default, Reset to YAT Default - checked by the library's own
// rules; Cancel leaves the library and its file as they were, Save puts the whole copy in place with one save; a failed
// save is said and the palettes stay in use; a read-only session can look and not touch.
public class GraphPaletteManagerViewModelTests
{
    private static readonly GraphPalette YatDefault = new([new GraphColor(0x1F, 0x77, 0xB4), new GraphColor(0xD6, 0x27, 0x28)]);
    private static readonly GraphPalette Warm = new([new GraphColor(0xE4, 0x57, 0x2E), new GraphColor(0xF3, 0xA7, 0x12)]);

    private sealed class Store : IGraphPaletteLibraryStore
    {
        public GraphPaletteLibraryLoadResult Loaded { get; set; } = GraphPaletteLibraryLoadResult.Empty;

        public Exception? SaveFailure { get; set; }

        public List<GraphPaletteLibrary> Saved { get; } = [];

        public GraphPaletteLibraryLoadResult Load() => Loaded;

        public void Save(GraphPaletteLibrary library)
        {
            if (SaveFailure is { } failure)
            {
                throw failure;
            }

            Saved.Add(library);
        }
    }

    private static (GraphPaletteManagerViewModel Manager, GraphPaletteLibraryService Service, Store Store) Manager(GraphPaletteLibrary? library = null, bool readOnly = false)
    {
        var store = new Store { Loaded = new GraphPaletteLibraryLoadResult(library ?? GraphPaletteLibrary.Empty, readOnly, []) };
        var service = new GraphPaletteLibraryService(store, YatDefault);
        return (new GraphPaletteManagerViewModel(service), service, store);
    }

    private static GraphPaletteLibrary WithWarm(bool asDefault = false)
    {
        var added = GraphPaletteLibrary.Empty.Add("Warm", Warm);
        return asDefault ? added.Library.SetDefault(added.PaletteId).Library : added.Library;
    }

    private static string[] Names(GraphPaletteManagerViewModel manager) => [.. manager.Palettes.Select(entry => entry.DisplayName)];

    private static GraphColor[] Colors(GraphPaletteManagerViewModel manager) => [.. manager.Colors.Colors.Select(color => color.Color!.Value)];

    // ---- Opening ----

    [Fact]
    public void YatDefaultIsListedFirstAndMarkedAsTheDefault()
    {
        var (manager, _, _) = Manager(WithWarm());

        Assert.Equal(["YAT Default  (built-in)  (Default)", "Warm"], Names(manager));
        Assert.True(manager.Palettes[0].IsBuiltIn);
        Assert.Same(manager.Palettes[0], manager.Selected);
        Assert.False(manager.HasChanges);
        Assert.False(manager.CanSave);
    }

    [Fact]
    public void ACustomDefaultIsSelectedAndMarkedWhenTheManagerOpens()
    {
        var (manager, _, _) = Manager(WithWarm(asDefault: true));

        Assert.Equal(["YAT Default  (built-in)", "Warm  (Default)"], Names(manager));
        Assert.Equal("Warm", manager.Selected!.Name);
        Assert.Equal("Warm", manager.NameText);
        Assert.Equal(Warm.Colors, Colors(manager));
    }

    [Fact]
    public void YatDefaultCanBeLookedAtButNotChanged()
    {
        var (manager, _, _) = Manager(WithWarm());

        manager.Selected = manager.Palettes[0];

        Assert.Equal(YatDefault.Colors, Colors(manager));
        Assert.False(manager.CanEditSelected);
        Assert.False(manager.Colors.IsEditable);
        Assert.False(manager.DeleteCommand.CanExecute(null));
        Assert.False(manager.Colors.AddCommand.CanExecute(null));
        Assert.True(manager.DuplicateCommand.CanExecute(null));

        manager.NameText = "Renamed";
        Assert.False(manager.HasChanges);
    }

    // ---- New, Duplicate ----

    [Fact]
    public void NewAddsAPaletteOfYatDefaultsColoursUnderAFreeName()
    {
        var (manager, _, _) = Manager();

        manager.NewCommand.Execute(null);
        manager.NewCommand.Execute(null);

        Assert.Equal(["YAT Default  (built-in)  (Default)", "New palette", "New palette 2"], Names(manager));
        Assert.Equal("New palette 2", manager.Selected!.Name);
        Assert.Equal(YatDefault.Colors, Colors(manager));
        Assert.True(manager.CanEditSelected);
        Assert.True(manager.CanSave);
    }

    [Fact]
    public void DuplicatingYatDefaultMakesAPaletteOfItsColours()
    {
        var (manager, _, _) = Manager();
        manager.Selected = manager.Palettes[0];

        manager.DuplicateCommand.Execute(null);

        Assert.Equal("YAT Default copy", manager.Selected!.Name);
        Assert.False(manager.Selected.IsBuiltIn);
        Assert.Equal(YatDefault, manager.Selected.Palette);
    }

    [Fact]
    public void DuplicatingAPaletteCopiesItsColoursUnderANameOfItsOwn()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];

        manager.DuplicateCommand.Execute(null);
        manager.Selected = manager.Palettes[1];
        manager.DuplicateCommand.Execute(null);

        Assert.Equal(["YAT Default  (built-in)  (Default)", "Warm", "Warm copy", "Warm copy 2"], Names(manager));
        Assert.All(manager.Palettes.Skip(1), entry => Assert.Equal(Warm, entry.Palette));
        Assert.Equal(3, manager.WorkingLibrary.CustomPalettes.Select(palette => palette.Id).Distinct().Count());
    }

    // ---- Rename and colours ----

    [Fact]
    public void RenamingKeepsTheIdAndShowsTheNewName()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];
        var id = manager.Selected.Id;

        manager.NameText = "Sunset ";

        Assert.Null(manager.Problem);
        Assert.Equal("Sunset", manager.Palettes[1].Name);
        Assert.Equal(id, manager.WorkingLibrary.CustomPalettes.Single().Id);
        Assert.Equal("Sunset", manager.WorkingLibrary.CustomPalettes.Single().Name);
        Assert.Equal("Sunset ", manager.NameText);
    }

    [Theory]
    [InlineData("", "Enter a name for the palette.")]
    [InlineData("  ", "Enter a name for the palette.")]
    [InlineData("yat default", "\"YAT Default\" is the name of the built-in palette.")]
    [InlineData("COOL", "Another palette already has this name.")]
    public void ANameThatCannotBeTakenIsSaidAndKeepsSaveUnavailable(string name, string problem)
    {
        var library = WithWarm().Add("Cool", YatDefault).Library;
        var (manager, _, _) = Manager(library);
        manager.Selected = manager.Palettes[1];

        manager.NameText = name;

        Assert.Equal(problem, manager.Problem);
        Assert.False(manager.CanSave);
        Assert.Equal("Warm", manager.WorkingLibrary.CustomPalettes[0].Name);

        manager.NameText = "Warmer";
        Assert.Null(manager.Problem);
        Assert.True(manager.CanSave);
    }

    [Fact]
    public void ColoursAreAddedRemovedMovedAndEditedInTheWorkingCopy()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];
        var colors = manager.Colors;

        colors.AddCommand.Execute(null);
        Assert.Equal(3, manager.WorkingLibrary.CustomPalettes[0].Palette.Colors.Count);

        // Added after the colour being edited (the first): Warm 0, the new one, Warm 1.
        var added = manager.WorkingLibrary.CustomPalettes[0].Palette.Colors[1];
        Assert.Equal([Warm.Colors[0], added, Warm.Colors[1]], manager.WorkingLibrary.CustomPalettes[0].Palette.Colors);

        colors.Selected = colors.Colors[0];
        colors.MoveRightCommand.Execute(null);
        Assert.Equal([added, Warm.Colors[0], Warm.Colors[1]], manager.WorkingLibrary.CustomPalettes[0].Palette.Colors);
        Assert.Same(colors.Colors[1], colors.Selected);

        colors.MoveLeftCommand.Execute(null);
        Assert.Equal([Warm.Colors[0], added, Warm.Colors[1]], manager.WorkingLibrary.CustomPalettes[0].Palette.Colors);
        Assert.False(colors.MoveLeftCommand.CanExecute(null));

        colors.Selected!.Choose(new GraphColor(1, 2, 3));
        Assert.Equal(new GraphColor(1, 2, 3), manager.WorkingLibrary.CustomPalettes[0].Palette.Colors[0]);
        Assert.Equal(new GraphColor(1, 2, 3), manager.Palettes[1].Palette.Colors[0]);

        colors.RemoveCommand.Execute(null);
        Assert.Equal(2, manager.WorkingLibrary.CustomPalettes[0].Palette.Colors.Count);
        Assert.True(manager.CanSave);
    }

    [Fact]
    public void APaletteKeepsOneToSixteenColours()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];

        while (manager.Colors.AddCommand.CanExecute(null))
        {
            manager.Colors.AddCommand.Execute(null);
        }

        Assert.Equal(GraphPalette.MaximumColors, manager.WorkingLibrary.CustomPalettes[0].Palette.Colors.Count);

        while (manager.Colors.RemoveCommand.CanExecute(null))
        {
            manager.Colors.RemoveCommand.Execute(null);
        }

        Assert.Single(manager.WorkingLibrary.CustomPalettes[0].Palette.Colors);
        Assert.Null(manager.Problem);
    }

    [Fact]
    public void AColourThatIsNotOneIsSaidAndKeepsSaveUnavailable()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];

        manager.Colors.Colors[0].Text = "red";

        Assert.Equal("Enter each color as #RRGGBB, for example #1F77B4.", manager.Problem);
        Assert.False(manager.CanSave);
        Assert.Equal(Warm, manager.WorkingLibrary.CustomPalettes[0].Palette);

        manager.Colors.Colors[0].Text = "#010203";
        Assert.Null(manager.Problem);
        Assert.True(manager.CanSave);
    }

    [Fact]
    public void ANameNotTakenStaysSaidWhenColoursAreEdited()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];
        manager.NameText = "";

        manager.Colors.Colors[0].Choose(new GraphColor(9, 9, 9));

        Assert.Equal("Enter a name for the palette.", manager.Problem);
        Assert.False(manager.CanSave);
    }

    [Fact]
    public void ChoosingAnotherPaletteDropsWhatWasNotTaken()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];
        manager.NameText = "";

        manager.Selected = manager.Palettes[0];
        manager.Selected = manager.Palettes[1];

        Assert.Null(manager.Problem);
        Assert.Equal("Warm", manager.NameText);
    }

    // ---- Delete and the default ----

    [Fact]
    public void DeletingSelectsTheNextPalette()
    {
        var library = WithWarm().Add("Cool", YatDefault).Library;
        var (manager, _, _) = Manager(library);
        manager.Selected = manager.Palettes[1];

        manager.DeleteCommand.Execute(null);

        Assert.Equal(["YAT Default  (built-in)  (Default)", "Cool"], Names(manager));
        Assert.Equal("Cool", manager.Selected!.Name);
    }

    [Fact]
    public void DeletingTheDefaultMakesYatDefaultTheDefaultAtOnce()
    {
        var (manager, _, _) = Manager(WithWarm(asDefault: true));

        manager.DeleteCommand.Execute(null);

        Assert.Equal(["YAT Default  (built-in)  (Default)"], Names(manager));
        Assert.Null(manager.WorkingLibrary.DefaultPaletteId);
        Assert.Same(manager.Palettes[0], manager.Selected);
    }

    [Fact]
    public void SetAsDefaultMarksThePaletteAndResetGivesYatDefaultBack()
    {
        var (manager, _, _) = Manager(WithWarm());
        manager.Selected = manager.Palettes[1];

        manager.SetDefaultCommand.Execute(null);

        Assert.Equal(["YAT Default  (built-in)", "Warm  (Default)"], Names(manager));
        Assert.False(manager.SetDefaultCommand.CanExecute(null));
        Assert.True(manager.ResetDefaultCommand.CanExecute(null));

        manager.ResetDefaultCommand.Execute(null);

        Assert.Equal(["YAT Default  (built-in)  (Default)", "Warm"], Names(manager));
        Assert.Single(manager.WorkingLibrary.CustomPalettes);
        Assert.False(manager.ResetDefaultCommand.CanExecute(null));
    }

    [Fact]
    public void YatDefaultCanBeSetAsTheDefaultFromTheList()
    {
        var (manager, _, _) = Manager(WithWarm(asDefault: true));
        manager.Selected = manager.Palettes[0];

        manager.SetDefaultCommand.Execute(null);

        Assert.Null(manager.WorkingLibrary.DefaultPaletteId);
    }

    // ---- Save and Cancel ----

    [Fact]
    public void NothingIsChangedOrSavedUntilSave()
    {
        var (manager, service, store) = Manager(WithWarm());
        var before = service.Library;

        manager.NewCommand.Execute(null);
        manager.NameText = "Mine";
        manager.SetDefaultCommand.Execute(null);

        Assert.Same(before, service.Library);
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void SaveAppliesEveryEditWithOneSave()
    {
        var (manager, service, store) = Manager(WithWarm());
        manager.NewCommand.Execute(null);
        manager.NameText = "Mine";
        manager.Colors.AddCommand.Execute(null);
        manager.SetDefaultCommand.Execute(null);

        var result = manager.Save();

        Assert.Equal(GraphPaletteSaveStatus.Saved, result!.Status);
        var saved = Assert.Single(store.Saved);
        Assert.Same(saved, service.Library);
        Assert.Equal(["Warm", "Mine"], saved.CustomPalettes.Select(palette => palette.Name));
        Assert.Equal("Mine", saved.DefaultPalette!.Name);
        Assert.Equal(3, saved.DefaultPalette.Palette.Colors.Count);
        Assert.False(manager.HasChanges);
        Assert.Null(manager.Save());
    }

    [Fact]
    public void SaveIsUnavailableWithoutChanges()
    {
        var (manager, _, store) = Manager(WithWarm());

        Assert.False(manager.CanSave);
        Assert.Null(manager.Save());
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void ASaveThatFailsLeavesThePalettesInUseAndSaysWhy()
    {
        var (manager, service, store) = Manager();
        store.SaveFailure = new GraphPaletteStoreException("The disk is full.");
        manager.NewCommand.Execute(null);

        var result = manager.Save();

        Assert.Equal(new GraphPaletteSaveResult(GraphPaletteSaveStatus.Failed, "The disk is full."), result);
        Assert.Equal("New palette", Assert.Single(service.Library.CustomPalettes).Name);
    }

    // ---- Read-only ----

    [Fact]
    public void AReadOnlySessionShowsThePalettesAndChangesNothing()
    {
        var store = new Store
        {
            Loaded = new GraphPaletteLibraryLoadResult(WithWarm(), true, [new(GraphPaletteLoadWarningKind.NewerSchema, "2")])
        };
        var service = new GraphPaletteLibraryService(store, YatDefault);
        var manager = new GraphPaletteManagerViewModel(service);
        manager.Selected = manager.Palettes[1];

        Assert.True(manager.IsReadOnly);
        Assert.Contains("newer version of YAT", manager.Notice, StringComparison.Ordinal);
        Assert.Contains("not changed", manager.Notice, StringComparison.Ordinal);
        Assert.False(manager.CanEditSelected);
        Assert.False(manager.Colors.IsEditable);
        Assert.False(manager.NewCommand.CanExecute(null));
        Assert.False(manager.DuplicateCommand.CanExecute(null));
        Assert.False(manager.DeleteCommand.CanExecute(null));
        Assert.False(manager.SetDefaultCommand.CanExecute(null));
        Assert.False(manager.ResetDefaultCommand.CanExecute(null));
        Assert.False(manager.Colors.AddCommand.CanExecute(null));

        manager.NameText = "Changed";
        Assert.False(manager.HasChanges);
        Assert.Null(manager.Save());
        Assert.Empty(store.Saved);
    }

    [Fact]
    public void ARecoveredFileIsSaidButTheManagerStillWorks()
    {
        var store = new Store
        {
            Loaded = new GraphPaletteLibraryLoadResult(
                GraphPaletteLibrary.Empty,
                false,
                [new(GraphPaletteLoadWarningKind.FileCorrupt, "It was kept as graph-palettes.corrupt-1.json."), new(GraphPaletteLoadWarningKind.PaletteSkipped, "x"), new(GraphPaletteLoadWarningKind.PaletteSkipped, "y")])
        };
        var manager = new GraphPaletteManagerViewModel(new GraphPaletteLibraryService(store, YatDefault));

        Assert.Contains("damaged", manager.Notice, StringComparison.Ordinal);
        Assert.Contains("2 saved palettes could not be read", manager.Notice, StringComparison.Ordinal);
        Assert.False(manager.IsReadOnly);
        Assert.True(manager.NewCommand.CanExecute(null));
    }

    // ---- With the real file ----

    [Fact]
    public void CancellingTheManagerLeavesTheFileUntouched()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("graph-palettes.json");
        var service = new GraphPaletteLibraryService(new JsonGraphPaletteLibraryStore(path), YatDefault);
        service.Add("Warm", Warm);
        var bytes = File.ReadAllBytes(path);
        var written = File.GetLastWriteTimeUtc(path);

        var manager = new GraphPaletteManagerViewModel(service);
        manager.NewCommand.Execute(null);
        manager.Selected = manager.Palettes[1];
        manager.DeleteCommand.Execute(null);
        manager.ResetDefaultCommand.Execute(null);
        // Cancel: the manager is simply dropped.

        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(written, File.GetLastWriteTimeUtc(path));
        Assert.Equal(["Warm"], service.Library.CustomPalettes.Select(palette => palette.Name));
        Assert.Equal(["graph-palettes.json"], Directory.GetFiles(directory.DirectoryPath).Select(Path.GetFileName));
    }

    [Fact]
    public void SavedPalettesAndTheDefaultAreThereAfterARestart()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("graph-palettes.json");
        var manager = new GraphPaletteManagerViewModel(new GraphPaletteLibraryService(new JsonGraphPaletteLibraryStore(path), YatDefault));
        manager.NewCommand.Execute(null);
        manager.NameText = "Engineering";
        manager.Colors.Colors[0].Choose(new GraphColor(0x11, 0x22, 0x33));
        manager.SetDefaultCommand.Execute(null);
        manager.Selected = manager.Palettes[0];
        manager.DuplicateCommand.Execute(null);
        Assert.Equal(GraphPaletteSaveStatus.Saved, manager.Save()!.Status);

        var restarted = new GraphPaletteLibraryService(new JsonGraphPaletteLibraryStore(path), YatDefault);

        Assert.Equal(["Engineering", "YAT Default copy"], restarted.Library.CustomPalettes.Select(palette => palette.Name));
        Assert.Equal("Engineering", restarted.Library.DefaultPalette!.Name);
        Assert.Equal(new GraphColor(0x11, 0x22, 0x33), restarted.DefaultAppearance.Palette!.Colors[0]);
        Assert.Equal(["Engineering  (Default)", "YAT Default copy"], new GraphPaletteManagerViewModel(restarted).Palettes.Skip(1).Select(entry => entry.DisplayName));
    }
}
