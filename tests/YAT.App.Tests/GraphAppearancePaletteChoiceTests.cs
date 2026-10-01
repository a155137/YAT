using YAT.Application.Abstractions.Settings;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// The appearance editor's palette list (Task #050): YAT Default, the library's palettes, Custom (this graph). A graph
// takes a library palette's colours, never the palette; the list only shows where colours came from, by matching them -
// one palette with exactly those colours is shown by name, none or several is Custom (this graph). A drawn graph's Edit
// Appearance offers the same list, and nothing the library does later reaches the graph.
public class GraphAppearancePaletteChoiceTests
{
    private static readonly GraphTypeDefinition Histogram = GraphTypeDefinitions.For(GraphType.Histogram);

    private static readonly GraphPalette Warm = new([new GraphColor(0xE4, 0x57, 0x2E), new GraphColor(0xF3, 0xA7, 0x12)]);
    private static readonly GraphPalette Cool = new([new GraphColor(0x29, 0x33, 0x5C)]);
    private static readonly GraphPalette Own = new([new GraphColor(1, 2, 3), new GraphColor(4, 5, 6), new GraphColor(7, 8, 9)]);

    private static readonly GraphPaletteChoices Choices = new(
        [new GraphPaletteChoice("Warm", Warm, IsDefault: true), new GraphPaletteChoice("Cool", Cool, IsDefault: false)],
        YatDefaultIsDefault: false);

    private static GraphAppearanceEditorViewModel Editor(GraphPalette? palette, GraphPaletteChoices? choices = null) =>
        new(Histogram, GraphAppearanceOptions.Default with { Palette = palette }, choices ?? Choices);

    private sealed class Store(GraphPaletteLibraryLoadResult loaded) : IGraphPaletteLibraryStore
    {
        public GraphPaletteLibraryLoadResult Load() => loaded;

        public void Save(GraphPaletteLibrary library)
        {
        }
    }

    // ---- The list ----

    [Fact]
    public void TheListIsYatDefaultTheLibraryAndThisGraphsOwn()
    {
        var editor = Editor(null);

        Assert.Equal(["YAT Default", "Warm  (Default)", "Cool", "Custom (this graph)"], editor.PaletteOptions.Select(option => option.ToString()));
        Assert.Equal(
            [GraphPaletteOptionKind.YatDefault, GraphPaletteOptionKind.Library, GraphPaletteOptionKind.Library, GraphPaletteOptionKind.ThisGraph],
            editor.PaletteOptions.Select(option => option.Kind));
    }

    [Fact]
    public void YatDefaultIsMarkedWhenItIsTheDefault()
    {
        var editor = Editor(null, Choices with { YatDefaultIsDefault = true, Palettes = [new GraphPaletteChoice("Cool", Cool, false)] });

        Assert.Equal("YAT Default  (Default)", editor.PaletteOptions[0].ToString());
    }

    [Fact]
    public void WithoutALibraryOnlyYatDefaultAndThisGraphsOwnAreOffered()
    {
        var editor = new GraphAppearanceEditorViewModel(Histogram);

        Assert.Equal(["YAT Default", "Custom (this graph)"], editor.PaletteOptions.Select(option => option.Name));
        Assert.Null(editor.PaletteNotice);
    }

    // ---- What a graph's colours are shown as ----

    [Fact]
    public void NoPaletteIsYatDefault()
    {
        var editor = Editor(null);

        Assert.Equal(GraphPaletteOptionKind.YatDefault, editor.SelectedPalette!.Kind);
        Assert.False(editor.IsCustomPalette);
        Assert.Same(GraphAppearanceOptions.Default.Palette, editor.Options!.Palette);
        Assert.Equal(GraphAppearanceOptions.Default, editor.Options);
    }

    [Fact]
    public void ColoursOfExactlyOneLibraryPaletteAreShownAsThatPalette()
    {
        var editor = Editor(new GraphPalette(Cool.Colors));

        Assert.Equal("Cool", editor.SelectedPalette!.Name);
        Assert.Equal(Cool, editor.Options!.Palette);
    }

    [Fact]
    public void ColoursOfNoLibraryPaletteAreThisGraphsOwn()
    {
        var editor = Editor(Own);

        Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);
        Assert.Equal(Own, editor.Options!.Palette);
    }

    [Fact]
    public void ColoursSeveralLibraryPalettesShareAreThisGraphsOwn()
    {
        var twins = new GraphPaletteChoices([new GraphPaletteChoice("A", Warm, false), new GraphPaletteChoice("B", Warm, false)], true);

        var editor = Editor(Warm, twins);

        Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);
        Assert.Equal(Warm, editor.Options!.Palette);
    }

    [Fact]
    public void ThePaletteDefaultColoursAreMatchedInOrderOnly()
    {
        var editor = Editor(new GraphPalette([.. Warm.Colors.Reverse()]));

        Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);
    }

    // ---- Choosing ----

    [Fact]
    public void ChoosingALibraryPaletteCopiesItsColoursIntoTheGraph()
    {
        var editor = Editor(null);

        editor.SelectedPalette = editor.PaletteOptions[1];

        Assert.True(editor.IsCustomPalette);
        Assert.Equal(GraphAppearanceOptions.Default with { Palette = Warm }, editor.Options);
        Assert.Equal(Warm.Colors, editor.PaletteColors.Select(color => color.Color!.Value));
    }

    [Fact]
    public void ChoosingYatDefaultTakesThePaletteOutOfTheAppearance()
    {
        var editor = Editor(Own);

        editor.SelectedPalette = editor.PaletteOptions[0];

        Assert.Null(editor.Options!.Palette);
        Assert.True(editor.Options.IsDefault);
    }

    [Fact]
    public void ChoosingThisGraphsOwnKeepsTheColoursShown()
    {
        var editor = Editor(null);
        editor.SelectedPalette = editor.PaletteOptions[2];

        editor.SelectedPalette = editor.PaletteOptions[^1];

        Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);
        Assert.Equal(Cool, editor.Options!.Palette);
    }

    [Fact]
    public void EditingALibraryPalettesColoursMakesThemThisGraphsOwn()
    {
        var editor = Editor(null);
        editor.SelectedPalette = editor.PaletteOptions[1];

        editor.PaletteColors[0].Choose(new GraphColor(9, 9, 9));

        Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);
        Assert.Equal(new GraphColor(9, 9, 9), editor.Options!.Palette!.Colors[0]);
    }

    [Fact]
    public void EditingColoursIntoALibraryPalettesShowsThatPalette()
    {
        var editor = Editor(new GraphPalette([new GraphColor(0x29, 0x33, 0x5C), new GraphColor(1, 1, 1)]));
        Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);

        editor.SelectedPaletteColor = editor.PaletteColors[1];
        editor.RemovePaletteColorCommand.Execute(null);

        Assert.Equal("Cool", editor.SelectedPalette!.Name);
    }

    [Fact]
    public void ColoursOfThisGraphCanBeMoved()
    {
        var editor = Editor(Own);
        editor.SelectedPaletteColor = editor.PaletteColors[0];

        editor.PaletteEditor.MoveRightCommand.Execute(null);

        Assert.Equal([Own.Colors[1], Own.Colors[0], Own.Colors[2]], editor.Options!.Palette!.Colors);
    }

    // ---- The library changes while the editor is open ----

    [Fact]
    public void NewChoicesNeverChangeTheColoursBeingEdited()
    {
        var editor = Editor(null);
        editor.SelectedPalette = editor.PaletteOptions[1];

        // Warm was recoloured in the Palette Manager, and Cool deleted.
        editor.UpdateChoices(new GraphPaletteChoices([new GraphPaletteChoice("Warm", Own, true)], false));

        Assert.Equal(Warm, editor.Options!.Palette);
        Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);
        Assert.Equal(["YAT Default", "Warm  (Default)", "Custom (this graph)"], editor.PaletteOptions.Select(option => option.ToString()));
    }

    [Fact]
    public void NewChoicesKeepYatDefault()
    {
        var editor = Editor(null);

        editor.UpdateChoices(new GraphPaletteChoices([], true));

        Assert.Equal(GraphPaletteOptionKind.YatDefault, editor.SelectedPalette!.Kind);
        Assert.Equal(GraphAppearanceOptions.Default, editor.Options);
    }

    [Fact]
    public void NewChoicesShowTheColoursAsTheLibraryNowHasThem()
    {
        var editor = Editor(Own);

        editor.UpdateChoices(Choices with { Palettes = [.. Choices.Palettes, new GraphPaletteChoice("Mine", Own, false)] });

        Assert.Equal("Mine", editor.SelectedPalette!.Name);
        Assert.Equal(Own, editor.Options!.Palette);
    }

    // ---- The access the windows get ----

    [Fact]
    public void TheAccessOffersTheLibraryAsItIsNow()
    {
        var library = GraphPaletteLibrary.Empty.Add("Warm", Warm).Library;
        var service = new GraphPaletteLibraryService(new Store(new GraphPaletteLibraryLoadResult(library, false, [])), Cool);
        var access = new GraphPaletteLibraryAccess(service);
        var before = access.Choices;

        service.SetDefault(library.CustomPalettes[0].Id);

        Assert.True(before.YatDefaultIsDefault);
        Assert.False(access.Choices.YatDefaultIsDefault);
        Assert.Equal([("Warm", Warm, true)], access.Choices.Palettes.Select(choice => (choice.Name, choice.Palette, choice.IsDefault)));
        Assert.Equal(service.DefaultAppearance, access.DefaultAppearance);
    }

    [Fact]
    public void ANoticeIsOfferedUntilThePaletteManagerHasBeenOpened()
    {
        var service = new GraphPaletteLibraryService(
            new Store(new GraphPaletteLibraryLoadResult(GraphPaletteLibrary.Empty, true, [new(GraphPaletteLoadWarningKind.FileUnreadable, "locked")])),
            Cool);
        var access = new GraphPaletteLibraryAccess(service);

        var notice = access.Choices.Notice;
        Assert.Contains("could not be read", notice, StringComparison.Ordinal);
        Assert.Equal(notice, new GraphAppearanceEditorViewModel(Histogram, GraphAppearanceOptions.Default, access.Choices).PaletteNotice);

        var manager = access.CreateManager();

        Assert.Null(access.Choices.Notice);
        Assert.Equal(notice, manager.Notice);
    }

    [Fact]
    public void WithNothingToSayThereIsNoNotice()
    {
        Assert.Null(GraphPaletteLibraryAccess.Notice([], false));
        Assert.Equal(
            "The default palette was missing, so YAT Default is the default.",
            GraphPaletteLibraryAccess.Notice([new(GraphPaletteLoadWarningKind.DefaultPaletteMissing, "x")], false));
        Assert.Equal(
            "One saved palette could not be read and was left out.",
            GraphPaletteLibraryAccess.Notice([new(GraphPaletteLoadWarningKind.PaletteSkipped, "x")], false));
    }

    // ---- A drawn graph ----

    private sealed class ChoosingDialog(Func<GraphAppearanceEditorViewModel, GraphAppearanceOptions?> choose, GraphPaletteChoices choices)
        : YAT.app.Graphs.IGraphAppearanceDialog
    {
        public Task<GraphAppearanceOptions?> EditAsync(GraphTypeDefinition definition, GraphAppearanceOptions current) =>
            Task.FromResult(choose(new GraphAppearanceEditorViewModel(definition, current, choices)));
    }

    private static GraphPresentationState Drawn(GraphAppearanceOptions appearance) =>
        new(
            new GraphRenderModel(
                "Histogram of Reg1",
                new GraphAxisModel(new GraphAxisRange(0, 1), [], "Reg1"),
                new GraphAxisModel(new GraphAxisRange(0, 1), [], "Frequency")),
            Histogram,
            GraphLabelOptions.Default,
            appearanceOptions: appearance);

    [Fact]
    public async Task ADrawnGraphTakesALibraryPalettesColours()
    {
        var edit = new GraphAppearanceEditController(
            Drawn(GraphAppearanceOptions.Default),
            new ChoosingDialog(editor =>
            {
                editor.SelectedPalette = editor.PaletteOptions.Single(option => option.Name == "Cool");
                return editor.Options;
            }, Choices));
        var frame = edit.Graph.Frame;

        Assert.True(await edit.EditAsync());

        Assert.Equal(Cool, edit.Graph.AppearanceOptions.Palette);
        Assert.Same(frame, edit.Graph.Frame);
    }

    [Fact]
    public async Task ADrawnGraphGoesBackToYatDefaultOrKeepsItsOwnColours()
    {
        var own = new GraphAppearanceEditController(
            Drawn(GraphAppearanceOptions.Default with { Palette = Own }),
            new ChoosingDialog(editor =>
            {
                Assert.Equal(GraphPaletteOptionKind.ThisGraph, editor.SelectedPalette!.Kind);
                return editor.Options;
            }, Choices));
        var back = new GraphAppearanceEditController(
            Drawn(GraphAppearanceOptions.Default with { Palette = Warm }),
            new ChoosingDialog(editor =>
            {
                Assert.Equal("Warm", editor.SelectedPalette!.Name);
                editor.SelectedPalette = editor.PaletteOptions[0];
                return editor.Options;
            }, Choices));

        Assert.True(await own.EditAsync());
        Assert.True(await back.EditAsync());

        Assert.Equal(Own, own.Graph.AppearanceOptions.Palette);
        Assert.True(back.Graph.AppearanceOptions.IsDefault);
    }

    [Fact]
    public async Task ADrawnGraphKeepsItsColoursWhateverTheLibraryDoesNext()
    {
        var service = new GraphPaletteLibraryService(new Store(GraphPaletteLibraryLoadResult.Empty), Cool);
        var warm = service.Add("Warm", Warm).Result.PaletteId!.Value;
        var access = new GraphPaletteLibraryAccess(service);
        var edit = new GraphAppearanceEditController(
            Drawn(GraphAppearanceOptions.Default),
            new ChoosingDialog(editor =>
            {
                editor.SelectedPalette = editor.PaletteOptions.Single(option => option.Name == "Warm");
                return editor.Options;
            }, access.Choices));
        await edit.EditAsync();

        service.EditColors(warm, Own);
        service.SetDefault(warm);
        service.Delete(warm);

        Assert.Equal(Warm, edit.Graph.AppearanceOptions.Palette);
    }
}
