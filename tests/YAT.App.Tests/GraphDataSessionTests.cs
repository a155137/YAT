using YAT.App.Tests.TestDoubles;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.app.Composition;
using YAT.app.Graphs;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// Graph data through the session boundary: it follows the current project, and a retired session refuses to load.
public class GraphDataSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // From Task #041 a graph of measured variables reads them one slice each (MultiVariableGraphData); a single
    // variable's slice holds what the single-variable read gave before.
    private static UnivariateGraphData OneVariable(GraphData data) =>
        Assert.Single(Assert.IsType<MultiVariableGraphData>(data).Variables);

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            Composition = new CompositionRoot(new FixedTimeProvider(Now), Directory.File("temp"));
            Workspace = Composition.CreateProjectWorkspace();
            Lifecycle = Composition.CreateProjectLifecycle(Workspace, Clipboard, Clipboard, ProjectDialogs);
            Shell = Composition.CreateMainWindowShellViewModel(
                Lifecycle,
                Composition.CreateGraphSetup(GraphDialogs, GraphWindows),
                Composition.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), new FakeAnalysisResultPresenter()),
                Composition.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), new FakeAnalysisResultPresenter()));
        }

        public TemporaryDirectory Directory { get; } = new();

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public FakeProjectLifecycleDialogs ProjectDialogs { get; } = new();

        public FakeGraphSetupDialogs GraphDialogs { get; } = new();

        public FakeGraphWindowPresenter GraphWindows { get; } = new();

        public ProjectLifecycleController Lifecycle { get; }

        public MainWindowShellViewModel Shell { get; }

        public MainWindowViewModel Project => Lifecycle.Project!;

        public MainWindowSession Session => Lifecycle.CurrentSession!;

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

        // A configuration over the active worksheet's columns, by name.
        public async Task<GraphConfiguration> ConfigurationAsync(GraphType graphType, params (GraphVariableRole Role, string ColumnName)[] roles)
        {
            var worksheet = Project.SelectedWorksheet!;
            var columns = await Session.LoadWorksheetColumnsAsync(worksheet.Id, Token);
            return new GraphConfiguration(graphType, worksheet.Id,
                [.. roles.Select(role => new GraphColumnAssignment(
                    role.Role, columns.Single(column => column.Name == role.ColumnName).Id))]);
        }

        public void Dispose()
        {
            Workspace.Dispose();
            Directory.Dispose();
        }
    }

    [Fact]
    public async Task TheSessionLoadsGraphDataOfItsProject()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tLot\n1\t10\tA\n\t20\tB\n3\t30\t\n");
        var configuration = await runtime.ConfigurationAsync(
            GraphType.ScatterPlot, (GraphVariableRole.X, "Reg1"), (GraphVariableRole.Y, "Reg2"), (GraphVariableRole.Group, "Lot"));

        var data = Assert.IsType<ScatterGraphData>(await runtime.Session.LoadGraphDataAsync(configuration, Token));

        Assert.Equal([1, 3], data.XValues.ToArray());
        Assert.Equal([10, 30], data.YValues.ToArray());
        Assert.Equal(["A", null], ((StringGroupData)data.Group!).Values.ToArray());
        Assert.Equal("Reg1", data.X.Name);
        Assert.Equal("Reg2", data.Y.Name);

        // The values stay in the graph data: the view model still holds only one page of display text.
        Assert.Equal(3, runtime.Project.TotalRowCount);
        Assert.Equal(3, runtime.Project.GridRows.Count);
    }

    [Theory]
    [InlineData(GraphType.Histogram)]
    [InlineData(GraphType.ProbabilityPlot)]
    [InlineData(GraphType.EmpiricalCdf)]
    public async Task SingleVariableGraphsLoadTheirVariable(GraphType graphType)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n5\n\n7\n");
        var configuration = await runtime.ConfigurationAsync(graphType, (GraphVariableRole.Variable, "Reg1"));

        var data = OneVariable(await runtime.Session.LoadGraphDataAsync(configuration, Token));

        Assert.Equal([5, 7], data.Values.ToArray());
        Assert.Equal(graphType, data.GraphType);
    }

    [Fact]
    public async Task GraphDataFollowsTheProjectAfterNewOpenAndSaveAs()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n");
        var savedPath = runtime.Directory.File("Saved.yat");
        runtime.ProjectDialogs.SavePaths.Enqueue(savedPath);
        Assert.True(await runtime.Lifecycle.SaveAsAsync());
        await runtime.Project.GridLoadTask;

        // Save As: the new session reads the saved project's data.
        var savedConfiguration = await runtime.ConfigurationAsync(GraphType.Histogram, (GraphVariableRole.Variable, "Reg1"));
        var savedData = OneVariable(await runtime.Session.LoadGraphDataAsync(savedConfiguration, Token));
        Assert.Equal([1, 2], savedData.Values.ToArray());

        // New: another project, its own worksheet and data.
        Assert.True(await runtime.Lifecycle.NewProjectAsync());
        await runtime.Project.GridLoadTask;
        await runtime.PasteAsync("Reg1\n7\n8\n9\n");
        var newConfiguration = await runtime.ConfigurationAsync(GraphType.Histogram, (GraphVariableRole.Variable, "Reg1"));
        var newData = OneVariable(await runtime.Session.LoadGraphDataAsync(newConfiguration, Token));
        Assert.Equal([7, 8, 9], newData.Values.ToArray());

        // A configuration of the previous project is not part of this one.
        var exception = await Assert.ThrowsAsync<GraphDataException>(() => runtime.Session.LoadGraphDataAsync(savedConfiguration, Token));
        Assert.Equal(GraphDataError.WorksheetUnavailable, exception.Error);

        // Open: back to the saved project, whose data is unchanged.
        runtime.ProjectDialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        runtime.ProjectDialogs.OpenPaths.Enqueue(savedPath);
        Assert.True(await runtime.Lifecycle.OpenProjectAsync());
        await runtime.Project.GridLoadTask;
        var reopenedData = OneVariable(await runtime.Session.LoadGraphDataAsync(savedConfiguration, Token));
        Assert.Equal([1, 2], reopenedData.Values.ToArray());
    }

    [Fact]
    public async Task ARetiredSessionRefusesToLoadGraphData()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n");
        var configuration = await runtime.ConfigurationAsync(GraphType.Histogram, (GraphVariableRole.Variable, "Reg1"));
        var retired = runtime.Session;

        runtime.ProjectDialogs.Choices.Enqueue(SaveChangesChoice.Discard);
        Assert.True(await runtime.Lifecycle.NewProjectAsync());
        await runtime.Project.GridLoadTask;

        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => retired.LoadGraphDataAsync(configuration, Token));
    }

    [Fact]
    public async Task ADeletedColumnIsReportedThroughTheSession()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n2\t20\n");
        var configuration = await runtime.ConfigurationAsync(
            GraphType.ScatterPlot, (GraphVariableRole.X, "Reg1"), (GraphVariableRole.Y, "Reg2"));

        runtime.Project.SelectColumn(runtime.Project.SelectedWorksheetColumns![0].Id);
        await runtime.Project.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        await runtime.Project.GridLoadTask;

        var exception = await Assert.ThrowsAsync<GraphDataException>(() => runtime.Session.LoadGraphDataAsync(configuration, Token));

        Assert.Equal(GraphDataError.ColumnUnavailable, exception.Error);
    }

    [Fact]
    public async Task AConfigurationConfirmedInTheSetupCanBeLoaded()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n2\t20\n");

        // The setup produces the configuration; loading its data is a separate, explicit step.
        runtime.GraphDialogs.Answer = setup =>
        {
            foreach (var role in setup.Roles.Where(role => role.IsRequired))
            {
                role.SelectedOption = role.Options[setup.Roles.ToList().IndexOf(role)];
            }

            return setup.Confirm();
        };
        await runtime.Shell.ScatterPlotCommand.ExecuteAsync(null);
        var configuration = runtime.Shell.Graphs.LastConfiguration;

        Assert.NotNull(configuration);
        var data = Assert.IsType<ScatterGraphData>(await runtime.Session.LoadGraphDataAsync(configuration, Token));
        Assert.Equal([1, 2], data.XValues.ToArray());
        Assert.Equal([10, 20], data.YValues.ToArray());
    }
}
