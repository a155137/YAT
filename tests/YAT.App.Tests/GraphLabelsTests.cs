using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The graph labels on the frame (Task #040): applied last by GraphPresentation, they keep the graph type's own labels
// (Auto) - returning the very frame when every label is Auto - replace them with the typed text (Custom) or remove them
// (Hidden), and change nothing but those three strings. The renderer, the layout and the exports only ever see the
// resolved strings, so a frame with Hidden labels is drawn exactly like one built without them.
public class GraphLabelsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private const int Count = 400;

    private static readonly double[] Reg1 = [.. Enumerable.Range(0, Count).Select(i => 15 + (0.1 * Math.Sin(i * 0.37)) + ((i % 7) * 0.003))];
    private static readonly double[] Reg2 = [.. Enumerable.Range(0, Count).Select(i => 14.9 + (0.15 * Math.Cos(i * 0.23)))];

    private static GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);

    private static StringGroupData Lots() => new(Column("Lot", WorksheetDataType.String), Enumerable.Range(0, Count).Select(i => (string?)$"Lot {i % 3}").ToArray());

    private static UnivariateGraphData Univariate(GraphType type, bool grouped = false) =>
        new(type, Guid.Empty, Column("Reg1"), Reg1, grouped ? Lots() : null);

    private static GraphLabelOptions Labels(GraphLabelOption? title = null, GraphLabelOption? x = null, GraphLabelOption? y = null) =>
        new(title ?? GraphLabelOption.Auto, x ?? GraphLabelOption.Auto, y ?? GraphLabelOption.Auto);

    private static readonly GraphLabelOptions AllHidden = Labels(GraphLabelOption.Hidden, GraphLabelOption.Hidden, GraphLabelOption.Hidden);

    private static readonly GraphLabelOptions AllCustom =
        Labels(GraphLabelOption.Custom("Wafer thickness"), GraphLabelOption.Custom("Thickness (um)"), GraphLabelOption.Custom("Wafers"));

    // A built graph of one type: its data, the frame its builder made and what draws its plot.
    private sealed record Built(GraphType Type, GraphData Data, GraphRenderModel Frame, IGraphPlotRenderer Plot);

    private static Built Build(GraphType type, bool grouped = false, HistogramOptions? histogram = null)
    {
        switch (type)
        {
            case GraphType.ScatterPlot:
            {
                var data = new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), Reg1, Reg2, grouped ? Lots() : null);
                var model = new ScatterRenderModelBuilder().Build(data, new ScatterPlotLabels("Reg1", "Reg2", data.Group?.Column.Name), Token)!;
                return new Built(type, data, model.Frame, new ScatterRenderer(model));
            }

            case GraphType.Histogram:
            {
                var data = Univariate(type, grouped);
                var model = new HistogramRenderModelBuilder().Build(
                    data, new HistogramPlotLabels("Reg1", data.Group?.Column.Name), histogram ?? HistogramOptions.Default, Token)!;
                return new Built(type, data, model.Frame, new HistogramRenderer(model));
            }

            case GraphType.BoxPlot:
            {
                var parts = new[]
                {
                    Univariate(type, grouped),
                    new UnivariateGraphData(type, Guid.Empty, Column("Reg2"), Reg2, grouped ? Lots() : null)
                };
                var data = new MultiVariableGraphData(type, Guid.Empty, parts);
                var model = new BoxPlotRenderModelBuilder().Build(data, new BoxPlotLabels(["Reg1", "Reg2"], grouped ? "Lot" : null), Token)!;
                return new Built(type, data, model.Frame, new BoxPlotRenderer(model));
            }

            case GraphType.ProbabilityPlot:
            {
                var data = Univariate(type, grouped);
                var model = new ProbabilityPlotRenderModelBuilder().Build(data, new ProbabilityPlotLabels("Reg1", data.Group?.Column.Name), Token)!;
                return new Built(type, data, model.Frame, new ProbabilityPlotRenderer(model));
            }

            case GraphType.EmpiricalCdf:
            {
                var data = Univariate(type, grouped);
                var model = new EmpiricalCdfRenderModelBuilder().Build(data, new EmpiricalCdfLabels("Reg1", data.Group?.Column.Name), Token)!;
                return new Built(type, data, model.Frame, new EmpiricalCdfRenderer(model));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static GraphConfiguration Configuration(
        GraphType type,
        GraphLabelOptions? labels = null,
        bool statistics = true,
        Specification? specification = null) =>
        new(type, Guid.Empty, [])
        {
            StatisticsOptions = new GraphStatisticsOptions(statistics ? GraphStatisticsMode.Auto : GraphStatisticsMode.Hide),
            Specification = specification ?? Specification.None,
            LabelOptions = labels ?? GraphLabelOptions.Default
        };

    private static GraphRenderModel Apply(Built built, GraphLabelOptions? labels = null, bool statistics = true, Specification? specification = null) =>
        GraphPresentation.Apply(built.Frame, built.Data, Configuration(built.Type, labels, statistics, specification), Token);

    public static TheoryData<GraphType> GraphTypes => [.. Enum.GetValues<GraphType>()];

    public static TheoryData<GraphType, bool> GraphTypesGroupedOrNot => new()
    {
        { GraphType.ScatterPlot, false }, { GraphType.ScatterPlot, true },
        { GraphType.Histogram, false }, { GraphType.Histogram, true },
        { GraphType.BoxPlot, false }, { GraphType.BoxPlot, true },
        { GraphType.ProbabilityPlot, false }, { GraphType.ProbabilityPlot, true },
        { GraphType.EmpiricalCdf, false }, { GraphType.EmpiricalCdf, true }
    };

    private static byte[] Png(GraphRenderModel frame, IGraphPlotRenderer? plot, GraphTheme? theme = null) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(frame, plot, theme ?? GraphThemes.Light));

    // A frame built from scratch with the given strings and everything else of the original: what the renderer should
    // see when labels are applied.
    private static GraphRenderModel Rebuilt(GraphRenderModel original, string? title, string? xTitle, string? yTitle) =>
        new GraphRenderModel(
            title,
            new GraphAxisModel(original.XAxis.Range, original.XAxis.Ticks, xTitle),
            new GraphAxisModel(original.YAxis.Range, original.YAxis.Ticks, yTitle),
            original.Legend)
        {
            StatisticsPanel = original.StatisticsPanel,
            ReferenceLines = original.ReferenceLines
        };

    // ---- Auto ----

    [Theory]
    [MemberData(nameof(GraphTypesGroupedOrNot))]
    public void AllAutoReturnsTheVeryFrame(GraphType type, bool grouped)
    {
        var built = Build(type, grouped);
        var definition = GraphTypeDefinitions.For(type);

        Assert.Same(built.Frame, GraphLabelsBuilder.Attach(built.Frame, definition, GraphLabelOptions.Default));
        Assert.Same(built.Frame, GraphLabelsBuilder.Attach(built.Frame, definition, Labels()));

        // Text typed and then left on Auto changes nothing either.
        var typedButAuto = new GraphLabelOption(GraphLabelMode.Auto, "typed");
        Assert.Same(built.Frame, GraphLabelsBuilder.Attach(built.Frame, definition, Labels(typedButAuto, typedButAuto, typedButAuto)));
    }

    [Theory]
    [MemberData(nameof(GraphTypesGroupedOrNot))]
    public void TheDefaultConfigurationKeepsTheBuildersLabelsAndAxes(GraphType type, bool grouped)
    {
        var built = Build(type, grouped);
        var applied = Apply(built);

        if (!GraphTypeDefinitions.For(type).Supports(GraphCapability.StatisticsPanel))
        {
            Assert.Same(built.Frame, applied);
        }

        Assert.Same(built.Frame.XAxis, applied.XAxis);
        Assert.Same(built.Frame.YAxis, applied.YAxis);
        Assert.Equal(built.Frame.Title, applied.Title);
    }

    [Fact]
    public void AGraphTypeWithoutTheCapabilityKeepsItsFrame()
    {
        var built = Build(GraphType.Histogram);
        var withoutLabels = new GraphTypeDefinition(GraphType.Histogram, "Test", GraphTypeDefinitions.For(GraphType.Histogram).Roles);

        Assert.Same(built.Frame, GraphLabelsBuilder.Attach(built.Frame, withoutLabels, AllHidden));
        Assert.Same(built.Frame, GraphLabelsBuilder.Attach(built.Frame, withoutLabels, AllCustom));
    }

    // ---- Custom and Hidden ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void CustomReplacesEachLabelWithTheNormalizedText(GraphType type)
    {
        var built = Build(type, grouped: true);
        var labels = Labels(GraphLabelOption.Custom("  Wafer\r\nthickness "), GraphLabelOption.Custom("Thickness\t(um)"), GraphLabelOption.Custom("Wafers"));

        var frame = Apply(built, labels);

        Assert.Equal("Wafer thickness", frame.Title);
        Assert.Equal("Thickness (um)", frame.XAxis.Title);
        Assert.Equal("Wafers", frame.YAxis.Title);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void HiddenRemovesEveryLabel(GraphType type)
    {
        var frame = Apply(Build(type, grouped: true), AllHidden);

        Assert.Null(frame.Title);
        Assert.Null(frame.XAxis.Title);
        Assert.Null(frame.YAxis.Title);
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void OnlyTheThreeStringsChange(GraphType type)
    {
        var built = Build(type, grouped: true);
        var auto = Apply(built);

        foreach (var labels in new[] { AllCustom, AllHidden })
        {
            // The labels step on the frame the other steps finished: everything but the three strings is that frame's.
            var frame = GraphLabelsBuilder.Attach(auto, GraphTypeDefinitions.For(type), labels);
            var applied = Apply(built, labels);
            Assert.Equal((applied.Title, applied.XAxis.Title, applied.YAxis.Title), (frame.Title, frame.XAxis.Title, frame.YAxis.Title));

            Assert.Equal(auto.XAxis.Range, frame.XAxis.Range);
            Assert.Same(auto.XAxis.Ticks, frame.XAxis.Ticks);
            Assert.Equal(auto.YAxis.Range, frame.YAxis.Range);
            Assert.Same(auto.YAxis.Ticks, frame.YAxis.Ticks);
            Assert.Same(auto.Legend, frame.Legend);
            Assert.Same(auto.StatisticsPanel, frame.StatisticsPanel);
            Assert.Same(auto.ReferenceLines, frame.ReferenceLines);
        }
    }

    [Fact]
    public void AnAxisWhoseTitleStaysIsKeptAsItIs()
    {
        var built = Build(GraphType.Histogram);

        var titleOnly = Apply(built, Labels(title: GraphLabelOption.Custom("Only the title")));
        Assert.Equal("Only the title", titleOnly.Title);
        Assert.Same(built.Frame.XAxis, titleOnly.XAxis);
        Assert.Same(built.Frame.YAxis, titleOnly.YAxis);

        var yOnly = Apply(built, Labels(y: GraphLabelOption.Hidden));
        Assert.Equal(built.Frame.Title, yOnly.Title);
        Assert.Same(built.Frame.XAxis, yOnly.XAxis);
        Assert.Null(yOnly.YAxis.Title);

        // Custom text equal to the graph type's own label changes nothing.
        Assert.Same(built.Frame, GraphLabelsBuilder.Attach(built.Frame, GraphTypeDefinitions.For(GraphType.Histogram),
            Labels(GraphLabelOption.Custom(built.Frame.Title!), GraphLabelOption.Custom("Reg1"), GraphLabelOption.Custom("Frequency"))));
    }

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void ApplyingTheSameLabelsAgainChangesNothing(GraphType type)
    {
        var built = Build(type, grouped: true);

        foreach (var labels in new[] { GraphLabelOptions.Default, AllCustom, AllHidden })
        {
            var once = Apply(built, labels);
            var twice = GraphPresentation.Apply(once, built.Data, Configuration(type, labels), Token);

            Assert.Equal(once.Title, twice.Title);
            Assert.Equal(once.XAxis.Title, twice.XAxis.Title);
            Assert.Equal(once.YAxis.Title, twice.YAxis.Title);
            Assert.Same(once.XAxis, twice.XAxis);
            Assert.Same(once.YAxis, twice.YAxis);
        }
    }

    [Fact]
    public void AnInvalidCustomLabelIsRefused()
    {
        var built = Build(GraphType.Histogram);

        Assert.Throws<ArgumentException>(() => Apply(built, Labels(GraphLabelOption.Custom("   "))));
    }

    // ---- Graph types ----

    // The histogram's Y axis title follows its Y scale under Auto; Custom and Hidden override every scale alike.
    [Theory]
    [InlineData(HistogramYScale.Frequency, "Frequency")]
    [InlineData(HistogramYScale.Percent, "Percent")]
    [InlineData(HistogramYScale.Density, "Density")]
    public void TheHistogramYTitleFollowsItsScaleUnderAutoOnly(HistogramYScale scale, string automatic)
    {
        var built = Build(GraphType.Histogram, histogram: new HistogramOptions(scale));

        Assert.Equal(automatic, Apply(built).YAxis.Title);
        Assert.Equal("Wafers", Apply(built, Labels(y: GraphLabelOption.Custom("Wafers"))).YAxis.Title);
        Assert.Null(Apply(built, Labels(y: GraphLabelOption.Hidden)).YAxis.Title);

        // The scale's own axis - range and ticks - is the same under every mode.
        var auto = Apply(built);
        foreach (var labels in new[] { Labels(y: GraphLabelOption.Custom("Wafers")), Labels(y: GraphLabelOption.Hidden) })
        {
            var frame = Apply(built, labels);
            Assert.Equal(auto.YAxis.Range, frame.YAxis.Range);
            Assert.Same(auto.YAxis.Ticks, frame.YAxis.Ticks);
        }
    }

    [Fact]
    public void AScatterPlotNamesItsAxesAfterItsColumnsUnderAuto()
    {
        var built = Build(GraphType.ScatterPlot);

        var auto = Apply(built);
        Assert.Equal("Scatterplot of Reg2 vs Reg1", auto.Title);
        Assert.Equal("Reg1", auto.XAxis.Title);
        Assert.Equal("Reg2", auto.YAxis.Title);

        var custom = Apply(built, Labels(x: GraphLabelOption.Custom("Thickness"), y: GraphLabelOption.Custom("Resistance")));
        Assert.Equal("Scatterplot of Reg2 vs Reg1", custom.Title);
        Assert.Equal("Thickness", custom.XAxis.Title);
        Assert.Equal("Resistance", custom.YAxis.Title);
        Assert.Equal(auto.XAxis.Range, custom.XAxis.Range);
        Assert.Equal(auto.YAxis.Range, custom.YAxis.Range);
    }

    // A box plot has no Y axis title of its own, and no X axis title without a group column: Auto keeps them empty,
    // Hidden has nothing to remove, Custom gives them one.
    [Fact]
    public void ABoxPlotKeepsItsEmptyTitlesUnderAutoAndTakesCustomOnes()
    {
        var ungrouped = Build(GraphType.BoxPlot);
        var definition = GraphTypeDefinitions.For(GraphType.BoxPlot);

        Assert.Equal("Boxplot of Reg1, Reg2", ungrouped.Frame.Title);
        Assert.Null(ungrouped.Frame.XAxis.Title);
        Assert.Null(ungrouped.Frame.YAxis.Title);
        Assert.Same(ungrouped.Frame, Apply(ungrouped));
        Assert.Same(ungrouped.Frame, GraphLabelsBuilder.Attach(ungrouped.Frame, definition, Labels(x: GraphLabelOption.Hidden, y: GraphLabelOption.Hidden)));

        var withY = Apply(ungrouped, Labels(y: GraphLabelOption.Custom("Thickness (um)")));
        Assert.Equal("Thickness (um)", withY.YAxis.Title);
        Assert.Null(withY.XAxis.Title);
        Assert.Same(ungrouped.Frame.XAxis, withY.XAxis);
        Assert.Same(ungrouped.Frame.YAxis.Ticks, withY.YAxis.Ticks);

        var grouped = Build(GraphType.BoxPlot, grouped: true);
        Assert.Equal("Lot", Apply(grouped).XAxis.Title);
        Assert.Null(Apply(grouped, Labels(x: GraphLabelOption.Hidden)).XAxis.Title);
        Assert.Equal("Fab lot", Apply(grouped, Labels(x: GraphLabelOption.Custom("Fab lot"))).XAxis.Title);

        // The category ticks that name the boxes are not a title and stay.
        Assert.Same(grouped.Frame.XAxis.Ticks, Apply(grouped, AllHidden).XAxis.Ticks);
    }

    [Theory]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void APercentAxisTakesLabelsLikeAnyOther(GraphType type)
    {
        var built = Build(type);

        Assert.Equal("Percent", Apply(built).YAxis.Title);
        Assert.Equal("Cumulative %", Apply(built, Labels(y: GraphLabelOption.Custom("Cumulative %"))).YAxis.Title);
        Assert.Null(Apply(built, Labels(y: GraphLabelOption.Hidden)).YAxis.Title);
    }

    // ---- With the other presentation steps ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void ACustomXTitleSitsOnTheAxisTheSpecificationWidened(GraphType type)
    {
        var built = Build(type);
        var specification = new Specification(10, 15, 20);

        var linesOnly = Apply(built, specification: specification);
        var withLabels = Apply(built, Labels(x: GraphLabelOption.Custom("Thickness")), specification: specification);

        Assert.NotEqual(built.Frame.XAxis.Range, linesOnly.XAxis.Range);
        Assert.Equal(linesOnly.XAxis.Range, withLabels.XAxis.Range);
        Assert.Equal(linesOnly.XAxis.Ticks, withLabels.XAxis.Ticks);
        Assert.Equal(linesOnly.ReferenceLines, withLabels.ReferenceLines);
        Assert.Equal("Thickness", withLabels.XAxis.Title);
        Assert.Equal("Reg1", linesOnly.XAxis.Title);

        var hidden = Apply(built, Labels(x: GraphLabelOption.Hidden), specification: specification);
        Assert.Equal(linesOnly.XAxis.Range, hidden.XAxis.Range);
        Assert.Null(hidden.XAxis.Title);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public void TheStatisticsPanelIsKeptUnderEveryLabel(GraphType type)
    {
        var built = Build(type, grouped: true);
        var auto = Apply(built);
        Assert.NotNull(auto.StatisticsPanel);

        foreach (var labels in new[] { AllCustom, AllHidden })
        {
            var panel = Apply(built, labels).StatisticsPanel!;
            Assert.Equal(auto.StatisticsPanel!.Title, panel.Title);
            Assert.Equal(auto.StatisticsPanel.GroupHeader, panel.GroupHeader);
            Assert.Equal(auto.StatisticsPanel.Rows, panel.Rows);
        }

        var off = Apply(built, AllCustom, statistics: false);
        Assert.Null(off.StatisticsPanel);
        Assert.Equal("Wafer thickness", off.Title);
    }

    // ---- Layout and drawing ----

    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void HiddenLabelsGiveTheirRoomToThePlot(GraphType type)
    {
        var built = Build(type, grouped: true);
        var bounds = new SKRect(0, 0, 1600, 1000);
        var auto = Apply(built);
        var hidden = Apply(built, AllHidden);

        var autoLayout = SkiaGraphRenderer.Layout(auto, bounds, GraphThemes.Light);
        var hiddenLayout = SkiaGraphRenderer.Layout(hidden, bounds, GraphThemes.Light);

        Assert.True(hiddenLayout.TitleArea.IsEmpty);
        Assert.True(hiddenLayout.PlotArea.Top < autoLayout.PlotArea.Top, "the title's room goes to the plot");
        Assert.True(hiddenLayout.PlotArea.Bottom > autoLayout.PlotArea.Bottom, "the X axis title's room goes to the plot");
        if (auto.YAxis.Title is not null)
        {
            Assert.True(hiddenLayout.PlotArea.Left < autoLayout.PlotArea.Left, "the Y axis title's room goes to the plot");
        }

        // Laid out exactly like a frame that never had labels.
        var never = Rebuilt(auto, null, null, null);
        Assert.Equal(SkiaGraphRenderer.Layout(never, bounds, GraphThemes.Light).PlotArea, hiddenLayout.PlotArea);
    }

    // The renderer sees only the resolved strings: a frame with applied labels is drawn exactly like a frame built with
    // those strings, in both themes.
    [Theory]
    [MemberData(nameof(GraphTypes))]
    public void AppliedLabelsAreDrawnLikeLabelsBuiltIn(GraphType type)
    {
        var built = Build(type, grouped: true);
        var auto = Apply(built);

        foreach (var labels in new[] { AllCustom, AllHidden, Labels(GraphLabelOption.Hidden, GraphLabelOption.Custom("Thickness"), GraphLabelOption.Auto) })
        {
            var frame = Apply(built, labels);
            var rebuilt = Rebuilt(auto, frame.Title, frame.XAxis.Title, frame.YAxis.Title);

            foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
            {
                Assert.Equal(Png(rebuilt, built.Plot, theme), Png(frame, built.Plot, theme));
            }
        }

        Assert.NotEqual(Png(auto, built.Plot), Png(Apply(built, AllHidden), built.Plot));
    }

    // ---- Window, clipboard and exports ----

    // Every way out of the graph window draws the snapshot's frame through the same service: the PNG export, the
    // PowerPoint export and the clipboard all get the same image of the resolved labels, and the suggested file names
    // follow the resolved title - "Graph" when it is hidden.
    [Theory]
    [InlineData(GraphLabelMode.Auto, "Histogram of Reg1")]
    [InlineData(GraphLabelMode.Custom, "Wafer_ A_B")]
    [InlineData(GraphLabelMode.Hidden, "Graph")]
    public async Task EveryExportUsesTheResolvedLabels(GraphLabelMode mode, string fileName)
    {
        var built = Build(GraphType.Histogram);
        var title = mode switch
        {
            GraphLabelMode.Custom => GraphLabelOption.Custom("Wafer: A/B"),
            GraphLabelMode.Hidden => GraphLabelOption.Hidden,
            _ => GraphLabelOption.Auto
        };
        var frame = Apply(built, Labels(title, GraphLabelOption.Custom("Thickness")));
        var snapshot = new GraphExportSnapshot(frame, built.Plot, GraphThemes.Light);
        var expected = Png(frame, built.Plot);

        using var directory = new TemporaryDirectory();
        var dialogs = new FakeGraphExportDialogs { PngPath = directory.File("graph.png"), PowerPointPath = directory.File("graph.pptx") };
        var powerPoint = new FakePowerPointGraphExporter();
        var clipboard = new FakeGraphImageClipboard();
        var controller = new GraphExportController(dialogs, new GraphExportService(), powerPoint, clipboard);

        await controller.ExportPngAsync(snapshot, Token);
        await controller.ExportPowerPointAsync(snapshot, Token);
        await controller.CopyImageAsync(snapshot, Token);

        Assert.Equal([$"{fileName}.png", $"{fileName}.pptx"], dialogs.SuggestedNames);
        Assert.Equal(expected, File.ReadAllBytes(dialogs.PngPath!));
        Assert.Equal(expected, powerPoint.Last.Image.Png);
        Assert.Equal(expected, Assert.Single(clipboard.Copied));
        Assert.Empty(dialogs.Errors);
    }
}
