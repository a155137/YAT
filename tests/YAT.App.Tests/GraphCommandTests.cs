using YAT.App.Tests.TestDoubles;
using YAT.Application.Graphs;
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
            Shell = Composition.CreateMainWindowShellViewModel(Lifecycle, Graphs);
        }

        public TemporaryDirectory Directory { get; } = new();

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public FakeProjectLifecycleDialogs ProjectDialogs { get; } = new();

        public FakeGraphSetupDialogs GraphDialogs { get; } = new();

        public FakeGraphWindowPresenter GraphWindows { get; } = new();

        public ProjectLifecycleController Lifecycle { get; }

        public GraphSetupController Graphs { get; }

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

    // The other graph types are not drawn yet, and say so instead of opening a window.
    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task AGraphTypeThatIsNotImplementedYetOpensNoWindow(GraphType graphType)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t2\n");
        runtime.GraphDialogs.Answer = ConfirmWithFirstColumns;

        await Command(runtime, graphType).ExecuteAsync(null);

        Assert.Empty(runtime.GraphWindows.Shown);
        Assert.Equal(["This graph type is not implemented yet."], runtime.GraphDialogs.Errors);
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

    private static CommunityToolkit.Mvvm.Input.IAsyncRelayCommand Command(Runtime runtime, GraphType graphType) => graphType switch
    {
        GraphType.ScatterPlot => runtime.Shell.ScatterPlotCommand,
        GraphType.Histogram => runtime.Shell.HistogramCommand,
        GraphType.ProbabilityPlot => runtime.Shell.ProbabilityPlotCommand,
        _ => runtime.Shell.EmpiricalCdfCommand
    };
}
