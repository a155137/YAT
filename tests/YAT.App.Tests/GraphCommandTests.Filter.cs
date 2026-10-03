using YAT.Application.Filtering;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// A filtered graph from the Graph menu (Task #049), over a real project: the filter is chosen in the setup's Filter
// editor from the column's values as DuckDB lists them, and every graph type draws only the rows it keeps - its series,
// its statistics and its legend - together or separately, with or without a group, the group being the filter column or
// another one.
public partial class GraphCommandTests
{
    // Site 2 is rows 2, 5, 8 and 11 (PS_RAW 11, 14, 17, 20); row 7 has no Site; Site 5's only row has no PS_RAW.
    private const string FilterData =
        "PS_RAW\tReg2\tSite\tTester\n" +
        "10\t100\t1\tT1\n" +
        "11\t101\t2\tT2\n" +
        "12\t102\t3\tT1\n" +
        "13\t103\t4\tT2\n" +
        "14\t104\t2\tT1\n" +
        "15\t\t1\tT2\n" +
        "16\t106\t\tT1\n" +
        "17\t107\t2\tT2\n" +
        "\t108\t4\tT1\n" +
        "19\t109\t3\tT2\n" +
        "20\t110\t2\tT1\n" +
        "\t111\t4\tT2\n" +
        "\t112\t5\tT1\n";

    private static readonly double[] SiteTwo = [11, 14, 17, 20];

    // Chooses the roles, then a filter in the setup's Filter dialog - one "is any of" condition on the column, with what to
    // select in its Choose Values dialog - applies it, and confirms the setup.
    private static async Task<GraphConfiguration?> ConfirmFilteredAsync(
        GraphSetupViewModel setup,
        (string Role, string Column)[] roles,
        string filterColumn,
        Action<FilterValueChooserViewModel> select)
    {
        foreach (var (roleName, columnName) in roles)
        {
            var role = setup.Roles.Single(candidate => candidate.DisplayName == roleName || candidate.Role.ToString() == roleName);
            var option = role.Options.Single(candidate => !candidate.IsNone && candidate.Name == columnName);
            if (role.AllowsMultiple)
            {
                role.SelectedOptions.Add(option);
            }
            else
            {
                role.Choose(option);
            }
        }

        var editor = setup.CreateFilterEditor();
        editor.AddConditionCommand.Execute(null);
        var condition = Assert.Single(editor.Conditions);
        condition.SelectedColumn = editor.Columns.Single(column => column.Name == filterColumn);
        Assert.Equal(RowFilterOperator.IsAnyOf, condition.SelectedOperator!.Operator);

        var chooser = condition.CreateValueChooser()!;
        await chooser.Loading;
        select(chooser);
        Assert.True(chooser.TryApply(out var choice));
        condition.ApplyValues(choice!);
        Assert.True(editor.TryApply(out var edit));
        setup.Filter = edit!.Filter;
        return setup.Confirm();
    }

    private static void Only(FilterValueChooserViewModel editor, params string[] labels)
    {
        editor.ClearCommand.Execute(null);
        foreach (var value in editor.Values.Where(value => labels.Contains(value.Label)))
        {
            value.IsSelected = true;
        }
    }

    private static async Task<IReadOnlyList<(GraphRenderModel Frame, IGraphPlotRenderer? Plot)>> DrawFilteredAsync(
        Runtime runtime,
        GraphType type,
        (string Role, string Column)[] roles,
        string filterColumn,
        Action<FilterValueChooserViewModel> select,
        GraphVariableLayout layout = GraphVariableLayout.Together)
    {
        var before = runtime.GraphWindows.Shown.Count;
        runtime.GraphDialogs.Layout = layout;
        runtime.GraphDialogs.AnswerAsync = setup => ConfirmFilteredAsync(setup, roles, filterColumn, select);
        await Command(runtime, type).ExecuteAsync(null);
        runtime.GraphDialogs.AnswerAsync = null;
        return [.. runtime.GraphWindows.Shown.Skip(before)];
    }

    private static double SampleStandardDeviation(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1));
    }

    // ---- Every graph type, the group being the filter column ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task ADistributionGraphFilteredToOneSiteDescribesOnlyThatSite(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (frame, _) = Assert.Single(await DrawFilteredAsync(runtime, type,
            [("Variable", "PS_RAW"), ("Group", "Site")], "Site", editor => Only(editor, "2")));

        Assert.Equal(new RowFilter(new NumericValueSetCondition(runtime.Graphs.LastConfiguration!.Filter!.Conditions[0].ColumnId, [2])), runtime.Graphs.LastConfiguration.Filter);
        var row = Assert.Single(frame.StatisticsPanel!.Rows);
        Assert.Equal("2", row.Label);
        Assert.Equal(SiteTwo.Length, row.Count);
        Assert.Equal(SiteTwo.Average(), row.Mean, 12);
        Assert.Equal(SampleStandardDeviation(SiteTwo), row.StandardDeviation!.Value, 12);
        Assert.Equal(["2"], frame.Legend!.Entries.Select(entry => entry.Label));
        Assert.Empty(runtime.GraphDialogs.Errors);
    }

    [Fact]
    public async Task AScatterPlotFilteredToOneSiteDrawsOnlyItsPoints()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (_, plot) = Assert.Single(await DrawFilteredAsync(runtime, GraphType.ScatterPlot,
            [("X-axis", "PS_RAW"), ("Y-axis", "Reg2"), ("Group", "Site")], "Site", editor => Only(editor, "2")));

        var model = Assert.IsType<ScatterRenderer>(plot).Model;
        var series = Assert.Single(model.Series);
        Assert.Equal("2", series.Label);
        Assert.Equal(SiteTwo.Select(x => new ScatterPoint(x, x + 90)), series.Points.ToArray());
    }

    [Fact]
    public async Task ABoxPlotOfSeveralVariablesFilteredToOneSiteHasOneBoxEach()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (frame, plot) = Assert.Single(await DrawFilteredAsync(runtime, GraphType.BoxPlot,
            [("Variable", "PS_RAW"), ("Variable", "Reg2"), ("Group", "Site")], "Site", editor => Only(editor, "2")));

        var model = Assert.IsType<BoxPlotRenderer>(plot).Model;
        Assert.Equal(["PS_RAW / 2", "Reg2 / 2"], model.Categories);
        Assert.Equal([4, 4], model.Boxes.Select(box => box.ObservationCount));
        Assert.Equal(["2"], frame.Legend!.Entries.Select(entry => entry.Label));
    }

    // ---- The filter column is not the group ----

    [Fact]
    public async Task AGroupedGraphFilteredByAnotherColumnKeepsItsGroupsOfTheKeptRows()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (frame, _) = Assert.Single(await DrawFilteredAsync(runtime, GraphType.Histogram,
            [("Variable", "PS_RAW"), ("Group", "Tester")], "Site", editor => Only(editor, "1", "3")));

        // Sites 1 and 3: T1 10 and 12; T2 15 and 19.
        Assert.Equal(["T1", "T2"], frame.StatisticsPanel!.Rows.Select(row => row.Label));
        Assert.Equal([2, 2], frame.StatisticsPanel.Rows.Select(row => row.Count));
        Assert.Equal([11.0, 17.0], frame.StatisticsPanel.Rows.Select(row => row.Mean));
        var configuration = runtime.Graphs.LastConfiguration!;
        Assert.NotEqual(configuration.FindColumnId(GraphVariableRole.Group), configuration.Filter!.Conditions[0].ColumnId);
    }

    [Fact]
    public async Task AGraphWithoutAGroupCanBeFiltered()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (frame, _) = Assert.Single(await DrawFilteredAsync(runtime, GraphType.EmpiricalCdf,
            [("Variable", "PS_RAW")], "Tester", editor => Only(editor, "T2")));

        // T2: 11, 13, 15, 17, 19 (row 12 has no PS_RAW).
        var row = Assert.Single(frame.StatisticsPanel!.Rows);
        Assert.Equal(5, row.Count);
        Assert.Equal(15, row.Mean, 12);
        Assert.Null(frame.Legend);
    }

    // ---- Several variables ----

    [Fact]
    public async Task VariablesDrawnTogetherAreAllFiltered()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (frame, _) = Assert.Single(await DrawFilteredAsync(runtime, GraphType.Histogram,
            [("Variable", "PS_RAW"), ("Variable", "Reg2")], "Site", editor => Only(editor, "2")));

        Assert.Equal(["PS_RAW", "Reg2"], frame.StatisticsPanel!.Rows.Select(row => row.Label));
        Assert.Equal([4, 4], frame.StatisticsPanel.Rows.Select(row => row.Count));
        Assert.Equal([15.5, 105.5], frame.StatisticsPanel.Rows.Select(row => row.Mean));
    }

    [Fact]
    public async Task VariablesDrawnSeparatelyAreEachFiltered()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var shown = await DrawFilteredAsync(runtime, GraphType.ProbabilityPlot,
            [("Variable", "PS_RAW"), ("Variable", "Reg2")], "Site", editor => Only(editor, "2"), GraphVariableLayout.Separate);

        Assert.Equal(2, shown.Count);
        Assert.Equal([4, 4], shown.Select(graph => Assert.Single(graph.Frame.StatisticsPanel!.Rows).Count));
        Assert.Equal([15.5, 105.5], shown.Select(graph => graph.Frame.StatisticsPanel!.Rows[0].Mean));
    }

    // ---- Missing ----

    [Fact]
    public async Task OnlyMissingKeepsTheRowsWithoutAValue()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (frame, _) = Assert.Single(await DrawFilteredAsync(runtime, GraphType.Histogram,
            [("Variable", "PS_RAW"), ("Group", "Site")], "Site", editor =>
            {
                Assert.True(editor.HasMissingOption);
                Only(editor);
                editor.IncludeMissing = true;
            }));

        var row = Assert.Single(frame.StatisticsPanel!.Rows);
        Assert.Equal("(Missing)", row.Label);
        Assert.Equal(1, row.Count);
        Assert.Equal(16, row.Mean);
    }

    // ---- No filter ----

    [Fact]
    public async Task EveryRowSelectedDrawsExactlyTheUnfilteredGraph()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var (filtered, _) = Assert.Single(await DrawFilteredAsync(runtime, GraphType.Histogram,
            [("Variable", "PS_RAW"), ("Group", "Site")], "Site", editor => editor.SelectAllCommand.Execute(null)));
        Assert.Null(runtime.Graphs.LastConfiguration!.Filter);

        var plain = Assert.Single(await DrawAsync(runtime, GraphType.Histogram, GraphVariableLayout.Together, ["PS_RAW"], "Site"));

        Assert.Equal(Describe(plain), Describe(filtered));
        Assert.Null(runtime.Graphs.LastConfiguration!.Filter);
    }

    // ---- Nothing left ----

    [Fact]
    public async Task AFilterThatLeavesNothingToDrawIsSaidAndOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(FilterData);

        var shown = await DrawFilteredAsync(runtime, GraphType.Histogram,
            [("Variable", "PS_RAW")], "Site", editor => Only(editor, "5"));

        Assert.Empty(shown);
        Assert.Equal(["This graph has no data to plot."], runtime.GraphDialogs.Errors);
    }

    // ---- Too many values ----

    [Fact]
    public async Task AColumnWithMoreThanAThousandValuesCannotFilterButOneWithAThousandCan()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var rows = Enumerable.Range(0, ValueSetCondition.MaximumDistinctValues + 1)
            .Select(index => $"{index}\tS{index}\t{(index == ValueSetCondition.MaximumDistinctValues ? 0 : index)}");
        await runtime.PasteAsync("Reg\tSerial\tThousand\n" + string.Join("\n", rows) + "\n");
        runtime.GraphDialogs.Answer = _ => null;
        await runtime.Shell.HistogramCommand.ExecuteAsync(null);
        var setup = runtime.GraphDialogs.LastSetup;
        var editor = setup.CreateFilterEditor();
        editor.AddConditionCommand.Execute(null);
        var condition = Assert.Single(editor.Conditions);

        // More than 1,000 values: the list is truncated, so no value can be chosen - selecting every value shown can never
        // mean every row.
        condition.SelectedColumn = editor.Columns.Single(column => column.Name == "Serial");
        var serial = condition.CreateValueChooser()!;
        await serial.Loading;

        Assert.True(serial.HasTooManyValues);
        Assert.Empty(serial.Values);
        Assert.False(serial.CanApply);
        Assert.False(serial.SelectAllCommand.CanExecute(null));
        Assert.Equal(FilterValueChooserViewModel.TooManyValuesMessage, serial.Message);
        Assert.False(editor.CanApply);

        // Exactly 1,000: the list is complete, every value can be chosen, and every value is every row.
        condition.SelectedColumn = editor.Columns.Single(column => column.Name == "Thousand");
        var thousand = condition.CreateValueChooser()!;
        await thousand.Loading;

        Assert.False(thousand.HasTooManyValues);
        Assert.Equal(ValueSetCondition.MaximumDistinctValues, thousand.Values.Count);
        Assert.True(thousand.TryApply(out var choice));
        Assert.False(choice!.Available.HasMore);
        condition.ApplyValues(choice);
        Assert.True(editor.TryApply(out var edit));
        Assert.Null(edit!.Filter);
    }
}
