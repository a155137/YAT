using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.app.Graphs;

namespace YAT.App.Tests;

// The default palette from the Graph menu (Task #050), over a real project and a palette file in the test's own folder:
// a new setup starts with a copy of the default palette's colours - or with YAT Default, which is no palette at all - and
// keeps them whatever the library does while it is open; the next setup starts with whatever the default is then.
public partial class GraphCommandTests
{
    private static readonly GraphPalette Warm =
        new([new GraphColor(0xE4, 0x57, 0x2E), new GraphColor(0xF3, 0xA7, 0x12), new GraphColor(0x66, 0x9B, 0xBC)]);

    private static readonly GraphPalette Cool = new([new GraphColor(0x29, 0x33, 0x5C)]);

    private static (GraphSetupController Graphs, GraphPaletteLibraryService Palettes) WithPalettes(Runtime runtime)
    {
        var palettes = runtime.Composition.CreateGraphPaletteLibrary(runtime.Directory.File("graph-palettes.json"));
        return (runtime.Composition.CreateGraphSetup(runtime.GraphDialogs, runtime.GraphWindows, palettes), palettes);
    }

    private static Task ConfigureAsync(Runtime runtime, GraphSetupController graphs, GraphType type) =>
        graphs.ConfigureAsync(type, runtime.Lifecycle.CurrentSession, runtime.Project.SelectedWorksheet, Token);

    [Fact]
    public async Task WithoutAPaletteLibraryASetupStartsWithYatDefault()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg1");

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Same(GraphAppearanceOptions.Default, runtime.GraphDialogs.LastSetup.Appearance);
        Assert.Same(GraphAppearanceOptions.Default, runtime.Graphs.LastConfiguration!.AppearanceOptions);
        Assert.Null(runtime.GraphWindows.Graphs[^1].AppearanceOptions.Palette);
    }

    [Fact]
    public async Task UnderYatDefaultASetupStartsWithTheThemesOwnLookItself()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (graphs, palettes) = WithPalettes(runtime);
        palettes.Add("Warm", Warm);
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg1");

        await ConfigureAsync(runtime, graphs, GraphType.Histogram);

        Assert.Null(palettes.Library.DefaultPaletteId);
        Assert.Same(GraphAppearanceOptions.Default, runtime.GraphDialogs.LastSetup.Appearance);
        Assert.Null(graphs.LastConfiguration!.AppearanceOptions.Palette);
        Assert.True(runtime.GraphWindows.Graphs[^1].AppearanceOptions.IsDefault);
    }

    [Theory]
    [MemberData(nameof(GraphsOfVariables))]
    public async Task ACustomDefaultIsANewGraphsPalette(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (graphs, palettes) = WithPalettes(runtime);
        palettes.SetDefault(palettes.Add("Warm", Warm).Result.PaletteId);
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, ["Reg1"], "Lot");

        await ConfigureAsync(runtime, graphs, type);

        Assert.Equal(GraphAppearanceOptions.Default with { Palette = Warm }, runtime.GraphDialogs.LastSetup.Appearance);
        Assert.Equal(Warm, graphs.LastConfiguration!.AppearanceOptions.Palette);
        Assert.Equal(Warm, runtime.GraphWindows.Graphs[^1].AppearanceOptions.Palette);
    }

    [Fact]
    public async Task AScatterPlotTakesTheCustomDefaultToo()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (graphs, palettes) = WithPalettes(runtime);
        palettes.SetDefault(palettes.Add("Warm", Warm).Result.PaletteId);
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("X-axis", "Reg1"), ("Y-axis", "Reg2"));

        await ConfigureAsync(runtime, graphs, GraphType.ScatterPlot);

        Assert.Equal(Warm, runtime.GraphWindows.Graphs[^1].AppearanceOptions.Palette);
    }

    [Fact]
    public async Task AnOpenSetupKeepsItsColoursWhateverTheLibraryDoes()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (graphs, palettes) = WithPalettes(runtime);
        var warm = palettes.Add("Warm", Warm).Result.PaletteId!.Value;
        palettes.SetDefault(warm);

        runtime.GraphDialogs.Answer = setup =>
        {
            // While the setup is open: the palette is changed, another becomes the default, and the first is deleted.
            palettes.EditColors(warm, Cool);
            palettes.SetDefault(palettes.Add("Cool", Cool).Result.PaletteId);
            palettes.Delete(warm);
            Assert.Equal(Warm, setup.Appearance.Palette);
            return ConfirmWithVariables(setup, "Reg1");
        };

        await ConfigureAsync(runtime, graphs, GraphType.Histogram);

        Assert.Equal(Warm, graphs.LastConfiguration!.AppearanceOptions.Palette);
        Assert.Equal(Warm, runtime.GraphWindows.Graphs[^1].AppearanceOptions.Palette);
    }

    [Fact]
    public async Task TheNextSetupStartsWithTheDefaultAsItIsThen()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (graphs, palettes) = WithPalettes(runtime);
        var warm = palettes.Add("Warm", Warm).Result.PaletteId!.Value;
        palettes.SetDefault(warm);
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg1");
        await ConfigureAsync(runtime, graphs, GraphType.Histogram);

        palettes.EditColors(warm, Cool);
        await ConfigureAsync(runtime, graphs, GraphType.Histogram);
        var edited = graphs.LastConfiguration!.AppearanceOptions.Palette;

        palettes.ResetDefault();
        await ConfigureAsync(runtime, graphs, GraphType.Histogram);

        Assert.Equal(Warm, runtime.GraphWindows.Graphs[0].AppearanceOptions.Palette);
        Assert.Equal(Cool, edited);
        Assert.Same(GraphAppearanceOptions.Default, graphs.LastConfiguration!.AppearanceOptions);
        Assert.Null(runtime.GraphWindows.Graphs[2].AppearanceOptions.Palette);
    }

    [Fact]
    public async Task ASetupOpenedUnderPaletteAKeepsAWhenTheDefaultBecomesBAndTheNextSetupHasB()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (graphs, palettes) = WithPalettes(runtime);
        var a = palettes.Add("A", Warm).Result.PaletteId!.Value;
        var b = palettes.Add("B", Cool).Result.PaletteId!.Value;
        palettes.SetDefault(a);

        runtime.GraphDialogs.Answer = setup =>
        {
            // The setup's Appearance... dialog is offered the library, A marked as the default.
            Assert.Equal(["A", "B"], setup.Palettes!.Choices.Palettes.Select(choice => choice.Name));
            Assert.True(setup.Palettes.Choices.Palettes[0].IsDefault);

            // The Palette Manager, opened from that dialog, makes B the default and saves.
            var manager = setup.Palettes.CreateManager();
            manager.Selected = manager.Palettes.Single(entry => entry.Id == b);
            manager.SetDefaultCommand.Execute(null);
            Assert.Equal(GraphPaletteSaveStatus.Saved, manager.Save()!.Status);

            Assert.Equal(Warm, setup.Appearance.Palette);
            return ConfirmWithVariables(setup, "Reg1");
        };
        await ConfigureAsync(runtime, graphs, GraphType.Histogram);
        var first = graphs.LastConfiguration!.AppearanceOptions.Palette;

        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg1");
        await ConfigureAsync(runtime, graphs, GraphType.Histogram);

        Assert.Equal(Warm, first);
        Assert.Equal(Cool, graphs.LastConfiguration!.AppearanceOptions.Palette);
        Assert.Equal(b, palettes.Library.DefaultPaletteId);
    }

    [Fact]
    public void TheSetupAndTheGraphWindowsShareOneAccessToTheLibrary()
    {
        using var directory = new TemporaryDirectory();
        var composition = new YAT.app.Composition.CompositionRoot(TimeProvider.System, directory.File("temp"));
        var palettes = composition.CreateGraphPaletteLibrary(directory.File("graph-palettes.json"));

        Assert.Same(composition.GraphPaletteAccess(palettes), composition.GraphPaletteAccess(palettes));
    }

    [Theory]
    [InlineData(GraphVariableLayout.Together, 1)]
    [InlineData(GraphVariableLayout.Separate, 2)]
    public async Task VariablesTogetherOrSeparatelyAllTakeTheSnapshot(GraphVariableLayout layout, int windows)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (graphs, palettes) = WithPalettes(runtime);
        palettes.SetDefault(palettes.Add("Warm", Warm).Result.PaletteId);
        runtime.GraphDialogs.Layout = layout;
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, ["Reg1", "Reg2"], "Lot");

        await ConfigureAsync(runtime, graphs, GraphType.ProbabilityPlot);

        Assert.Equal(windows, runtime.GraphWindows.Graphs.Count);
        Assert.All(runtime.GraphWindows.Graphs, graph => Assert.Equal(Warm, graph.AppearanceOptions.Palette));
    }

    [Fact]
    public async Task TheDefaultIsKeptForTheNextRun()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var (_, palettes) = WithPalettes(runtime);
        palettes.SetDefault(palettes.Add("Warm", Warm).Result.PaletteId);

        // Another run of YAT reads the same file.
        var (graphs, restarted) = WithPalettes(runtime);
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg1");
        await ConfigureAsync(runtime, graphs, GraphType.EmpiricalCdf);

        Assert.Equal("Warm", restarted.Library.DefaultPalette!.Name);
        Assert.Equal(Warm, graphs.LastConfiguration!.AppearanceOptions.Palette);
    }

    [Fact]
    public void ASetupRefusesAnAppearanceThatIsNotValid()
    {
        var worksheet = new YAT.Domain.Entities.Worksheet { Id = Guid.NewGuid(), Name = "Sheet1" };

        Assert.Throws<ArgumentException>(() => new YAT.app.ViewModels.GraphSetupViewModel(
            GraphTypeDefinitions.For(GraphType.Histogram), worksheet, [], appearance: GraphAppearanceOptions.Default with { Palette = new GraphPalette([]) }));
    }

    [Fact]
    public void TheLibraryOfACompositionIsLoadedFromThePathItIsGivenAndWritesNothingByItself()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File(Path.Combine("YAT", "graph-palettes.json"));

        var palettes = new YAT.app.Composition.CompositionRoot(TimeProvider.System, directory.File("temp")).CreateGraphPaletteLibrary(path);

        Assert.Same(GraphPaletteLibrary.Empty, palettes.Library);
        Assert.False(File.Exists(path));
        Assert.False(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.Equal(8, palettes.YatDefault.Colors.Count);
        Assert.Equal(new GraphColor(0x1F, 0x77, 0xB4), palettes.YatDefault.Colors[0]);
    }
}
