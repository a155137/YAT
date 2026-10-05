using System.Globalization;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// Several variables from the Graph menu (Task #041), over a real project: read once, drawn together as one graph of
// series or each in a graph of its own - each exactly the graph setting up that one variable would open - with the
// windows of one request cascading, and the graphs that cannot be drawn reported together while the others open.
public partial class GraphCommandTests
{
    private const string SeveralVariables =
        "Reg1\tReg2\tLot\n" +
        "15.02\t30.1\tA\n14.97\t\tA\n15.10\t30.4\tB\n\t29.6\tB\n15.05\t30.2\tC\n" +
        "15.00\t30.0\t\n14.95\t29.9\tA\n15.08\t30.3\tB\n15.12\t30.5\tC\n14.89\t29.5\tA\n";

    private static string R(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // Everything a frame shows, as text, for comparing two graphs.
    private static string Describe(GraphRenderModel frame) =>
        $"title={frame.Title}|x={frame.XAxis.Title} {R(frame.XAxis.Range.Minimum)}..{R(frame.XAxis.Range.Maximum)}" +
        $"|y={frame.YAxis.Title} {R(frame.YAxis.Range.Minimum)}..{R(frame.YAxis.Range.Maximum)}" +
        $"|legend={(frame.Legend is { } legend ? legend.Title + ":" + string.Join(",", legend.Entries.Select(entry => $"{entry.Label}#{entry.SeriesIndex}")) : "none")}" +
        $"|panel={(frame.StatisticsPanel is { } panel ? panel.GroupHeader + ":" + string.Join(";", panel.Rows.Select(row => $"{row.Label}#{row.SeriesIndex} {row.Count} {R(row.Mean)}")) : "none")}" +
        $"|lines={string.Join(",", frame.ReferenceLines.Select(line => $"{line.Label}"))}";

    private static GraphConfiguration? ConfirmSetup(
        GraphSetupViewModel setup,
        string[] variables,
        string? group = null,
        Action<GraphSetupViewModel>? options = null)
    {
        options?.Invoke(setup);
        return ConfirmWithVariables(setup, variables, group);
    }

    private static async Task<IReadOnlyList<GraphRenderModel>> DrawAsync(
        Runtime runtime,
        GraphType type,
        GraphVariableLayout layout,
        string[] variables,
        string? group = null,
        Action<GraphSetupViewModel>? options = null)
    {
        var before = runtime.GraphWindows.Shown.Count;
        runtime.GraphDialogs.Layout = layout;
        runtime.GraphDialogs.Answer = setup => ConfirmSetup(setup, variables, group, options);
        await Command(runtime, type).ExecuteAsync(null);
        return [.. runtime.GraphWindows.Shown.Skip(before).Select(shown => shown.Frame)];
    }

    public static TheoryData<GraphType> DistributionGraphs => [GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf];

    public static TheoryData<GraphType> GraphsOfVariables =>
        [GraphType.Histogram, GraphType.ProbabilityPlot, GraphType.EmpiricalCdf, GraphType.BoxPlot];

    private static string TitleOf(GraphType type, string variables) => type switch
    {
        GraphType.Histogram => $"Histogram of {variables}",
        GraphType.ProbabilityPlot => $"Normal Probability Plot of {variables}",
        GraphType.EmpiricalCdf => $"Empirical CDF of {variables}",
        _ => $"Boxplot of {variables}"
    };

    // ---- Together ----

    [Theory]
    [MemberData(nameof(DistributionGraphs))]
    public async Task TogetherOpensOneGraphWithASeriesPerVariableAndGroup(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frame = Assert.Single(await DrawAsync(runtime, type, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot",
            setup => setup.LowerLimitText = "14.9"));

        Assert.Equal(TitleOf(type, "Reg1, Reg2"), frame.Title);
        Assert.Equal("Data", frame.XAxis.Title);
        Assert.Equal("Variable / Lot", frame.Legend!.Title);
        Assert.Equal(
            ["Reg1 / A", "Reg1 / B", "Reg1 / C", "Reg1 / (Missing)", "Reg2 / A", "Reg2 / B", "Reg2 / C", "Reg2 / (Missing)"],
            frame.Legend.Entries.Select(entry => entry.Label));
        Assert.Equal(
            frame.Legend.Entries.Select(entry => (entry.Label, (int?)entry.SeriesIndex)),
            frame.StatisticsPanel!.Rows.Select(row => (row.Label, row.SeriesIndex)));
        Assert.Equal([9, 9], new[] { "Reg1", "Reg2" }.Select(variable =>
            frame.StatisticsPanel.Rows.Where(row => row.Label.StartsWith(variable + " /", StringComparison.Ordinal)).Sum(row => row.Count)));
        Assert.Equal(["LSL 14.9"], frame.ReferenceLines.Select(line => line.Label));
        Assert.Equal([0], runtime.GraphWindows.Cascades);
    }

    [Fact]
    public async Task ABoxPlotIsDrawnTogetherAsItAlwaysWas()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frame = Assert.Single(await DrawAsync(runtime, GraphType.BoxPlot, GraphVariableLayout.Together, ["Reg1", "Reg2"], "Lot"));

        Assert.Equal("Boxplot of Reg1, Reg2", frame.Title);
        Assert.Equal("Lot", frame.XAxis.Title);
        Assert.Equal(["A", "B", "C", "(Missing)"], frame.Legend!.Entries.Select(entry => entry.Label));
    }

    // ---- Separate ----

    // Each variable's graph is the graph that variable alone opens, options, specification and labels included.
    [Theory]
    [MemberData(nameof(GraphsOfVariables))]
    public async Task SeparateOpensEachVariablesOwnGraph(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        void Options(GraphSetupViewModel setup)
        {
            setup.LowerLimitText = "14.9";
            setup.Labels.SelectedYAxisTitleMode = setup.Labels.LabelModeChoices.Single(choice => choice.Value == GraphLabelMode.Custom);
            setup.Labels.YAxisTitleText = "Wafers";
        }

        var separate = await DrawAsync(runtime, type, GraphVariableLayout.Separate, ["Reg1", "Reg2"], "Lot", Options);
        var reg1 = Assert.Single(await DrawAsync(runtime, type, GraphVariableLayout.Together, ["Reg1"], "Lot", Options));
        var reg2 = Assert.Single(await DrawAsync(runtime, type, GraphVariableLayout.Together, ["Reg2"], "Lot", Options));

        Assert.Equal(2, separate.Count);
        Assert.Equal(Describe(reg1), Describe(separate[0]));
        Assert.Equal(Describe(reg2), Describe(separate[1]));
        Assert.Equal(TitleOf(type, "Reg1"), separate[0].Title);
        Assert.Equal("Wafers", separate[1].YAxis.Title);
        Assert.Equal([0, 1, 0, 0], runtime.GraphWindows.Cascades);

        // Every window keeps its own presentation with the labels the setup gave, to be edited on its own.
        Assert.NotSame(runtime.GraphWindows.Graphs[0], runtime.GraphWindows.Graphs[1]);
        Assert.All(runtime.GraphWindows.Graphs.Take(2), graph => Assert.Equal(GraphLabelMode.Custom, graph.LabelOptions.YAxisTitle.Mode));
    }

    // With one variable the layout changes nothing.
    [Theory]
    [MemberData(nameof(GraphsOfVariables))]
    public async Task OneVariableIsTheSameGraphWhateverTheLayout(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var together = Assert.Single(await DrawAsync(runtime, type, GraphVariableLayout.Together, ["Reg1"], "Lot"));
        var separate = Assert.Single(await DrawAsync(runtime, type, GraphVariableLayout.Separate, ["Reg1"], "Lot"));

        Assert.Equal(Describe(together), Describe(separate));
        Assert.Equal(type == GraphType.BoxPlot ? "Lot" : "Reg1", together.XAxis.Title);
    }

    [Fact]
    public async Task SeparateBoxPlotsAreOnePerVariable()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);

        var frames = await DrawAsync(runtime, GraphType.BoxPlot, GraphVariableLayout.Separate, ["Reg1", "Reg2"]);

        Assert.Equal(["Boxplot of Reg1", "Boxplot of Reg2"], frames.Select(frame => frame.Title));
        Assert.All(frames, frame => Assert.Single(frame.XAxis.Ticks));
    }

    // ---- What cannot be drawn ----

    private const string DifferentRanges =
        "Narrow\tWide\n1.2\t0\n1.4\t400\n1.9\t999\n1.1\t250\n";

    [Fact]
    public async Task SeparateOpensWhatCanBeDrawnAndReportsTheRestInOneMessage()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(DifferentRanges);

        // A width of 1 from 0 suits Narrow (2 bins) and not Wide (1000 bins).
        var frames = await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Separate, ["Narrow", "Wide"], options: setup =>
        {
            setup.SelectedBinning = setup.BinningChoices.Single(choice => choice.Value == HistogramBinningMode.WidthAndStart);
            setup.BinWidthText = "1";
            setup.BinStartText = "0";
        });

        Assert.Equal("Histogram of Narrow", Assert.Single(frames).Title);
        Assert.Equal(
            $"{GraphSetupController.PartialFailureHeading}{Environment.NewLine}Wide: {HistogramRenderModelBuilder.TooManyBinsMessage}",
            Assert.Single(runtime.GraphDialogs.Errors));
    }

    // One graph asked for, one message, word for word as before.
    [Theory]
    [InlineData(GraphVariableLayout.Together)]
    [InlineData(GraphVariableLayout.Separate)]
    public async Task OneGraphThatCannotBeDrawnKeepsItsOwnMessage(GraphVariableLayout layout)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(DifferentRanges);

        var frames = await DrawAsync(runtime, GraphType.Histogram, layout, layout == GraphVariableLayout.Together ? ["Narrow", "Wide"] : ["Wide"], options: setup =>
        {
            setup.SelectedBinning = setup.BinningChoices.Single(choice => choice.Value == HistogramBinningMode.WidthAndStart);
            setup.BinWidthText = "1";
            setup.BinStartText = "0";
        });

        Assert.Empty(frames);
        Assert.Equal([HistogramRenderModelBuilder.TooManyBinsMessage], runtime.GraphDialogs.Errors);
    }

    [Fact]
    public async Task EveryGraphThatCannotBeDrawnIsNamed()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(DifferentRanges);

        var frames = await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Separate, ["Narrow", "Wide"], options: setup =>
        {
            setup.SelectedBinning = setup.BinningChoices.Single(choice => choice.Value == HistogramBinningMode.WidthAndStart);
            setup.BinWidthText = "0.001";
            setup.BinStartText = "0";
        });

        Assert.Empty(frames);
        var message = Assert.Single(runtime.GraphDialogs.Errors);
        Assert.StartsWith(GraphSetupController.PartialFailureHeading, message, StringComparison.Ordinal);
        Assert.Contains("Narrow: ", message, StringComparison.Ordinal);
        Assert.Contains("Wide: ", message, StringComparison.Ordinal);
    }

    // ---- Fifty variables (Task #060, from ten) ----

    [Fact]
    public async Task FiftyVariablesOpenFiftyCascadingWindowsAndFiftyOneAreRefused()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var header = string.Join("\t", Enumerable.Range(1, 51).Select(index => $"V{index}"));
        var rows = Enumerable.Range(0, 5).Select(row => string.Join("\t", Enumerable.Range(1, 51).Select(column => (column * 10 + row).ToString(CultureInfo.InvariantCulture))));
        await runtime.PasteAsync(header + "\n" + string.Join("\n", rows) + "\n");
        string[] fifty = [.. Enumerable.Range(1, 50).Select(index => $"V{index}")];

        var frames = await DrawAsync(runtime, GraphType.EmpiricalCdf, GraphVariableLayout.Separate, fifty);

        Assert.Equal(fifty.Select(variable => $"Empirical CDF of {variable}"), frames.Select(frame => frame.Title));
        Assert.Equal(Enumerable.Range(0, 50), runtime.GraphWindows.Cascades);

        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, [.. fifty, "V51"], null);
        await runtime.Shell.EmpiricalCdfCommand.ExecuteAsync(null);

        Assert.Equal(50, runtime.GraphWindows.Shown.Count);
        Assert.False(runtime.GraphDialogs.LastSetup.CanConfirm);
        Assert.Equal("Select at most 50 variables.", runtime.GraphDialogs.LastSetup.ValidationMessage);
    }

    // ---- The setup ----

    [Theory]
    [MemberData(nameof(GraphsOfVariables))]
    public async Task TheLayoutIsOfferedTogetherFirstAndMattersOnlyForSeveralVariables(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        runtime.GraphDialogs.Answer = _ => null;
        await Command(runtime, type).ExecuteAsync(null);
        var setup = runtime.GraphDialogs.LastSetup;
        var variables = setup.Roles.Single(role => role.Role == GraphVariableRole.Variable);

        Assert.True(setup.SupportsVariableLayout);
        Assert.True(variables.AllowsMultiple);
        Assert.True(setup.IsTogether);
        Assert.False(setup.IsLayoutEnabled);

        variables.SelectedOptions.Add(variables.Options.Single(option => option.Name == "Reg1"));
        Assert.False(setup.IsLayoutEnabled);
        Assert.Equal(GraphVariableLayout.Together, setup.ConfirmRequest()!.Layout);

        variables.SelectedOptions.Add(variables.Options.Single(option => option.Name == "Reg2"));
        Assert.True(setup.IsLayoutEnabled);
        setup.IsSeparate = true;
        Assert.False(setup.IsTogether);
        Assert.Equal(GraphVariableLayout.Separate, setup.ConfirmRequest()!.Layout);
        Assert.Equal(2, setup.ConfirmRequest()!.Configuration.FindColumnIds(GraphVariableRole.Variable).Count);
    }

    [Fact]
    public async Task TheScatterPlotOffersNoLayout()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        runtime.GraphDialogs.Answer = setup => ConfirmWithFirstColumns(setup);
        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        var setup = runtime.GraphDialogs.LastSetup;
        Assert.False(setup.SupportsVariableLayout);
        Assert.All(setup.Roles, role => Assert.False(role.AllowsMultiple));
        setup.IsSeparate = true;
        Assert.Equal(GraphVariableLayout.Together, setup.ConfirmRequest()!.Layout);
        Assert.Single(runtime.GraphWindows.Shown);
    }

    // Separately, every variable is prepared on its own, from its own slice of the one read, under its own configuration.
    [Fact]
    public async Task SeparateGraphsArePreparedOnePerVariable()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(SeveralVariables);
        var prepared = 0;
        var controller = new GraphSetupController(
            runtime.GraphDialogs,
            runtime.GraphWindows,
            new ScatterRenderModelBuilder(),
            new HistogramRenderModelBuilder(),
            new ProbabilityPlotRenderModelBuilder(),
            new EmpiricalCdfRenderModelBuilder(),
            new BoxPlotRenderModelBuilder(),
            (data, configuration, token) =>
            {
                Interlocked.Increment(ref prepared);
                var univariate = Assert.IsType<UnivariateGraphData>(data);
                Assert.Equal([univariate.Variable.ColumnId], configuration.FindColumnIds(GraphVariableRole.Variable));
                var model = new HistogramRenderModelBuilder().Build(univariate, new HistogramPlotLabels(univariate.Variable.Name), token)!;
                return (model.Frame, new HistogramRenderer(model));
            });
        runtime.GraphDialogs.Layout = GraphVariableLayout.Separate;
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg1", "Reg2");

        await controller.ConfigureAsync(GraphType.Histogram, runtime.Lifecycle.CurrentSession, runtime.Project.SelectedWorksheet, Token);

        Assert.Equal(2, prepared);
        Assert.Equal(2, runtime.GraphWindows.Shown.Count);
    }
}
