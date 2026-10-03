using System.Globalization;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Analyses;
using YAT.Application.Filtering;
using YAT.Application.Graphs;
using YAT.app.Analyses;
using YAT.app.Composition;
using YAT.app.Graphs;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// The row filter in Descriptive Statistics, Capability Analysis and the graphs, over a real project (Task #053): the
// same Filter editor in every setup, every count and statistic over the kept rows only - N and Missing included - a
// short note on a filtered result, "No rows match the filter." when none is kept, and every result exactly as before
// without a filter.
public class AnalysisRowFilterCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    // Site, Current and Bin of ten rows: row 4 has no Current, row 7 no Site, row 8 no Bin.
    private const string Data =
        "Site\tCurrent\tBin\n" +
        "1\t14.4\t1\n" +
        "2\t14.5\t1\n" +
        "3\t15.0\t3\n" +
        "5\t\t1\n" +
        "7\t15.5\t2\n" +
        "1\t15.6\t1\n" +
        "\t15.0\t1\n" +
        "3\t14.9\t\n" +
        "5\t15.2\t3\n" +
        "7\t14.6\t1\n";

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            Composition = new CompositionRoot(new FixedTimeProvider(Now), Directory.File("temp"));
            Workspace = Composition.CreateProjectWorkspace();
            Lifecycle = Composition.CreateProjectLifecycle(Workspace, Clipboard, Clipboard, ProjectDialogs);
            Statistics = Composition.CreateDescriptiveStatistics(AnalysisDialogs, Results);
            Capability = Composition.CreateCapabilityAnalysis(CapabilityDialogs, Results);
            Graphs = Composition.CreateGraphSetup(GraphDialogs, GraphWindows);
            Shell = Composition.CreateMainWindowShellViewModel(Lifecycle, Graphs, Statistics, Capability);
        }

        public TemporaryDirectory Directory { get; } = new();

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public FakeProjectLifecycleDialogs ProjectDialogs { get; } = new();

        public FakeAnalysisSetupDialogs AnalysisDialogs { get; } = new();

        public FakeCapabilityAnalysisSetupDialogs CapabilityDialogs { get; } = new();

        public FakeGraphSetupDialogs GraphDialogs { get; } = new();

        public FakeGraphWindowPresenter GraphWindows { get; } = new();

        public FakeAnalysisResultPresenter Results { get; } = new();

        public ProjectLifecycleController Lifecycle { get; }

        public DescriptiveStatisticsController Statistics { get; }

        public CapabilityAnalysisController Capability { get; }

        public GraphSetupController Graphs { get; }

        public MainWindowShellViewModel Shell { get; }

        public MainWindowViewModel Project => Lifecycle.Project!;

        public async Task StartAsync(string data = Data)
        {
            Assert.True(await Shell.StartAsync());
            await Project.GridLoadTask;
            Clipboard.Text = data;
            await Project.PasteCommand.ExecuteAsync(null);
            await Project.GridLoadTask;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Directory.Dispose();
        }
    }

    // The engineering filter: Site is any of 1, 3, 5, 7 AND Current >= 14.5 AND Current <= 15.5 AND Bin is not any of {3}
    // - which keeps rows 5 (15.5), 8 (14.9, Bin Missing - not 3) and 10 (14.6).
    private static RowFilter Engineering(Func<string, Guid> column) => new(
    [
        new NumericValueSetCondition(column("Site"), [1, 3, 5, 7]),
        new NumericComparisonCondition(column("Current"), NumericComparison.GreaterOrEqual, 14.5),
        new NumericComparisonCondition(column("Current"), NumericComparison.LessOrEqual, 15.5),
        new NumericValueSetCondition(column("Bin"), [3], exclude: true)
    ]);

    private static readonly double[] Kept = [15.5, 14.9, 14.6];

    private static string Format(double value) => YAT.app.Analyses.AnalysisNumberFormat.Statistic(value);

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1));
    }

    private static string Cell(AnalysisResultTable table, int row, string columnName) =>
        table.Rows[row].Cells[table.Columns.Select((column, index) => (column.Name, index)).Single(column => column.Name == columnName).index];

    private static Func<AnalysisSetupViewModel, AnalysisConfiguration?> Descriptive(Func<Func<string, Guid>, RowFilter?> filter) => setup =>
    {
        setup.Variables.Single(variable => variable.Name == "Current").IsSelected = true;
        setup.Filter = filter(name => setup.GroupOptions.Single(option => option.Name == name).WorksheetColumnId!.Value);
        return setup.Confirm();
    };

    private static Func<CapabilityAnalysisSetupViewModel, CapabilityAnalysisConfiguration?> Capability(Func<Func<string, Guid>, RowFilter?> filter) => setup =>
    {
        var current = setup.Variables.Single(variable => variable.Name == "Current");
        current.IsSelected = true;
        current.LowerSpecificationLimitText = "14";
        current.UpperSpecificationLimitText = "16";
        foreach (var statistic in setup.Statistics)
        {
            statistic.IsSelected = statistic.Statistic is CapabilityStatistic.Count or CapabilityStatistic.Missing or CapabilityStatistic.Mean;
        }

        setup.Filter = filter(name => setup.GroupOptions.Single(option => option.Name == name).WorksheetColumnId!.Value);
        return setup.Confirm();
    };

    // ---- Descriptive Statistics ----

    [Fact]
    public async Task DescriptiveStatisticsAreOverTheKeptRowsOnly()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.AnalysisDialogs.Answer = Descriptive(Engineering);

        await runtime.Shell.DescriptiveStatisticsCommand.ExecuteAsync(null);

        var table = Assert.Single(runtime.Results.Shown);
        Assert.Equal(("3", "0"), (Cell(table, 0, "N"), Cell(table, 0, "Missing")));
        Assert.Equal(Format(Kept.Average()), Cell(table, 0, "Mean"));
        Assert.Equal(Format(StandardDeviation(Kept)), Cell(table, 0, "StDev"));
        Assert.Equal((Format(14.6), Format(15.5)), (Cell(table, 0, "Min"), Cell(table, 0, "Max")));
        Assert.Equal("Filter: 4 conditions", table.Note);
        Assert.Equal(4, runtime.Statistics.LastConfiguration!.Filter!.Conditions.Count);
    }

    [Fact]
    public async Task MissingIsCountedOverTheKeptRowsOnly()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.AnalysisDialogs.Answer = Descriptive(column => new RowFilter(new NumericValueSetCondition(column("Site"), [5])));

        await runtime.Shell.DescriptiveStatisticsCommand.ExecuteAsync(null);

        // Site 5 is rows 4 (no Current) and 9 (15.2).
        var table = Assert.Single(runtime.Results.Shown);
        Assert.Equal(("1", "1", Format(15.2)), (Cell(table, 0, "N"), Cell(table, 0, "Missing"), Cell(table, 0, "Mean")));
        Assert.Equal("Filter: 1 condition", table.Note);
    }

    [Fact]
    public async Task WithoutAFilterTheResultIsAsItWas()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.AnalysisDialogs.Answer = Descriptive(_ => null);

        await runtime.Shell.DescriptiveStatisticsCommand.ExecuteAsync(null);

        var table = Assert.Single(runtime.Results.Shown);
        Assert.Equal(("9", "1"), (Cell(table, 0, "N"), Cell(table, 0, "Missing")));
        Assert.Null(table.Note);
    }

    [Fact]
    public async Task NoRowMatchingIsSaidSoForDescriptiveStatistics()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.AnalysisDialogs.Answer = Descriptive(column => new RowFilter(new NumericComparisonCondition(column("Current"), NumericComparison.Greater, 100)));

        await runtime.Shell.DescriptiveStatisticsCommand.ExecuteAsync(null);

        Assert.Equal(["No rows match the filter."], runtime.AnalysisDialogs.Errors);
        Assert.Empty(runtime.Results.Shown);
    }

    // ---- Capability Analysis ----

    [Fact]
    public async Task CapabilityMeasuresExactlyTheKeptObservations()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.CapabilityDialogs.Answer = Capability(Engineering);

        await runtime.Shell.CapabilityAnalysisCommand.ExecuteAsync(null);

        var filtered = Assert.Single(runtime.Results.Shown);
        Assert.Equal(("3", "0", Format(Kept.Average())), (Cell(filtered, 0, "N"), Cell(filtered, 0, "Missing"), Cell(filtered, 0, "Mean")));
        Assert.Equal("Filter: 4 conditions", filtered.Note);

        // The same three observations pasted on their own give the very same result.
        using var alone = new Runtime();
        await alone.StartAsync("Site\tCurrent\tBin\n" + string.Join("\n", Kept.Select(value => $"1\t{value.ToString(CultureInfo.InvariantCulture)}\t1")) + "\n");
        alone.CapabilityDialogs.Answer = Capability(_ => null);
        await alone.Shell.CapabilityAnalysisCommand.ExecuteAsync(null);

        Assert.Equal(Assert.Single(alone.Results.Shown).Rows[0].Cells, filtered.Rows[0].Cells);
    }

    [Fact]
    public async Task NoRowMatchingIsSaidSoForCapability()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.CapabilityDialogs.Answer = Capability(column => new RowFilter(new NumericComparisonCondition(column("Current"), NumericComparison.Greater, 100)));

        await runtime.Shell.CapabilityAnalysisCommand.ExecuteAsync(null);

        Assert.Equal(["No rows match the filter."], runtime.CapabilityDialogs.Errors);
        Assert.Empty(runtime.Results.Shown);
    }

    // ---- Graphs ----

    [Fact]
    public async Task NoRowMatchingIsSaidSoForAGraph()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.GraphDialogs.AnswerAsync = setup =>
        {
            var role = setup.Roles.Single(candidate => candidate.AllowsMultiple);
            role.SelectedOptions.Add(role.Options.Single(option => option.Name == "Current"));
            setup.Filter = new RowFilter(new NumericComparisonCondition(role.Options.Single(option => option.Name == "Current").WorksheetColumnId!.Value, NumericComparison.Greater, 100));
            return Task.FromResult(setup.Confirm());
        };

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Equal([GraphSetupController.NoMatchingRowsMessage], runtime.GraphDialogs.Errors);
        Assert.Empty(runtime.GraphWindows.Shown);
    }

    // ---- One editor ----

    [Fact]
    public async Task EverySetupFiltersWithTheSameEditor()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.AnalysisDialogs.Answer = _ => null;
        runtime.CapabilityDialogs.Answer = _ => null;
        runtime.GraphDialogs.Answer = _ => null;
        await runtime.Shell.DescriptiveStatisticsCommand.ExecuteAsync(null);
        await runtime.Shell.CapabilityAnalysisCommand.ExecuteAsync(null);
        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        var descriptive = runtime.AnalysisDialogs.LastSetup;
        var capability = runtime.CapabilityDialogs.Shown[^1];
        var graph = runtime.GraphDialogs.LastSetup;
        Assert.True(descriptive.SupportsFilter && capability.SupportsFilter && graph.SupportsFilter);
        Assert.Equal(("Filter: All rows", "Filter: All rows", "All rows"), (descriptive.FilterSummary, capability.FilterSummary, graph.FilterSummary));

        var editors = new[] { descriptive.CreateFilterEditor(), capability.CreateFilterEditor(), graph.CreateFilterEditor() };
        Assert.All(editors, editor => Assert.Equal(["Site", "Current", "Bin"], editor.Columns.Select(column => column.Name)));

        // The values come from the worksheet itself, through the session, for any of them.
        editors[0].AddConditionCommand.Execute(null);
        var condition = editors[0].Conditions[0];
        condition.SelectedColumn = condition.Columns.Single(column => column.Name == "Site");
        var chooser = condition.CreateValueChooser()!;
        await chooser.Loading;
        Assert.Equal(["1", "2", "3", "5", "7"], chooser.Values.Select(value => value.Label));
        Assert.True(chooser.HasMissingOption);
    }
}
