using YAT.Application.Graphs;
using YAT.App.Tests.TestDoubles;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using YAT.app.Views;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Editing a graph's appearance (Task #046): in the setup's Appearance... dialog and a drawn graph's Edit Appearance...,
// through the one editor. A confirmed appearance is kept with the graph and changes only the theme it is drawn in: the
// very same frames, nothing read or worked out again. Default keeps the theme's colours out of the configuration.
public class GraphAppearanceEditingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 600;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)))];

    private static readonly GraphTypeDefinition HistogramDefinition = GraphTypeDefinitions.For(GraphType.Histogram);

    private static readonly GraphColor Red = new(0xD0, 0x00, 0x00);
    private static readonly GraphColor Navy = new(0x12, 0x34, 0x56);

    private static readonly GraphAppearanceOptions Styled = new(new GraphPalette([Red, Navy]), GraphGridMode.Hide, null, Navy);

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static GraphPresentationState Histogram(GraphAppearanceOptions? appearance = null)
    {
        var group = new StringGroupData(Column("Lot", WorksheetDataType.String), (string?[])[.. Enumerable.Range(0, Count).Select(i => $"Lot {i % 4}")]);
        var data = new UnivariateGraphData(GraphType.Histogram, Guid.Empty, Column("Reg1"), Reg1, group);
        var model = new HistogramRenderModelBuilder().Build(data, new HistogramPlotLabels("Reg1", "Lot"), Token)!;
        var configuration = new GraphConfiguration(GraphType.Histogram, Guid.Empty, [])
        {
            AppearanceOptions = appearance ?? GraphAppearanceOptions.Default
        };

        return GraphPresentation.Present(model.Frame, data, configuration, Token);
    }

    private sealed class FakeAppearanceDialog(Func<GraphAppearanceOptions, GraphAppearanceOptions?> answer) : IGraphAppearanceDialog
    {
        public List<(GraphTypeDefinition Definition, GraphAppearanceOptions Current)> Calls { get; } = [];

        public Task<GraphAppearanceOptions?> EditAsync(GraphTypeDefinition definition, GraphAppearanceOptions current)
        {
            Calls.Add((definition, current));
            return Task.FromResult(answer(current));
        }
    }

    // ---- The controller ----

    [Fact]
    public async Task AConfirmedAppearanceKeepsTheVeryFrames()
    {
        var graph = Histogram();
        var dialog = new FakeAppearanceDialog(_ => Styled);
        var controller = new GraphAppearanceEditController(graph, dialog);
        var changed = 0;
        controller.GraphChanged += (_, _) => changed++;

        Assert.True(controller.CanEdit);
        Assert.True(await controller.EditAsync());

        var call = Assert.Single(dialog.Calls);
        Assert.Same(HistogramDefinition, call.Definition);
        Assert.Same(GraphAppearanceOptions.Default, call.Current);
        Assert.Equal(1, changed);
        Assert.Equal(Styled, controller.Graph.AppearanceOptions);
        Assert.Same(graph.Frame, controller.Graph.Frame);
        Assert.Same(graph.BaseFrame, controller.Graph.BaseFrame);
        Assert.Same(graph.UnlabelledFrame, controller.Graph.UnlabelledFrame);
    }

    [Fact]
    public async Task ACancelledEditChangesNothing()
    {
        var graph = Histogram(Styled);
        var controller = new GraphAppearanceEditController(graph, new FakeAppearanceDialog(_ => null));

        Assert.False(await controller.EditAsync());
        Assert.Same(graph, controller.Graph);
    }

    [Fact]
    public async Task AnAppearanceThatIsNotValidIsNeverApplied()
    {
        var graph = Histogram();
        foreach (var invalid in new[]
                 {
                     new GraphAppearanceOptions(new GraphPalette([])),
                     new GraphAppearanceOptions(new GraphPalette([.. Enumerable.Repeat(Red, 17)])),
                     new GraphAppearanceOptions(GridMode: (GraphGridMode)5)
                 })
        {
            var controller = new GraphAppearanceEditController(graph, new FakeAppearanceDialog(_ => invalid));
            Assert.False(await controller.EditAsync());
            Assert.Same(graph, controller.Graph);
        }
    }

    // Custom and back to Default: the graph as it was, frames and all.
    [Fact]
    public async Task RepeatedEditsComeBackToTheDefault()
    {
        var graph = Histogram();
        var answers = new Queue<GraphAppearanceOptions>(
        [
            Styled, new GraphAppearanceOptions(GridMode: GraphGridMode.Show), Styled with { Palette = null }, GraphAppearanceOptions.Default
        ]);
        var controller = new GraphAppearanceEditController(graph, new FakeAppearanceDialog(_ => answers.Dequeue()));

        for (var edit = 0; edit < 4; edit++)
        {
            Assert.True(await controller.EditAsync());
            Assert.Same(graph.Frame, controller.Graph.Frame);
        }

        Assert.Equal(GraphAppearanceOptions.Default, controller.Graph.AppearanceOptions);
        Assert.Same(GraphThemes.Light, GraphAppearance.Resolve(GraphThemes.Light, controller.Graph.AppearanceOptions));
    }

    // Labels, ranges, the legend, the statistics and the appearance are edited on the one graph: each keeps the others.
    [Fact]
    public async Task TheAppearanceAndTheOtherEditorsKeepEachOther()
    {
        var graph = Histogram();
        var appearance = new GraphAppearanceEditController(graph, new FakeAppearanceDialog(_ => Styled));
        Assert.True(await appearance.EditAsync());

        var legend = new GraphLegendEditController(graph, new FixedLegendDialog());
        legend.Show(appearance.Graph);
        Assert.True(await legend.EditAsync());
        Assert.Equal(GraphLegendPosition.Bottom, legend.Graph.Frame.LegendPosition);
        Assert.Equal(Styled, legend.Graph.AppearanceOptions);

        var statistics = legend.Graph.WithStatistics(new GraphStatisticsOptions(GraphStatisticsMode.Hide))
            .WithLabels(new GraphLabelOptions(GraphLabelOption.Custom("Wafer"), GraphLabelOption.Auto, GraphLabelOption.Auto));
        appearance.Show(statistics);
        var back = new GraphAppearanceEditController(statistics, new FakeAppearanceDialog(_ => GraphAppearanceOptions.Default));
        Assert.True(await back.EditAsync());
        Assert.Same(statistics.Frame, back.Graph.Frame);
        Assert.Equal("Wafer", back.Graph.Frame.Title);
        Assert.Null(back.Graph.Frame.StatisticsPanel);
        Assert.Equal(GraphLegendPosition.Bottom, back.Graph.Frame.LegendPosition);
    }

    private sealed class FixedLegendDialog : IGraphLegendDialog
    {
        public Task<GraphLegendOptions?> EditAsync(GraphLegendOptions current) =>
            Task.FromResult<GraphLegendOptions?>(new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Bottom));
    }

    [Fact]
    public async Task OneEditAtATime()
    {
        var release = new TaskCompletionSource<GraphAppearanceOptions?>();
        var dialog = new BlockingDialog(release.Task);
        var controller = new GraphAppearanceEditController(Histogram(), dialog);

        var first = controller.EditAsync();
        Assert.False(await controller.EditAsync());
        release.SetResult(Styled);
        Assert.True(await first);
        Assert.Equal(1, dialog.Opened);
    }

    private sealed class BlockingDialog(Task<GraphAppearanceOptions?> answer) : IGraphAppearanceDialog
    {
        public int Opened { get; private set; }

        public Task<GraphAppearanceOptions?> EditAsync(GraphTypeDefinition definition, GraphAppearanceOptions current)
        {
            Opened++;
            return answer;
        }
    }

    [Fact]
    public void EditAppearanceIsOfferedOnTheRightClickMenu()
    {
        var command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => { });
        var item = GraphWindow.EditAppearanceItem(command);

        Assert.Equal("Edit A_ppearance...", item.Header);
        Assert.Same(command, item.Command);
    }

    // ---- The editor ----

    [Fact]
    public void TheEditorStartsFromTheThemeWithoutCopyingItsColors()
    {
        var appearance = new GraphAppearanceEditorViewModel(HistogramDefinition);

        Assert.Equal(["Default", "Custom"], appearance.PaletteModeChoices.Select(choice => choice.Name));
        Assert.Equal(["Auto", "Show", "Hide"], appearance.GridModeChoices.Select(choice => choice.Name));
        Assert.False(appearance.IsCustomPalette);
        Assert.Equal(GraphAppearanceOptions.Default, appearance.Options);
        Assert.True(appearance.IsValid);

        // Where editing a custom palette starts: the theme's colours - but only once Custom is chosen.
        Assert.Equal(
            GraphThemes.Light.SeriesPalette.Select(GraphAppearance.FromSkia),
            appearance.PaletteColors.Select(color => color.Color!.Value));
        Assert.Null(appearance.Options!.Palette);

        appearance.SelectedPaletteMode = appearance.PaletteModeChoices[1];
        Assert.Equal(GraphThemes.Light.SeriesPalette.Select(GraphAppearance.FromSkia), appearance.Options!.Palette!.Colors);

        appearance.SelectedPaletteMode = appearance.PaletteModeChoices[0];
        Assert.Equal(GraphAppearanceOptions.Default, appearance.Options);
    }

    [Fact]
    public void TheEditorStartsFromTheAppearanceItIsGiven()
    {
        var appearance = new GraphAppearanceEditorViewModel(HistogramDefinition, Styled);

        Assert.True(appearance.IsCustomPalette);
        Assert.Equal([Red, Navy], appearance.PaletteColors.Select(color => color.Color!.Value));
        Assert.Equal(GraphGridMode.Hide, appearance.SelectedGridMode.Value);
        Assert.False(appearance.GridColor.IsCustom);
        Assert.True(appearance.PlotBackground.IsCustom);
        Assert.Equal("#123456", appearance.PlotBackground.Editor.Text);
        Assert.False(appearance.GraphBackground.IsCustom);
        Assert.Equal(Styled, appearance.Options);
    }

    // A custom palette keeps one to sixteen colours: Add after the one being edited, Remove the one being edited.
    [Fact]
    public void APaletteGrowsToSixteenAndShrinksToOne()
    {
        var appearance = new GraphAppearanceEditorViewModel(HistogramDefinition);
        Assert.False(appearance.AddPaletteColorCommand.CanExecute(null));
        appearance.SelectedPaletteMode = appearance.PaletteModeChoices[1];

        appearance.SelectedPaletteColor = appearance.PaletteColors[1];
        appearance.AddPaletteColorCommand.Execute(null);
        Assert.Equal(9, appearance.PaletteColors.Count);
        Assert.Same(appearance.PaletteColors[2], appearance.SelectedPaletteColor);

        while (appearance.AddPaletteColorCommand.CanExecute(null))
        {
            appearance.AddPaletteColorCommand.Execute(null);
        }

        Assert.Equal(GraphPalette.MaximumColors, appearance.PaletteColors.Count);
        Assert.True(appearance.IsValid);

        while (appearance.RemovePaletteColorCommand.CanExecute(null))
        {
            appearance.RemovePaletteColorCommand.Execute(null);
        }

        Assert.Single(appearance.PaletteColors);
        Assert.Single(appearance.Options!.Palette!.Colors);
        Assert.True(appearance.IsValid);
    }

    // The order is the order the colours stand in: series i takes colour i.
    [Fact]
    public void EditingAColorKeepsItsPlace()
    {
        var appearance = new GraphAppearanceEditorViewModel(HistogramDefinition, Styled);
        appearance.SelectedPaletteColor = appearance.PaletteColors[1];
        appearance.SelectedPaletteColor!.Choose(new GraphColor(1, 2, 3));

        Assert.Equal([Red, new GraphColor(1, 2, 3)], appearance.Options!.Palette!.Colors);
    }

    // A colour that is not "#RRGGBB" cannot be confirmed; written the long way, it is written the one way.
    [Fact]
    public void AColorThatIsNotOneIsExplained()
    {
        var appearance = new GraphAppearanceEditorViewModel(HistogramDefinition);
        appearance.PlotBackground.SelectedMode = appearance.PlotBackground.ModeChoices[1];
        appearance.PlotBackground.Editor.Text = "#12345";

        Assert.False(appearance.IsValid);
        Assert.Null(appearance.Options);
        Assert.Equal("Enter each color as #RRGGBB, for example #1F77B4.", appearance.ValidationMessage);

        appearance.PlotBackground.Editor.Text = " #abcdef";
        Assert.True(appearance.IsValid);
        appearance.PlotBackground.Editor.Canonicalize();
        Assert.Equal("#ABCDEF", appearance.PlotBackground.Editor.Text);
        Assert.Equal(new GraphColor(0xAB, 0xCD, 0xEF), appearance.Options!.PlotBackground);

        // A colour that is Default is not read at all, whatever its text.
        appearance.PlotBackground.Editor.Text = "nonsense";
        appearance.PlotBackground.SelectedMode = appearance.PlotBackground.ModeChoices[0];
        Assert.True(appearance.IsValid);
        Assert.Null(appearance.Options!.PlotBackground);

        // A palette colour likewise, while the palette is custom.
        appearance.SelectedPaletteMode = appearance.PaletteModeChoices[1];
        appearance.PaletteColors[3].Text = "red";
        Assert.False(appearance.IsValid);
        appearance.SelectedPaletteMode = appearance.PaletteModeChoices[0];
        Assert.True(appearance.IsValid);
    }

    [Fact]
    public void EveryColorAndTheGridAreEditable()
    {
        var appearance = new GraphAppearanceEditorViewModel(HistogramDefinition);
        appearance.SelectedGridMode = appearance.GridModeChoices[2];
        foreach (var (choice, color) in new[] { (appearance.GridColor, Red), (appearance.PlotBackground, Navy), (appearance.GraphBackground, new GraphColor(9, 9, 9)) })
        {
            choice.SelectedMode = choice.ModeChoices[1];
            choice.Editor.Choose(color);
        }

        Assert.Equal(new GraphAppearanceOptions(null, GraphGridMode.Hide, Red, Navy, new GraphColor(9, 9, 9)), appearance.Options);
    }

    [Fact]
    public void AppearanceProblemsAreExplained()
    {
        Assert.Equal("Please choose how the grid is shown.",
            GraphValidationMessages.For(new GraphValidationError(GraphValidationReason.AppearanceGridModeInvalid), HistogramDefinition));
        Assert.Equal("A custom palette has 1 to 16 colors.",
            GraphValidationMessages.For(new GraphValidationError(GraphValidationReason.AppearancePaletteInvalid), HistogramDefinition));
    }

    // ---- The setup ----

    private static readonly Worksheet Sheet = new() { Id = Guid.NewGuid(), Name = "Sheet1" };

    private static readonly WorksheetColumn Reg1Column = new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = Sheet.Id,
        Name = "Reg1",
        Index = 0,
        DataType = WorksheetDataType.Numeric
    };

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    [InlineData(GraphType.BoxPlot)]
    public void TheSetupCarriesTheAppearance(GraphType type)
    {
        var setup = new GraphSetupViewModel(GraphTypeDefinitions.For(type), Sheet, [Reg1Column]);
        var variable = setup.Roles.Single(role => role.Role == GraphVariableRole.Variable);
        variable.Choose(variable.Options.Single(option => option.Name == "Reg1"));

        Assert.True(setup.SupportsAppearance);
        Assert.Same(GraphAppearanceOptions.Default, setup.Appearance);
        Assert.Same(GraphAppearanceOptions.Default, setup.Confirm()!.AppearanceOptions);

        setup.Appearance = Styled;
        Assert.Equal(Styled, setup.Confirm()!.AppearanceOptions);

        // The dialog starts from what the setup holds, and what it confirms is what the setup holds next.
        var editor = new GraphAppearanceEditorViewModel(GraphTypeDefinitions.For(type), setup.Appearance);
        editor.SelectedGridMode = editor.GridModeChoices[1];
        setup.Appearance = editor.Options!;
        Assert.Equal(Styled with { GridMode = GraphGridMode.Show }, setup.Confirm()!.AppearanceOptions);
    }

    [Fact]
    public void TheSetupRefusesAnAppearanceThatIsNotValid()
    {
        var setup = new GraphSetupViewModel(HistogramDefinition, Sheet, [Reg1Column]);
        var variable = setup.Roles.Single(role => role.Role == GraphVariableRole.Variable);
        variable.Choose(variable.Options.Single(option => option.Name == "Reg1"));
        Assert.True(setup.CanConfirm);

        setup.Appearance = new GraphAppearanceOptions(new GraphPalette([]));

        Assert.False(setup.CanConfirm);
        Assert.Null(setup.Confirm());
        Assert.Equal("A custom palette has 1 to 16 colors.", setup.ValidationMessage);
    }
}
