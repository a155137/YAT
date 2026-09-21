using System.Diagnostics;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Analyses;
using YAT.app.Analyses;
using YAT.app.Composition;
using YAT.app.Graphs;
using YAT.app.Lifecycle;
using YAT.app.ViewModels;
using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// The Capability Analysis command over a real project: the setup is built from the active worksheet's stored column
// metadata, and a confirmed setup measures the worksheet's own values against their specifications.
public class CapabilityAnalysisCommandTests
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
            Capability = Composition.CreateCapabilityAnalysis(CapabilityDialogs, Results);
            Shell = Composition.CreateMainWindowShellViewModel(
                Lifecycle,
                Composition.CreateGraphSetup(new FakeGraphSetupDialogs(), new FakeGraphWindowPresenter()),
                Composition.CreateDescriptiveStatistics(new FakeAnalysisSetupDialogs(), Results),
                Capability);
        }

        public TemporaryDirectory Directory { get; } = new();

        public CompositionRoot Composition { get; }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public FakeProjectLifecycleDialogs ProjectDialogs { get; } = new();

        public FakeCapabilityAnalysisSetupDialogs CapabilityDialogs { get; } = new();

        public FakeAnalysisResultPresenter Results { get; } = new();

        public ProjectLifecycleController Lifecycle { get; }

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

        public Task RunAsync() => Shell.CapabilityAnalysisCommand.ExecuteAsync(null);

        public void Dispose()
        {
            Workspace.Dispose();
            Directory.Dispose();
        }
    }

    // Confirms the setup with the named variables and their limits, optionally grouped, optionally showing more
    // statistics than the defaults.
    private static Func<CapabilityAnalysisSetupViewModel, CapabilityAnalysisConfiguration?> ConfirmWith(
        string? group,
        IEnumerable<(string Variable, string Lower, string Upper)> specifications,
        params CapabilityStatistic[] alsoShow) =>
        setup =>
        {
            foreach (var (name, lower, upper) in specifications)
            {
                var variable = setup.Variables.Single(candidate => candidate.Name == name);
                variable.IsSelected = true;
                variable.LowerSpecificationLimitText = lower;
                variable.UpperSpecificationLimitText = upper;
            }

            if (group is not null)
            {
                setup.SelectedGroup = setup.GroupOptions.Single(option => option.Name == group);
            }

            foreach (var statistic in alsoShow)
            {
                setup.Statistics.Single(candidate => candidate.Statistic == statistic).IsSelected = true;
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
        await runtime.PasteAsync("SITE\tLot\tReg1\n1\tA\t5\n");
        runtime.CapabilityDialogs.Answer = _ => null;

        await runtime.RunAsync();

        var setup = runtime.CapabilityDialogs.LastSetup;
        Assert.Equal("Capability Analysis", setup.Title);
        Assert.Equal(runtime.Project.SelectedWorksheet!.Id, setup.WorksheetId);
        Assert.Equal(["SITE", "Reg1"], setup.Variables.Select(variable => variable.Name));
        Assert.Equal(["(None)", "SITE", "Lot", "Reg1"], setup.GroupOptions.Select(option => option.Name));
    }

    // 1
    [Fact]
    public async Task CancellingTheSetupChangesNothing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n");
        runtime.CapabilityDialogs.Answer = _ => null;

        await runtime.RunAsync();

        Assert.Single(runtime.CapabilityDialogs.Shown);
        Assert.Null(runtime.Capability.LastConfiguration);
        Assert.Empty(runtime.CapabilityDialogs.Errors);
        Assert.Empty(runtime.Results.Shown);
    }

    // 2
    [Fact]
    public async Task AConfirmedTwoSidedSpecificationMeasuresTheWorksheetsOwnValues()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n14950\n15050\n14950\n15050\n");
        runtime.CapabilityDialogs.Answer = ConfirmWith(
            null,
            [("Reg1", "14500", "15500")],
            CapabilityStatistic.Cp, CapabilityStatistic.Cpl, CapabilityStatistic.Cpu, CapabilityStatistic.Cpk);

        await runtime.RunAsync();

        var table = runtime.Results.Last;
        Assert.Equal("Capability Analysis: Reg1", table.Title);
        Assert.Equal(["Variable", "N", "Mean", "Within StDev", "Cp", "Cpl", "Cpu", "Cpk"], table.Columns.Select(column => column.Name));

        // MRbar 100 -> sigma 100 / 1.128; the process is centred, so Cpl and Cpu are equal.
        var sigma = 100 / 1.128;
        Assert.Equal("4", Cell(table, 0, "N"));
        Assert.Equal("15000", Cell(table, 0, "Mean"));
        Assert.Equal(AnalysisNumberFormat.Statistic(sigma), Cell(table, 0, "Within StDev"));
        Assert.Equal(AnalysisNumberFormat.Statistic(1000 / (6 * sigma)), Cell(table, 0, "Cp"));
        Assert.Equal(Cell(table, 0, "Cpl"), Cell(table, 0, "Cpu"));
        Assert.Equal(Cell(table, 0, "Cpu"), Cell(table, 0, "Cpk"));
    }

    // 3
    [Fact]
    public async Task AnUpperOnlyAndALowerOnlySpecificationEachReportTheirOwnSide()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\n450\t22\n490\t30\n450\t22\n490\t30\n");
        runtime.CapabilityDialogs.Answer = ConfirmWith(
            null,
            [("Reg1", string.Empty, "500"), ("Reg2", "20", string.Empty)],
            CapabilityStatistic.Cp, CapabilityStatistic.Cpl, CapabilityStatistic.Cpu, CapabilityStatistic.Cpk,
            CapabilityStatistic.LowerSpecificationLimit, CapabilityStatistic.UpperSpecificationLimit);

        await runtime.RunAsync();

        var table = runtime.Results.Last;
        Assert.Equal(["Reg1", "Reg2"], Column(table, "Variable"));

        // Reg1 has an upper limit only, Reg2 a lower one only.
        Assert.Equal([string.Empty, "20"], Column(table, "LSL"));
        Assert.Equal(["500", string.Empty], Column(table, "USL"));
        Assert.Equal([string.Empty, string.Empty], Column(table, "Cp"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cpl"));
        Assert.NotEqual(string.Empty, Cell(table, 0, "Cpu"));
        Assert.Equal(string.Empty, Cell(table, 1, "Cpu"));
        Assert.NotEqual(string.Empty, Cell(table, 1, "Cpl"));
        Assert.Equal(Cell(table, 0, "Cpu"), Cell(table, 0, "Cpk"));
        Assert.Equal(Cell(table, 1, "Cpl"), Cell(table, 1, "Cpk"));
    }

    // 4
    [Fact]
    public async Task GroupedCapabilityMeasuresEachGroupOnItsOwn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        // SITE 2 first, and SITE 1's two measurements are not next to each other in the worksheet.
        await runtime.PasteAsync("Reg1\tSITE\n15450\t2\n14950\t1\n15550\t2\n15050\t1\n");
        runtime.CapabilityDialogs.Answer = ConfirmWith("SITE", [("Reg1", "14500", "15500")], CapabilityStatistic.Cpk);

        await runtime.RunAsync();

        var table = runtime.Results.Last;
        Assert.Equal(["Variable", "Group", "N", "Mean", "Within StDev", "Cpk"], table.Columns.Select(column => column.Name));
        Assert.Equal(["2", "1"], Column(table, "Group"));
        Assert.Equal(["15500", "15000"], Column(table, "Mean"));

        // Each group has its own moving range over its own rows: rows of the other site do not interrupt it.
        var sigma = 100 / 1.128;
        Assert.Equal([AnalysisNumberFormat.Statistic(sigma), AnalysisNumberFormat.Statistic(sigma)], Column(table, "Within StDev"));

        // SITE 2 sits exactly on the upper limit; SITE 1 is centred.
        Assert.Equal(AnalysisNumberFormat.Statistic(0), Cell(table, 0, "Cpk"));
        Assert.Equal(AnalysisNumberFormat.Statistic(500 / (3 * sigma)), Cell(table, 1, "Cpk"));
    }

    // 5
    [Fact]
    public async Task AMissingValueBreaksTheSequenceAndIsCountedAsMissing()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n100\n\n101\n");
        runtime.CapabilityDialogs.Answer = ConfirmWith(
            null, [("Reg1", "90", "110")], CapabilityStatistic.Missing, CapabilityStatistic.Cpk);

        await runtime.RunAsync();

        var table = runtime.Results.Last;
        Assert.Equal("2", Cell(table, 0, "N"));
        Assert.Equal("1", Cell(table, 0, "Missing"));
        Assert.Equal("100.5", Cell(table, 0, "Mean"));
        Assert.Equal(string.Empty, Cell(table, 0, "Within StDev"));
        Assert.Equal(string.Empty, Cell(table, 0, "Cpk"));
    }

    // 6
    [Fact]
    public async Task TheConfirmedConfigurationIdentifiesItsColumnsByIdAndKeepsItsSpecifications()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\tReg2\tSITE\n1\t10\t1\n2\t20\t2\n");
        runtime.CapabilityDialogs.Answer = ConfirmWith("SITE", [("Reg1", "0", "5"), ("Reg2", "5", "50")]);

        await runtime.RunAsync();

        var configuration = runtime.Capability.LastConfiguration;
        Assert.NotNull(configuration);

        var columns = await runtime.Workspace.CurrentSession!.WorksheetColumns.GetByWorksheetIdAsync(configuration.WorksheetId, Token);
        Assert.Equal([columns[0].Id, columns[1].Id], configuration.Variables.Select(variable => variable.WorksheetColumnId));
        Assert.Equal([0, 5], configuration.Variables.Select(variable => variable.LowerSpecificationLimit));
        Assert.Equal(columns[2].Id, configuration.GroupColumnId);
        Assert.True(new CapabilityAnalysisValidator().Validate(configuration, columns).IsValid);
    }

    // 7
    [Fact]
    public async Task RunningTheCommandWithoutAWorksheetSaysSo()
    {
        using var runtime = new Runtime();

        await runtime.Capability.ConfigureAsync(session: null, worksheet: null, Token);

        Assert.Equal("Select a worksheet before running an analysis.", Assert.Single(runtime.CapabilityDialogs.Errors));
        Assert.Empty(runtime.CapabilityDialogs.Shown);
    }

    // 8
    [Fact]
    public async Task AWorksheetWithoutNumericColumnsHasNothingToMeasure()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Lot\nA\nB\n");

        await runtime.RunAsync();

        Assert.Equal("This worksheet has no numeric columns to analyse.", Assert.Single(runtime.CapabilityDialogs.Errors));
        Assert.Empty(runtime.Results.Shown);
    }

    // 9
    [Fact]
    public async Task AConfigurationWhoseColumnWasDeletedIsReportedInsteadOfShown()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg1\n1\n2\n");

        var worksheetId = runtime.Project.SelectedWorksheet!.Id;
        runtime.CapabilityDialogs.Answer = _ => new CapabilityAnalysisConfiguration(
            worksheetId,
            [new CapabilityVariable(Guid.NewGuid(), 0, 10)],
            null,
            CapabilityAnalysisConfiguration.DefaultDisplayStatistics);

        await runtime.RunAsync();

        Assert.Equal("A column of this analysis is no longer available.", Assert.Single(runtime.CapabilityDialogs.Errors));
        Assert.Empty(runtime.Results.Shown);
    }

    // A representative dataset over real DuckDB storage: 200,000 rows of three variables and a group, read through the
    // analysis data query and measured by the capability builder. Timing is reported, never asserted.
    // 10
    [Fact]
    public async Task ALargeGroupedCapabilityAnalysisRunsOverRealStorage()
    {
        const int RowCount = 200_000;
        var composition = new CompositionRoot(new FixedTimeProvider(Now));
        using var session = composition.CreateProjectSession(":memory:");

        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "Sheet1" };
        await session.Worksheets.AddAsync(worksheet, Token);

        var reg1 = new double?[RowCount];
        var reg2 = new double?[RowCount];
        var reg3 = new double?[RowCount];
        var site = new string?[RowCount];
        for (var row = 0; row < RowCount; row++)
        {
            // Values that move from row to row, with gaps, so the moving range has real work to do.
            reg1[row] = row % 11 == 0 ? null : 15000 + (row % 7);
            reg2[row] = 0.15 + (row % 5 * 0.001);
            reg3[row] = row % 13 == 0 ? null : 480 + (row % 3);
            site[row] = row % 4 == 0 ? null : $"SITE{row % 4}";
        }

        var columns = new List<WorksheetColumn>();
        foreach (var (name, dataType) in ((string, WorksheetDataType)[])
                 [("Reg1", WorksheetDataType.Numeric), ("Reg2", WorksheetDataType.Numeric),
                  ("Reg3", WorksheetDataType.Numeric), ("SITE", WorksheetDataType.String)])
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = worksheet.Id,
                Index = columns.Count,
                Name = name,
                DataType = dataType
            };
            await session.WorksheetColumns.AddAsync(column, Token);
            columns.Add(column);
        }

        await session.RawDataStore.WriteColumnsAsync(
            worksheet.Id,
            new RawDataBlock(
            [
                new NumericRawDataColumn(columns[0].Id, reg1),
                new NumericRawDataColumn(columns[1].Id, reg2),
                new NumericRawDataColumn(columns[2].Id, reg3),
                new StringRawDataColumn(columns[3].Id, site)
            ]),
            Token);

        var configuration = new CapabilityAnalysisConfiguration(
            worksheet.Id,
            [
                new CapabilityVariable(columns[0].Id, 14500, 15500),
                new CapabilityVariable(columns[1].Id, 0.1, 0.2),
                new CapabilityVariable(columns[2].Id, null, 500)
            ],
            columns[3].Id,
            CapabilityAnalysisConfiguration.SelectableStatistics);

        var query = new AnalysisDataQueryService(session.Worksheets, session.WorksheetColumns, session.RawDataStore);

        var loadWatch = Stopwatch.StartNew();
        var data = await query.LoadAsync(configuration.ToAnalysisConfiguration(), Token);
        loadWatch.Stop();

        var buildWatch = Stopwatch.StartNew();
        var table = new CapabilityAnalysisBuilder().Build(data, configuration, Token);
        buildWatch.Stop();

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{RowCount:N0} rows x 3 variables + group: loaded in {loadWatch.ElapsedMilliseconds:N0} ms, " +
            $"measured in {buildWatch.ElapsedMilliseconds:N0} ms");

        Assert.Equal(RowCount, data.RowCount);

        // Three variables over four observed groups (SITE1..3 and the rows without a site).
        Assert.Equal(12, table.Rows.Count);
        Assert.All(table.Rows, row => Assert.DoesNotContain(row.Cells, cell => cell.Contains("NaN") || cell.Contains("Inf")));
    }
}
