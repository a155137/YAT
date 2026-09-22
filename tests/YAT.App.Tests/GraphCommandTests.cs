using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
using YAT.app.Analyses;
using YAT.app.Composition;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// The Graph menu over a real project: the setup is built from the active worksheet's stored column metadata, and a
// confirmed setup produces a validated configuration and opens a graph window.
public class GraphCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            Composition = new CompositionRoot(new FixedTimeProvider(Now), Directory.File("temp"));
            Workspace = Composition.CreateProjectWorkspace();
            Lifecycle = Composition.CreateProjectLifecycle(Workspace, Clipboard, Clipboard, ProjectDialogs);
            Graphs = Composition.CreateGraphSetup(GraphDialogs, GraphWindows);
            Statistics = Composition.CreateDescriptiveStatistics(AnalysisDialogs, AnalysisResults);
            Capability = Composition.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), AnalysisResults);
            Shell = Composition.CreateMainWindowShellViewModel(Lifecycle, Graphs, Statistics, Capability);
        }

        public TemporaryDirectory Directory { get; } = new();

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public FakeProjectLifecycleDialogs ProjectDialogs { get; } = new();

        public FakeGraphSetupDialogs GraphDialogs { get; } = new();

        public FakeGraphWindowPresenter GraphWindows { get; } = new();

        public FakeAnalysisSetupDialogs AnalysisDialogs { get; } = new();

        public FakeAnalysisResultPresenter AnalysisResults { get; } = new();

        public ProjectLifecycleController Lifecycle { get; }

        public GraphSetupController Graphs { get; }

        public DescriptiveStatisticsController Statistics { get; }

        public CapabilityAnalysisController Capability { get; }

        public MainWindowShellViewModel Shell { get; }

        public MainWindowViewModel Project => Lifecycle.Project!;

        public async Task StartAsync()
        {
            Assert.True(await Shell.StartAsync());
            await Project.GridLoadTask;
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await Project.PasteCommand.ExecuteAsync(null);
            await Project.GridLoadTask;
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Directory.Dispose();
        }
    }

    // Confirms the setup by giving every required role the next compatible column (X the first, Y the second, ...),
    // leaving optional roles alone.
    private static GraphConfiguration? ConfirmWithFirstColumns(GraphSetupViewModel setup)
    {
        var used = new List<Guid>();
        foreach (var role in setup.Roles.Where(role => role.IsRequired))
        {
            var option = role.Options.FirstOrDefault(candidate => !candidate.IsNone && !used.Contains(candidate.WorksheetColumnId!.Value));
            role.SelectedOption = option;
            if (option is not null)
            {
                used.Add(option.WorksheetColumnId!.Value);
            }
        }

        return setup.Confirm();
    }

    // Confirms the setup with named columns in named roles ("X-axis", "Y-axis", "Group").
    private static GraphConfiguration? ConfirmWith(GraphSetupViewModel setup, params (string Role, string Column)[] assignments)
    {
        foreach (var (roleName, columnName) in assignments)
        {
            var role = setup.Roles.Single(candidate => candidate.DisplayName == roleName);
            role.SelectedOption = role.Options.Single(option => !option.IsNone && option.Name == columnName);
        }

        return setup.Confirm();
    }

    [Fact]
    public async Task AGraphCommandShowsTheSetupForTheActiveWorksheetsColumns()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("No\tSITE\tLot\tReg1\tReg2\n1\t1\tA\t5\t0.132\n");
        runtime.GraphDialogs.Answer = _ => null;

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        var setup = runtime.GraphDialogs.LastSetup;
        Assert.Equal("Scatter Plot", setup.Title);
        Assert.Equal(runtime.Project.SelectedWorksheet!.Id, setup.WorksheetId);
        Assert.Equal("Sheet1", setup.WorksheetName);
        Assert.Equal(["No", "SITE", "Lot", "Reg1", "Reg2"], setup.AvailableColumns.Select(option => option.Name));
        Assert.Equal(
            ["No", "SITE", "Reg1", "Reg2"],
            Assert.Single(setup.Roles, role => role.Role == GraphVariableRole.X).Options.Select(option => option.Name));
        Assert.Equal(
            ["(None)", "No", "SITE", "Lot", "Reg1", "Reg2"],
            Assert.Single(setup.Roles, role => role.Role == GraphVariableRole.Group).Options.Select(option => option.Name));
    }

    [Theory]
    [InlineData(GraphType.ScatterPlot, "Scatter Plot")]
    [InlineData(GraphType.Histogram, "Histogram")]
    [InlineData(GraphType.BoxPlot, "Box Plot")]
    [InlineData(GraphType.ProbabilityPlot, "Probability Plot")]
    [InlineData(GraphType.EmpiricalCdf, "Empirical CDF")]
    public async Task EveryGraphMenuCommandOpensItsOwnSetup(GraphType graphType, string title)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t2\n");
        runtime.GraphDialogs.Answer = _ => null;

        await Command(runtime, graphType).ExecuteAsync(null);

        Assert.Equal(title, runtime.GraphDialogs.LastSetup.Title);
        Assert.Equal(graphType, runtime.GraphDialogs.LastSetup.GraphType);
    }

    [Fact]
    public async Task ConfirmingTheSetupReturnsAValidatedConfiguration()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tLot\n1\t2\tA\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        var configuration = runtime.Graphs.LastConfiguration;
        Assert.NotNull(configuration);
        Assert.Equal(GraphType.ScatterPlot, configuration.GraphType);
        Assert.Equal(runtime.Project.SelectedWorksheet!.Id, configuration.WorksheetId);

        var columns = await runtime.Workspace.CurrentSession!.WorksheetColumns.GetByWorksheetIdAsync(configuration.WorksheetId, Token);
        Assert.Equal(columns[0].Id, configuration.FindColumnId(GraphVariableRole.X));
        Assert.Equal(columns[1].Id, configuration.FindColumnId(GraphVariableRole.Y));
        Assert.Null(configuration.FindColumnId(GraphVariableRole.Group));
        Assert.True(new GraphConfigurationValidator().Validate(configuration, columns).IsValid);
    }

    [Fact]
    public async Task CancellingTheSetupChangesNothing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t2\n");
        runtime.GraphDialogs.Answer = _ => null;

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Single(runtime.GraphDialogs.Shown);
        Assert.Null(runtime.Graphs.LastConfiguration);
        Assert.Empty(runtime.GraphDialogs.Errors);
        Assert.Null(runtime.Project.ErrorMessage);
        Assert.Empty(runtime.GraphWindows.Shown);
    }

    // A confirmed scatter setup reads the worksheet through the session, prepares the plot and opens a graph window
    // showing the worksheet's own values.
    [Fact]
    public async Task ConfirmingAScatterSetupOpensAGraphWindowOfTheWorksheetData()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n2\t20\n3\t30\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Equal("Scatterplot of Reg2 vs Reg1", frame.Title);
        Assert.Equal("Reg1", frame.XAxis.Title);
        Assert.Equal("Reg2", frame.YAxis.Title);
        Assert.NotEmpty(frame.XAxis.Ticks);
        Assert.NotEmpty(frame.YAxis.Ticks);
        Assert.Null(frame.Legend);

        var scatter = Assert.IsType<ScatterRenderer>(plot).Model;
        Assert.Equal(3, scatter.SourcePointCount);
        Assert.Equal(3, scatter.RenderedPointCount);
        Assert.False(scatter.WasSampled);
        Assert.Equal(
            [new ScatterPoint(1, 10), new ScatterPoint(2, 20), new ScatterPoint(3, 30)],
            Assert.Single(scatter.Series).Points.ToArray());
    }

    // A group column becomes one series per value, with the legend the graph window shows.
    [Fact]
    public async Task AGroupedScatterSetupOpensAGraphWindowWithOneSeriesPerGroup()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tSITE\n1\t10\t1\n2\t20\t2\n3\t30\t1\n4\t40\t\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("X-axis", "Reg1"), ("Y-axis", "Reg2"), ("Group", "SITE"));

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        var scatter = Assert.IsType<ScatterRenderer>(plot).Model;

        Assert.Equal(["1", "2", ScatterRenderModelBuilder.MissingGroupLabel], scatter.Series.Select(series => series.Label));
        Assert.Equal([0, 1, 2], scatter.Series.Select(series => series.SeriesIndex));
        Assert.Equal(4, scatter.RenderedPointCount);
        Assert.NotNull(frame.Legend);
        Assert.Equal("SITE", frame.Legend.Title);
        Assert.Equal(["1", "2", ScatterRenderModelBuilder.MissingGroupLabel], frame.Legend.Entries.Select(entry => entry.Label));
    }

    // A confirmed histogram setup counts the worksheet's own values into bins and opens a graph window.
    [Fact]
    public async Task ConfirmingAHistogramSetupOpensAGraphWindowOfTheWorksheetData()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n2\t20\n3\t30\n4\t40\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("Graph variables", "Reg1"));

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Equal("Histogram of Reg1", frame.Title);
        Assert.Equal("Reg1", frame.XAxis.Title);
        Assert.Equal("Frequency", frame.YAxis.Title);
        Assert.Equal(0, frame.YAxis.Range.Minimum);
        Assert.Null(frame.Legend);

        var histogram = Assert.IsType<HistogramRenderer>(plot).Model;
        Assert.Equal(4, histogram.SourceObservationCount);
        Assert.Equal(4, Assert.Single(histogram.Series).Counts.Sum());
        Assert.Equal(1, histogram.Bins[0].LowerEdge);
        Assert.Equal(4, histogram.Bins[^1].UpperEdge);
    }

    // A group column becomes one series per value, counted into the same bins, with the legend the window shows.
    [Fact]
    public async Task AGroupedHistogramSetupCountsEveryGroupIntoTheSameBins()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tSITE\n1\t1\n2\t2\n3\t1\n4\t\n");
        runtime.GraphDialogs.Answer = setup =>
            ConfirmWith(setup, ("Graph variables", "Reg1"), ("Categorical variable for grouping", "SITE"));

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        var histogram = Assert.IsType<HistogramRenderer>(plot).Model;

        Assert.Equal(["1", "2", HistogramRenderModelBuilder.MissingGroupLabel], histogram.Series.Select(series => series.Label));
        Assert.All(histogram.Series, series => Assert.Equal(histogram.Bins.Count, series.Counts.Count));
        Assert.Equal(4, histogram.SourceObservationCount);
        Assert.NotNull(frame.Legend);
        Assert.Equal("SITE", frame.Legend.Title);
    }

    // A histogram over a column with no values has nothing to count.
    [Fact]
    public async Task AHistogramSetupWithoutUsableObservationsOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg2\n10\n20\n");

        // A numeric column that was added but never filled in: a valid configuration with nothing behind it.
        runtime.Project.ColumnName = "Reg1";
        await runtime.Project.AddColumnCommand.ExecuteAsync(null);
        await runtime.Project.GridLoadTask;
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("Graph variables", "Reg1"));

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Equal(["This graph has no data to plot."], runtime.GraphDialogs.Errors);
    }

    // A confirmed probability plot setup ranks the worksheet's own values and opens a graph window.
    [Fact]
    public async Task ConfirmingAProbabilityPlotSetupOpensAGraphWindowOfTheWorksheetData()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n2\t20\n3\t30\n4\t40\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("Graph variables", "Reg1"));

        await runtime.Shell.ProbabilityPlotCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Equal("Normal Probability Plot of Reg1", frame.Title);
        Assert.Equal("Reg1", frame.XAxis.Title);
        Assert.Equal("Percent", frame.YAxis.Title);
        Assert.Contains(frame.YAxis.Ticks, tick => tick.Label == "50");
        Assert.Null(frame.Legend);

        var probability = Assert.IsType<ProbabilityPlotRenderer>(plot).Model;
        Assert.Equal(4, probability.SourceObservationCount);
        Assert.Equal(4, probability.RenderedPointCount);
        Assert.Equal([1, 2, 3, 4], Assert.Single(probability.Series).Points.ToArray().Select(point => point.Value));
        Assert.NotNull(probability.Series[0].FittedLine);
    }

    // A group column becomes one series per value, each ranked on its own, with the legend the window shows.
    [Fact]
    public async Task AGroupedProbabilityPlotRanksEveryGroupOnItsOwn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tSITE\n1\t1\n2\t2\n3\t1\n4\t\n");
        runtime.GraphDialogs.Answer = setup =>
            ConfirmWith(setup, ("Graph variables", "Reg1"), ("Categorical variable for grouping", "SITE"));

        await runtime.Shell.ProbabilityPlotCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        var probability = Assert.IsType<ProbabilityPlotRenderer>(plot).Model;

        Assert.Equal(["1", "2", ProbabilityPlotRenderModelBuilder.MissingGroupLabel], probability.Series.Select(series => series.Label));
        Assert.Equal([2, 1, 1], probability.Series.Select(series => series.Points.Length));
        Assert.Equal(4, probability.SourceObservationCount);
        Assert.NotNull(frame.Legend);
        Assert.Equal("SITE", frame.Legend.Title);
    }

    // A probability plot over a column with no values has nothing to rank.
    [Fact]
    public async Task AProbabilityPlotSetupWithoutUsableObservationsOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg2\n10\n20\n");

        runtime.Project.ColumnName = "Reg1";
        await runtime.Project.AddColumnCommand.ExecuteAsync(null);
        await runtime.Project.GridLoadTask;
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("Graph variables", "Reg1"));

        await runtime.Shell.ProbabilityPlotCommand.ExecuteAsync(null);

        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Equal(["This graph has no data to plot."], runtime.GraphDialogs.Errors);
    }

    // A confirmed empirical CDF setup turns the worksheet's own values into a step function and opens a graph window.
    [Fact]
    public async Task ConfirmingAnEmpiricalCdfSetupOpensAGraphWindowOfTheWorksheetData()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n1\t20\n1\t30\n2\t40\n3\t50\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("Graph variables", "Reg1"));

        await runtime.Shell.EmpiricalCdfCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Equal("Empirical CDF of Reg1", frame.Title);
        Assert.Equal("Reg1", frame.XAxis.Title);
        Assert.Equal("Percent", frame.YAxis.Title);
        Assert.Equal(0, frame.YAxis.Range.Minimum);
        Assert.Equal(100, frame.YAxis.Range.Maximum);
        Assert.Null(frame.Legend);

        // 1, 1, 1, 2, 3: one step at each distinct value, at the share of the sample it reaches.
        var cdf = Assert.IsType<EmpiricalCdfRenderer>(plot).Model;
        Assert.Equal(5, cdf.SourceObservationCount);
        Assert.Equal(
            [new EmpiricalCdfPoint(1, 60), new EmpiricalCdfPoint(2, 80), new EmpiricalCdfPoint(3, 100)],
            Assert.Single(cdf.Series).Points.ToArray());
    }

    // A group column becomes one series per value, each counted against its own size.
    [Fact]
    public async Task AGroupedEmpiricalCdfCountsEveryGroupAgainstItsOwnSize()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tSITE\n1\t1\n2\t2\n3\t1\n4\t\n");
        runtime.GraphDialogs.Answer = setup =>
            ConfirmWith(setup, ("Graph variables", "Reg1"), ("Categorical variable for grouping", "SITE"));

        await runtime.Shell.EmpiricalCdfCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        var cdf = Assert.IsType<EmpiricalCdfRenderer>(plot).Model;

        Assert.Equal(["1", "2", EmpiricalCdfRenderModelBuilder.MissingGroupLabel], cdf.Series.Select(series => series.Label));
        Assert.All(cdf.Series, series => Assert.Equal(100, series.Points.Span[^1].CumulativePercent, 1e-9));
        Assert.Equal(4, cdf.SourceObservationCount);
        Assert.NotNull(frame.Legend);
        Assert.Equal("SITE", frame.Legend.Title);
    }

    // An empirical CDF over a column with no values has nothing to describe.
    [Fact]
    public async Task AnEmpiricalCdfSetupWithoutUsableObservationsOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg2\n10\n20\n");

        runtime.Project.ColumnName = "Reg1";
        await runtime.Project.AddColumnCommand.ExecuteAsync(null);
        await runtime.Project.GridLoadTask;
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("Graph variables", "Reg1"));

        await runtime.Shell.EmpiricalCdfCommand.ExecuteAsync(null);

        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Equal(["This graph has no data to plot."], runtime.GraphDialogs.Errors);
    }

    // Every graph type of this version draws a real graph: none of them answers with the unimplemented message.
    [Theory]
    [InlineData(GraphType.ScatterPlot, typeof(ScatterRenderer))]
    [InlineData(GraphType.Histogram, typeof(HistogramRenderer))]
    [InlineData(GraphType.ProbabilityPlot, typeof(ProbabilityPlotRenderer))]
    [InlineData(GraphType.EmpiricalCdf, typeof(EmpiricalCdfRenderer))]
    public async Task EveryGraphTypeHasARealExecutionPath(GraphType graphType, Type renderer)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t2\n3\t4\n5\t6\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        await Command(runtime, graphType).ExecuteAsync(null);

        var (_, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.IsType(renderer, plot);
        Assert.Empty(runtime.GraphDialogs.Errors);
        Assert.NotNull(runtime.Graphs.LastConfiguration);
    }

    // A configuration can be valid while its columns hold nothing that can be plotted.
    [Fact]
    public async Task AScatterSetupWithoutUsableObservationsOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        // Every row misses one of the two values, so no row survives the graph's null rules.
        await runtime.PasteAsync("Reg1\tReg2\n1\t\n\t20\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Equal(["This graph has no data to plot."], runtime.GraphDialogs.Errors);
    }

    // A cancelled graph request leaves no window behind.
    [Fact]
    public async Task ACancelledScatterRequestOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n2\t20\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        // Cancelled while the setup is open: the configuration is confirmed, but its data is never loaded.
        using var cancellation = new CancellationTokenSource();
        runtime.GraphDialogs.Answer = setup =>
        {
            cancellation.Cancel();
            return ConfirmWithFirstColumns(setup);
        };

        await runtime.Graphs.ConfigureAsync(
            GraphType.ScatterPlot, runtime.Lifecycle.CurrentSession, runtime.Project.SelectedWorksheet, cancellation.Token);

        Assert.NotNull(runtime.Graphs.LastConfiguration);
        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Empty(runtime.GraphDialogs.Errors);
    }

    // Nothing to graph means nothing to show: the error path never opens a window.
    [Fact]
    public async Task AGraphCommandOnAWorksheetWithoutColumnsOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Equal(["This worksheet has no columns to graph."], runtime.GraphDialogs.Errors);
    }

    [Fact]
    public async Task TheSetupFollowsTheActiveWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n");
        await runtime.Project.NewWorksheetCommand.ExecuteAsync(null);
        await runtime.Project.GridLoadTask;
        await runtime.PasteAsync("Vth\tIdsat\n0.4\t1\n");
        runtime.GraphDialogs.Answer = _ => null;

        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Equal("Sheet2", runtime.GraphDialogs.LastSetup.WorksheetName);
        Assert.Equal(["Vth", "Idsat"], runtime.GraphDialogs.LastSetup.AvailableColumns.Select(option => option.Name));
    }

    [Fact]
    public async Task AfterAProjectSwitchTheSetupUsesTheNewProjectsWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("OldColumn\n1\n");
        var saved = runtime.Directory.File("Saved.yat");
        runtime.ProjectDialogs.SavePaths.Enqueue(saved);
        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        runtime.GraphDialogs.Answer = _ => null;

        // A new project: its Sheet1 has its own columns.
        Assert.True(await runtime.Lifecycle.NewProjectAsync());
        await runtime.Project.GridLoadTask;
        await runtime.PasteAsync("NewColumn\n2\n");
        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Equal(["NewColumn"], runtime.GraphDialogs.LastSetup.AvailableColumns.Select(option => option.Name));
        Assert.Equal(runtime.Project.SelectedWorksheet!.Id, runtime.GraphDialogs.LastSetup.WorksheetId);

        // And after opening the saved project again, its own columns.
        runtime.ProjectDialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        runtime.ProjectDialogs.OpenPaths.Enqueue(saved);
        Assert.True(await runtime.Lifecycle.OpenProjectAsync());
        await runtime.Project.GridLoadTask;
        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Equal(["OldColumn"], runtime.GraphDialogs.LastSetup.AvailableColumns.Select(option => option.Name));
    }

    [Fact]
    public async Task WithoutAnActiveWorksheetTheSetupDoesNotOpen()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Project.SelectedWorksheet = null;
        await runtime.Project.GridLoadTask;

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        Assert.Empty(runtime.GraphDialogs.Shown);
        Assert.Equal(["Select a worksheet before creating a graph."], runtime.GraphDialogs.Errors);
        Assert.Null(runtime.Graphs.LastConfiguration);
    }

    [Fact]
    public async Task AWorksheetWithoutColumnsDoesNotOpenTheSetup()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        Assert.Empty(runtime.GraphDialogs.Shown);
        Assert.Equal(["This worksheet has no columns to graph."], runtime.GraphDialogs.Errors);
    }

    [Fact]
    public async Task ColumnMetadataIsReadThroughTheSessionAndNeverStale()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t2\n");
        runtime.GraphDialogs.Answer = _ => null;

        // A column deleted after the first setup is gone from the next one.
        await runtime.Shell.HistogramCommand.ExecuteAsync(null);
        Assert.Equal(["Reg1", "Reg2"], runtime.GraphDialogs.LastSetup.AvailableColumns.Select(option => option.Name));

        runtime.Project.SelectColumn(runtime.Project.SelectedWorksheetColumns![0].Id);
        await runtime.Project.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        await runtime.Project.GridLoadTask;
        await runtime.Shell.HistogramCommand.ExecuteAsync(null);

        Assert.Equal(["Reg2"], runtime.GraphDialogs.LastSetup.AvailableColumns.Select(option => option.Name));
    }

    [Fact]
    public async Task GraphSetupDoesNotReadWorksheetValues()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n3\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        await runtime.Shell.EmpiricalCdfCommand.ExecuteAsync(null);

        // The setup and the configuration carry column metadata and ids only.
        var setup = runtime.GraphDialogs.LastSetup;
        Assert.All(setup.AvailableColumns, option => Assert.False(string.IsNullOrEmpty(option.DataTypeName)));
        Assert.DoesNotContain(
            typeof(GraphSetupViewModel).GetProperties(),
            property => property.PropertyType.Namespace?.StartsWith("YAT.Application.Abstractions", StringComparison.Ordinal) == true);
        Assert.NotNull(runtime.Graphs.LastConfiguration);
    }

    // ---- Box Plot ----

    // A box plot's variable role takes several columns, so its setup offers them as a multi-selection; every other
    // role keeps its single selection.
    [Fact]
    public async Task TheBoxPlotSetupTakesSeveralGraphVariablesAndOneGroupingColumn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tSITE\n1\t10\tA\n");
        runtime.GraphDialogs.Answer = _ => null;

        await runtime.Shell.BoxPlotCommand.ExecuteAsync(null);

        var setup = runtime.GraphDialogs.LastSetup;
        Assert.Equal("Box Plot", setup.Title);
        Assert.Equal(GraphType.BoxPlot, setup.GraphType);

        var variables = Assert.Single(setup.Roles, role => role.Role == GraphVariableRole.Variable);
        Assert.True(variables.AllowsMultiple);
        Assert.Equal("Graph variables", variables.DisplayName);
        Assert.Equal(["Reg1", "Reg2"], variables.Options.Select(option => option.Name));

        var group = Assert.Single(setup.Roles, role => role.Role == GraphVariableRole.Group);
        Assert.False(group.AllowsMultiple);
        Assert.Equal(["(None)", "Reg1", "Reg2", "SITE"], group.Options.Select(option => option.Name));
    }

    //
    [Fact]
    public async Task ConfirmingABoxPlotOfSeveralVariablesOpensAWindowWithOneBoxEach()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        // Reg1 is empty in the last row and Reg2 in the first: neither takes the other's observations away.
        await runtime.PasteAsync("Reg1\tReg2\n1\t\n2\t20\n3\t30\n4\t40\n5\t50\n\t60\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg1", "Reg2");

        await runtime.Shell.BoxPlotCommand.ExecuteAsync(null);

        var configuration = runtime.Graphs.LastConfiguration!;
        Assert.Equal(GraphType.BoxPlot, configuration.GraphType);
        Assert.Equal(2, configuration.FindColumnIds(GraphVariableRole.Variable).Count);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Equal("Boxplot of Reg1, Reg2", frame.Title);

        var model = Assert.IsType<BoxPlotRenderer>(plot).Model;
        Assert.Equal(["Reg1", "Reg2"], model.Categories);
        Assert.Equal([5, 5], model.Boxes.Select(box => box.ObservationCount));
        Assert.Equal([3, 40], model.Boxes.Select(box => box.Median));
        Assert.Null(frame.Legend);
    }

    //
    [Fact]
    public async Task ConfirmingAGroupedBoxPlotOpensAWindowWithOneBoxPerVariableAndGroup()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tSITE\n1\t10\tB\n2\t20\tA\n3\t30\tB\n4\t40\tA\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, ["Reg1", "Reg2"], "SITE");

        await runtime.Shell.BoxPlotCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        var model = Assert.IsType<BoxPlotRenderer>(plot).Model;

        Assert.Equal(["Reg1 / B", "Reg1 / A", "Reg2 / B", "Reg2 / A"], model.Categories);
        Assert.Equal(["B", "A"], frame.Legend!.Entries.Select(entry => entry.Label));

        // Site B keeps series 0 in both variables, site A series 1.
        Assert.Equal([0, 1, 0, 1], model.Boxes.Select(box => box.SeriesIndex));
    }

    //
    [Fact]
    public async Task ABoxPlotReadsItsVariablesInWorksheetOrderWhateverOrderTheyWerePicked()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tReg3\n1\t10\t100\n2\t20\t200\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg3", "Reg1");

        await runtime.Shell.BoxPlotCommand.ExecuteAsync(null);

        var model = Assert.IsType<BoxPlotRenderer>(Assert.Single(runtime.GraphWindows.Shown).Plot).Model;
        Assert.Equal(["Reg1", "Reg3"], model.Categories);
    }

    // Regression (real-user crash): the reported column through the whole Box Plot command - worksheet, session, graph
    // data, preparation - must open a graph window rather than throw.
    [Fact]
    public async Task ABoxPlotOfRepeatedDecimalsOpensAGraphWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg2\n" + string.Concat(Enumerable.Repeat("0.132\n0.157\n0.122\n0.133\n", 200)));
        runtime.GraphDialogs.Answer = setup => ConfirmWithVariables(setup, "Reg2");

        await runtime.Shell.BoxPlotCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Equal("Boxplot of Reg2", frame.Title);
        Assert.Equal(800, Assert.Single(Assert.IsType<BoxPlotRenderer>(plot).Model.Boxes).ObservationCount);
        Assert.Empty(runtime.GraphDialogs.Errors);
    }

    // ---- A failing preparation cannot end the application (hotfix) ----

    // A Graph menu command whose preparation throws: the same shell and session the application uses, with only the
    // preparation replaced through the controller's test seam.
    private static (MainWindowShellViewModel Shell, GraphSetupController Graphs) ShellWithPreparation(
        Runtime runtime,
        Func<GraphData, CancellationToken, (GraphRenderModel Frame, IGraphPlotRenderer Plot)?> prepare)
    {
        var graphs = new GraphSetupController(
            runtime.GraphDialogs,
            runtime.GraphWindows,
            new ScatterRenderModelBuilder(),
            new HistogramRenderModelBuilder(),
            new ProbabilityPlotRenderModelBuilder(),
            new EmpiricalCdfRenderModelBuilder(),
            new BoxPlotRenderModelBuilder(),
            prepare);

        return (runtime.Composition.CreateMainWindowShellViewModel(runtime.Lifecycle, graphs, runtime.Statistics, runtime.Capability), graphs);
    }

    [Fact]
    public async Task APreparationThatThrowsIsReportedAndOpensNoWindow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n3\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        var (shell, graphs) = ShellWithPreparation(runtime, (_, _) => throw new InvalidOperationException("A defect in a builder."));

        // The command completes: nothing escapes to the dispatcher that would end the process.
        await shell.HistogramCommand.ExecuteAsync(null);

        Assert.NotNull(graphs.LastConfiguration);
        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Equal([GraphSetupController.PreparationFailedMessage], runtime.GraphDialogs.Errors);
        Assert.Equal("This graph could not be drawn.", GraphSetupController.PreparationFailedMessage);
    }

    [Fact]
    public async Task ACancelledPreparationIsStillSilent()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n3\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        var (shell, _) = ShellWithPreparation(runtime, (_, _) => throw new OperationCanceledException());

        await shell.HistogramCommand.ExecuteAsync(null);

        // Cancellation keeps its meaning: no window, and no error either.
        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Empty(runtime.GraphDialogs.Errors);
    }

    // ---- Statistics panel ----

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task AGraphWithTheStatisticsPanelShowsItByDefault(GraphType graphType)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n3\n4\n\n");
        runtime.GraphDialogs.Answer = setup => ConfirmWith(setup, ("Graph variables", "Reg1"));

        await Command(runtime, graphType).ExecuteAsync(null);

        Assert.True(runtime.Graphs.LastConfiguration!.PresentationOptions.ShowStatistics);
        var (frame, _) = Assert.Single(runtime.GraphWindows.Shown);
        var panel = Assert.IsType<GraphStatisticsPanel>(frame.StatisticsPanel);
        Assert.False(panel.IsGrouped);

        var row = Assert.Single(panel.Rows);
        Assert.Equal(4, row.Count);
        Assert.Equal(2.5, row.Mean);
        Assert.Equal("2.5", row.MeanText);
        Assert.Equal("1.2909944", row.StandardDeviationText);
        Assert.Equal("4", row.CountText);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task TurningStatisticsOffInTheSetupDrawsTheGraphWithoutThePanel(GraphType graphType)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n3\n4\n");
        runtime.GraphDialogs.Answer = setup =>
        {
            setup.ShowStatistics = false;
            return ConfirmWith(setup, ("Graph variables", "Reg1"));
        };

        await Command(runtime, graphType).ExecuteAsync(null);

        Assert.False(runtime.Graphs.LastConfiguration!.PresentationOptions.ShowStatistics);
        var (frame, _) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Null(frame.StatisticsPanel);
    }

    // The panel's rows are the graph's series: same labels, same order, same colours, same N.
    [Fact]
    public async Task AGroupedProbabilityPlotHasOneRowPerSeriesAndItsFittedLinesStatistics()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tLot\n1.1\tB\n2.3\tA\n3.6\tB\n4.2\tA\n5.9\tB\n6.4\tA\n7.5\tC\n");
        runtime.GraphDialogs.Answer = setup =>
            ConfirmWith(setup, ("Graph variables", "Reg1"), ("Categorical variable for grouping", "Lot"));

        await runtime.Shell.ProbabilityPlotCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        var model = Assert.IsType<ProbabilityPlotRenderer>(plot).Model;
        var panel = Assert.IsType<GraphStatisticsPanel>(frame.StatisticsPanel);

        Assert.Equal("Lot", panel.GroupHeader);
        Assert.Equal(model.Series.Select(series => series.Label), panel.Rows.Select(row => row.Label));
        Assert.Equal(model.Series.Select(series => (int?)series.SeriesIndex), panel.Rows.Select(row => row.SeriesIndex));
        Assert.Equal(model.Series.Select(series => series.ObservationCount), panel.Rows.Select(row => row.Count));
        Assert.NotNull(frame.Legend);

        foreach (var (series, row) in model.Series.Zip(panel.Rows))
        {
            if (series.FittedLine is { } line)
            {
                Assert.Equal(line.Mean, row.Mean, 1e-12);
                Assert.Equal(line.StandardDeviation, row.StandardDeviation!.Value, 1e-12);
            }
            else
            {
                Assert.Null(row.StandardDeviation);
            }
        }

        // Lot C has one observation: a Mean and an N, but no standard deviation.
        Assert.Equal(GraphStatisticsPanelBuilder.UndefinedText, panel.Rows[^1].StandardDeviationText);
    }

    [Fact]
    public async Task AGroupedEmpiricalCdfCountsRowsWithoutAGroupUnderMissing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tSITE\n0.1\t1\n0.1\t\n0.1\t2\n0.1\t1\n0.1\t\n");
        runtime.GraphDialogs.Answer = setup =>
            ConfirmWith(setup, ("Graph variables", "Reg1"), ("Categorical variable for grouping", "SITE"));

        await runtime.Shell.EmpiricalCdfCommand.ExecuteAsync(null);

        var (frame, plot) = Assert.Single(runtime.GraphWindows.Shown);
        var model = Assert.IsType<EmpiricalCdfRenderer>(plot).Model;
        var panel = Assert.IsType<GraphStatisticsPanel>(frame.StatisticsPanel);

        Assert.Equal(["1", GraphStatisticsPanelBuilder.MissingGroupLabel, "2"], panel.Rows.Select(row => row.Label));
        Assert.Equal(model.Series.Select(series => series.Label), panel.Rows.Select(row => row.Label));
        Assert.Equal([2, 2, 1], panel.Rows.Select(row => row.Count));

        // Constant decimals: exactly no spread where there are two or more, none at all where there is one.
        Assert.Equal([0d, 0d, null], panel.Rows.Select(row => row.StandardDeviation));
        Assert.All(panel.Rows, row => Assert.Equal("0.1", row.MeanText));
    }

    [Fact]
    public async Task AScatterPlotNeverHasAStatisticsPanel()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t2\n3\t4\n5\t6\n");
        runtime.GraphDialogs.Answer = setup =>
        {
            Assert.False(setup.SupportsStatisticsPanel);
            setup.ShowStatistics = true;
            return ConfirmWithFirstColumns(setup);
        };

        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);

        var (frame, _) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Null(frame.StatisticsPanel);
    }

    [Fact]
    public async Task ABoxPlotNeverHasAStatisticsPanel()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t2\n3\t4\n5\t6\n");
        runtime.GraphDialogs.Answer = setup =>
        {
            Assert.False(setup.SupportsStatisticsPanel);
            setup.ShowStatistics = true;
            return ConfirmWithVariables(setup, "Reg1", "Reg2");
        };

        await runtime.Shell.BoxPlotCommand.ExecuteAsync(null);

        var (frame, _) = Assert.Single(runtime.GraphWindows.Shown);
        Assert.Null(frame.StatisticsPanel);
    }

    // Confirms a box plot setup by ticking the named graph variables, and optionally a grouping column.
    private static GraphConfiguration? ConfirmWithVariables(GraphSetupViewModel setup, params string[] variables) =>
        ConfirmWithVariables(setup, variables, null);

    private static GraphConfiguration? ConfirmWithVariables(GraphSetupViewModel setup, string[] variables, string? group)
    {
        var role = setup.Roles.Single(candidate => candidate.AllowsMultiple);
        foreach (var name in variables)
        {
            role.SelectedOptions.Add(role.Options.Single(option => option.Name == name));
        }

        if (group is not null)
        {
            var grouping = setup.Roles.Single(candidate => candidate.Role == GraphVariableRole.Group);
            grouping.SelectedOption = grouping.Options.Single(option => option.Name == group);
        }

        return setup.Confirm();
    }

    private static CommunityToolkit.Mvvm.Input.IAsyncRelayCommand Command(Runtime runtime, GraphType graphType) => graphType switch
    {
        GraphType.ScatterPlot => runtime.Shell.ScatterPlotCommand,
        GraphType.Histogram => runtime.Shell.HistogramCommand,
        GraphType.BoxPlot => runtime.Shell.BoxPlotCommand,
        GraphType.ProbabilityPlot => runtime.Shell.ProbabilityPlotCommand,
        _ => runtime.Shell.EmpiricalCdfCommand
    };
}
