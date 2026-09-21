using YAT.App.Tests.TestDoubles;
using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.app.Composition;
using YAT.app.Graphs;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// The Statistics menu over a real project: the setup is built from the active worksheet's stored column metadata, and
// a confirmed setup summarises the worksheet's own values into a result table.
public class DescriptiveStatisticsCommandTests
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
            Statistics = Composition.CreateDescriptiveStatistics(AnalysisDialogs, AnalysisResults);
            Shell = Composition.CreateMainWindowShellViewModel(
                Lifecycle,
                Composition.CreateGraphSetup(new FakeGraphSetupDialogs(), new FakeGraphWindowPresenter()),
                Statistics,
                Composition.CreateCapabilityAnalysis(new FakeCapabilityAnalysisSetupDialogs(), new FakeAnalysisResultPresenter()));
        }

        public TemporaryDirectory Directory { get; } = new();

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public FakeProjectLifecycleDialogs ProjectDialogs { get; } = new();

        public FakeAnalysisSetupDialogs AnalysisDialogs { get; } = new();

        public FakeAnalysisResultPresenter AnalysisResults { get; } = new();

        public ProjectLifecycleController Lifecycle { get; }

        public DescriptiveStatisticsController Statistics { get; }

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

        public Task RunAsync() => Shell.DescriptiveStatisticsCommand.ExecuteAsync(null);

        public void Dispose()
        {
            Workspace.Dispose();
            Directory.Dispose();
        }
    }

    // Confirms the setup with the named variables, and optionally the named grouping column.
    private static Func<AnalysisSetupViewModel, AnalysisConfiguration?> ConfirmWith(string? group, params string[] variables) =>
        setup =>
        {
            foreach (var name in variables)
            {
                setup.Variables.Single(variable => variable.Name == name).IsSelected = true;
            }

            if (group is not null)
            {
                setup.SelectedGroup = setup.GroupOptions.Single(option => option.Name == group);
            }

            return setup.Confirm();
        };

    private static string Cell(AnalysisResultTable table, int row, string columnName)
    {
        var column = table.Columns.Select((candidate, index) => (candidate.Name, index)).Single(candidate => candidate.Name == columnName);
        return table.Rows[row].Cells[column.index];
    }

    private static string[] Column(AnalysisResultTable table, string columnName) =>
        [.. Enumerable.Range(0, table.Rows.Count).Select(row => Cell(table, row, columnName))];

    // 0
    [Fact]
    public async Task TheCommandShowsTheSetupForTheActiveWorksheetsColumns()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("No\tSITE\tLot\tReg1\n1\t1\tA\t5\n");
        runtime.AnalysisDialogs.Answer = _ => null;

        await runtime.RunAsync();

        var setup = runtime.AnalysisDialogs.LastSetup;
        Assert.Equal("Descriptive Statistics", setup.Title);
        Assert.Equal(runtime.Project.SelectedWorksheet!.Id, setup.WorksheetId);
        Assert.Equal("Sheet1", setup.WorksheetName);
        Assert.Equal(["No", "SITE", "Reg1"], setup.Variables.Select(variable => variable.Name));
        Assert.Equal(["(None)", "No", "SITE", "Lot", "Reg1"], setup.GroupOptions.Select(option => option.Name));
    }

    // 1
    [Fact]
    public async Task CancellingTheSetupChangesNothing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n");
        runtime.AnalysisDialogs.Answer = _ => null;

        await runtime.RunAsync();

        Assert.Single(runtime.AnalysisDialogs.Shown);
        Assert.Null(runtime.Statistics.LastConfiguration);
        Assert.Empty(runtime.AnalysisDialogs.Errors);
        Assert.Empty(runtime.AnalysisResults.Shown);
        Assert.Null(runtime.Project.ErrorMessage);
    }

    // 2
    [Fact]
    public async Task AConfirmedSetupSummarisesTheWorksheetsOwnValues()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n3\n4\n");
        runtime.AnalysisDialogs.Answer = ConfirmWith(null, "Reg1");

        await runtime.RunAsync();

        var table = runtime.AnalysisResults.Last;
        Assert.Equal("Descriptive Statistics: Reg1", table.Title);
        Assert.Equal(["Variable", "N", "Missing", "Mean", "StDev", "Min", "Q1", "Median", "Q3", "Max"], table.Columns.Select(column => column.Name));
        var row = Assert.Single(table.Rows);
        Assert.Equal("Reg1", row.Cells[0]);
        Assert.Equal("4", Cell(table, 0, "N"));
        Assert.Equal("0", Cell(table, 0, "Missing"));
        Assert.Equal("2.5", Cell(table, 0, "Mean"));
        Assert.Equal("1.2909944", Cell(table, 0, "StDev"));
        Assert.Equal("4", Cell(table, 0, "Max"));
    }

    // 3
    [Fact]
    public async Task TheConfirmedConfigurationIdentifiesItsColumnsById()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tSITE\n1\t10\t1\n2\t20\t2\n");
        runtime.AnalysisDialogs.Answer = ConfirmWith("SITE", "Reg1", "Reg2");

        await runtime.RunAsync();

        var configuration = runtime.Statistics.LastConfiguration;
        Assert.NotNull(configuration);

        var columns = await runtime.Workspace.CurrentSession!.WorksheetColumns.GetByWorksheetIdAsync(configuration.WorksheetId, Token);
        Assert.Equal([columns[0].Id, columns[1].Id], configuration.VariableColumnIds);
        Assert.Equal(columns[2].Id, configuration.GroupColumnId);
        Assert.True(new AnalysisConfigurationValidator().Validate(configuration, columns).IsValid);
    }

    // 4
    [Fact]
    public async Task GroupedResultsKeepEachVariableAndGroupOnItsOwnRow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tSITE\n1\t10\t2\n3\t30\t1\n5\t50\t2\n");
        runtime.AnalysisDialogs.Answer = ConfirmWith("SITE", "Reg1", "Reg2");

        await runtime.RunAsync();

        var table = runtime.AnalysisResults.Last;
        Assert.Equal(["Reg1", "Reg1", "Reg2", "Reg2"], Column(table, "Variable"));

        // First observed: SITE 2 before SITE 1, for both variables.
        Assert.Equal(["2", "1", "2", "1"], Column(table, "Group"));
        Assert.Equal(["2", "1", "2", "1"], Column(table, "N"));
        Assert.Equal(["3", "3", "30", "30"], Column(table, "Mean"));
    }

    // 5
    [Fact]
    public async Task MissingValuesAndMissingGroupsAreReportedOverTheWorksheetsRows()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        // Reg1 is empty in the third row; SITE is empty in the fourth.
        await runtime.PasteAsync("Reg1\tSITE\n1\tA\n2\tA\n\tA\n4\t\n");
        runtime.AnalysisDialogs.Answer = ConfirmWith("SITE", "Reg1");

        await runtime.RunAsync();

        var table = runtime.AnalysisResults.Last;
        Assert.Equal(["A", "(Missing)"], Column(table, "Group"));
        Assert.Equal(["2", "1"], Column(table, "N"));
        Assert.Equal(["1", "0"], Column(table, "Missing"));
        Assert.Equal(["1.5", "4"], Column(table, "Mean"));
    }

    // 6
    [Fact]
    public async Task AVariableIsMissingTheWorksheetsRowsItHasNoValuesFor()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        // Reg2 stops after two rows; the worksheet still has four.
        await runtime.PasteAsync("Reg1\tReg2\n1\t10\n2\t20\n3\t\n4\t\n");
        runtime.AnalysisDialogs.Answer = ConfirmWith(null, "Reg2");

        await runtime.RunAsync();

        var table = runtime.AnalysisResults.Last;
        Assert.Equal("2", Cell(table, 0, "N"));
        Assert.Equal("2", Cell(table, 0, "Missing"));
    }

    // 7
    [Fact]
    public async Task RunningTheCommandWithoutAWorksheetSaysSo()
    {
        using var runtime = new Runtime();

        await runtime.Statistics.ConfigureAsync(session: null, worksheet: null, Token);

        Assert.Equal("Select a worksheet before running an analysis.", Assert.Single(runtime.AnalysisDialogs.Errors));
        Assert.Empty(runtime.AnalysisDialogs.Shown);
    }

    // 8
    [Fact]
    public async Task AWorksheetWithoutNumericColumnsHasNothingToSummarise()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\nB\n");

        await runtime.RunAsync();

        Assert.Equal("This worksheet has no numeric columns to analyse.", Assert.Single(runtime.AnalysisDialogs.Errors));
        Assert.Empty(runtime.AnalysisDialogs.Shown);
        Assert.Empty(runtime.AnalysisResults.Shown);
    }

    // 9
    [Fact]
    public async Task AConfigurationWhoseColumnWasDeletedIsReportedInsteadOfShown()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n");

        var worksheetId = runtime.Project.SelectedWorksheet!.Id;
        runtime.AnalysisDialogs.Answer = _ => new AnalysisConfiguration(worksheetId, [Guid.NewGuid()], null);

        await runtime.RunAsync();

        Assert.Equal("A column of this analysis is no longer available.", Assert.Single(runtime.AnalysisDialogs.Errors));
        Assert.Empty(runtime.AnalysisResults.Shown);
    }
}
