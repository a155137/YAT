using System.Globalization;
using System.Text;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// Graphs of many variables from the Graph menu (Task #060), over a real project: fifty variables picked with Select All
// from fifty-five, drawn together - grouped by a numeric column in its order (Task #059), after a row filter - under a
// title that counts them, and in panels (Task #058).
public partial class GraphCommandTests
{
    private static readonly int[] Sites = [4, 2, 3, 1];

    // P01..P55, a numeric SITE 4, 2, 3, 1 and a text LOT, twelve rows.
    private static string ManyVariablesData()
    {
        var text = new StringBuilder();
        text.Append(string.Join("\t", Enumerable.Range(1, 55).Select(index => $"P{index:00}"))).Append("\tSITE\tLOT\n");
        for (var row = 0; row < 12; row++)
        {
            text.Append(string.Join("\t", Enumerable.Range(1, 55).Select(index => (index + (row * 0.01)).ToString("0.00", CultureInfo.InvariantCulture))));
            text.Append($"\t{Sites[row % 4]}\t{(row % 3 == 0 ? "B" : "A")}\n");
        }

        return text.ToString();
    }

    private static GraphConfiguration? SelectAllAndConfirm(GraphSetupViewModel setup, string? group, Action<GraphSetupViewModel>? options = null)
    {
        var variables = setup.Roles.Single(role => role.AllowsMultiple);
        variables.SelectAll();
        if (group is not null)
        {
            var grouping = setup.Roles.Single(role => role.Role == GraphVariableRole.Group);
            grouping.SelectedOption = grouping.Options.Single(option => option.Name == group);
        }

        options?.Invoke(setup);
        return setup.Confirm();
    }

    [Fact]
    public async Task FiftyVariablesInOneBoxPlotGroupedAndFilteredAreDrawnUnderACountingTitle()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(ManyVariablesData());
        runtime.GraphDialogs.Answer = setup => SelectAllAndConfirm(setup, "SITE", setup =>
        {
            var lot = setup.AvailableColumns.Single(option => option.Name == "LOT").WorksheetColumnId!.Value;
            setup.Filter = new RowFilter(new TextValueSetCondition(lot, ["A"]));
        });

        await Command(runtime, GraphType.BoxPlot).ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Equal("Boxplot of 50 variables", frame.Title);
        var model = Assert.IsType<BoxPlotRenderer>(plot).Model;
        Assert.Equal(200, model.Categories.Count);
        Assert.Equal("P01 / 1", model.Categories[0]);
        Assert.Equal("P50 / 4", model.Categories[^1]);
        Assert.Equal([("1", 0), ("2", 1), ("3", 2), ("4", 3)], frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
        Assert.All(model.Boxes, box => Assert.Equal(2, box.ObservationCount));
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task FiftyVariablesTogetherAreOneGraphWithAStatisticsRowEach(GraphType type)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(ManyVariablesData());
        runtime.GraphDialogs.Answer = setup => SelectAllAndConfirm(setup, null);

        await Command(runtime, type).ExecuteAsync(null);

        var (frame, _) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.EndsWith(" of 50 variables", frame.Title, StringComparison.Ordinal);
        Assert.Equal(50, frame.Legend!.Entries.Count);
        Assert.Equal(50, frame.StatisticsPanel!.Rows.Count);
        Assert.Equal("P01", frame.StatisticsPanel.Rows[0].Label);
    }

    [Fact]
    public async Task TwentyVariablesTogetherInPanelsKeepEveryPanelsColours()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(ManyVariablesData());
        string[] twenty = [.. Enumerable.Range(1, 20).Select(index => $"P{index:00}")];
        runtime.GraphDialogs.Answer = setup =>
        {
            var panel = setup.Roles.Single(role => role.Role == GraphVariableRole.Panel);
            panel.SelectedOption = panel.Options.Single(option => option.Name == "SITE");
            return ConfirmWithVariables(setup, twenty, null);
        };

        await Command(runtime, GraphType.EmpiricalCdf).ExecuteAsync(null);

        var graph = runtime.GraphWindows.Graphs[^1];
        Assert.Equal("Empirical CDF of 20 variables", graph.Frame.Title);
        Assert.Equal(["SITE = 1", "SITE = 2", "SITE = 3", "SITE = 4"], runtime.GraphWindows.Panels[^1]!.Select(panel => panel.Title));
        Assert.Equal(twenty.Select((name, index) => (name, index)), graph.Frame.Legend!.Entries.Select(entry => (entry.Label, entry.SeriesIndex)));
    }
}
