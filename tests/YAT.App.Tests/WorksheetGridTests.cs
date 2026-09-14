using System.Globalization;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Worksheet grid: one bounded page loaded through MainWindowSession → WorksheetDataQueryService → DuckDB.
public class WorksheetGridTests
{
    private const string CanonicalText =
        "No\tBin\tSITE\tReg1\tReg2\n" +
        "1\t1\t1\t5\t.132\n" +
        "2\t2\t2\t7\t.157\n" +
        "3\t1\t3\t2\t.122\n";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime(string databasePath = ":memory:")
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(databasePath);
            ViewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(ProjectSession, Clipboard));
        }

        public FakeClipboardTextReader Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public async Task StartAsync()
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
        }

        public async Task<Worksheet> CreateWorksheetAsync(string name)
        {
            ViewModel.WorksheetName = name;
            await ViewModel.CreateWorksheetCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
            return ViewModel.SelectedWorksheet!;
        }

        public async Task SelectAsync(Worksheet worksheet)
        {
            ViewModel.SelectedWorksheet = worksheet;
            await ViewModel.GridLoadTask;
        }

        public string[][] Cells => ViewModel.GridRows.Select(row => row.Cells.ToArray()).ToArray();

        public string[] Headers => ViewModel.GridColumns.Select(column => column.Name).ToArray();

        public void Dispose() => ProjectSession.Dispose();
    }

    private static string Numbers(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    // 11
    [Fact]
    public async Task PasteShowsHeadersRowsAndRowCountImmediately()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.PasteAsync(CanonicalText);

        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Reg2"], runtime.Headers);
        Assert.Equal(
            [["1", "1", "1", "5", "0.132"], ["2", "2", "2", "7", "0.157"], ["3", "1", "3", "2", "0.122"]],
            runtime.Cells);
        Assert.Equal([1L, 2L, 3L], runtime.ViewModel.GridRows.Select(row => row.RowNumber));
        Assert.Equal(3, runtime.ViewModel.TotalRowCount);
        Assert.Equal("3 rows · 5 columns", runtime.ViewModel.SelectedWorksheetSummary);
        Assert.Equal("Rows 1–3 of 3", runtime.ViewModel.GridPageSummary);
        Assert.True(runtime.ViewModel.HasGridColumns);
        Assert.False(runtime.ViewModel.IsGridLoading);
    }

    [Fact]
    public async Task StartupGridIsEmpty()
    {
        using var runtime = new Runtime();

        await runtime.StartAsync();

        Assert.Empty(runtime.ViewModel.GridColumns);
        Assert.Empty(runtime.ViewModel.GridRows);
        Assert.False(runtime.ViewModel.HasGridColumns);
        Assert.Equal(0, runtime.ViewModel.TotalRowCount);
        Assert.Equal("No rows", runtime.ViewModel.GridPageSummary);
        Assert.False(runtime.ViewModel.PreviousPageCommand.CanExecute(null));
        Assert.False(runtime.ViewModel.NextPageCommand.CanExecute(null));
    }

    // 7
    [Fact]
    public async Task GridColumnsFollowWorksheetColumnIndex()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("No\tBin\n1\t1\n");
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        await runtime.ProjectSession.WorksheetColumns.AddAsync(new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = sheet1.Id,
            Index = 4,
            Name = "Temp",
            DataType = WorksheetDataType.Numeric
        }, Token);

        await runtime.PasteAsync("Reg1\n5\n");

        Assert.Equal(["No", "Bin", "Temp", "Reg1"], runtime.Headers);
        Assert.Equal([0, 1, 4, 5], runtime.ViewModel.GridColumns.Select(column => column.Index));
        Assert.Equal(["1", "1", "", "5"], Assert.Single(runtime.Cells));
    }

    // 8
    [Fact]
    public async Task NumericStringAndEmptyCellsDisplayAsText()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.PasteAsync(
            "Num\tText\tSci\n" +
            "1.5\t N 1 \t1.57E-6\n" +
            "\t\t\n" +
            "-23\tabc\t2500\n");

        Assert.Equal([WorksheetDataType.Numeric, WorksheetDataType.String, WorksheetDataType.Numeric],
            runtime.ViewModel.GridColumns.Select(column => column.DataType));
        Assert.Equal(
            [["1.5", " N 1 ", "1.57E-06"], ["", "", ""], ["-23", "abc", "2500"]],
            runtime.Cells);
    }

    // 6
    [Fact]
    public async Task ShorterReplacementColumnIsBlankPadded()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("A\tB\n1\t2\n3\t4\n5\t6\n");

        runtime.ViewModel.ToggleColumnSelectionCommand.Execute(runtime.ViewModel.GridColumns[0].ColumnId);
        await runtime.PasteAsync("X\n9\n");

        Assert.Equal(["X", "B"], runtime.Headers);
        Assert.Equal([["9", "2"], ["", "4"], ["", "6"]], runtime.Cells);
        Assert.Equal(3, runtime.ViewModel.TotalRowCount);
    }

    // 9, 4, 5
    [Fact]
    public async Task GridHoldsOnePageAtATimeAndPagesThroughTheRows()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg\n" + string.Join("\n", Enumerable.Range(1, 1200)) + "\n");

        Assert.Equal(1200, runtime.ViewModel.TotalRowCount);
        Assert.Equal(500, runtime.ViewModel.GridRows.Count);
        Assert.Equal($"Rows 1–500 of {Numbers(1200)}", runtime.ViewModel.GridPageSummary);
        Assert.False(runtime.ViewModel.PreviousPageCommand.CanExecute(null));
        Assert.True(runtime.ViewModel.NextPageCommand.CanExecute(null));

        await runtime.ViewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(500, runtime.ViewModel.GridRows.Count);
        Assert.Equal(500, runtime.ViewModel.GridRowOffset);
        Assert.Equal(501, runtime.ViewModel.GridRows[0].RowNumber);
        Assert.Equal("501", runtime.ViewModel.GridRows[0].Cells[0]);
        Assert.Equal($"Rows 501–{Numbers(1000)} of {Numbers(1200)}", runtime.ViewModel.GridPageSummary);

        await runtime.ViewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Equal(200, runtime.ViewModel.GridRows.Count);
        Assert.Equal("1200", runtime.ViewModel.GridRows[^1].Cells[0]);
        Assert.False(runtime.ViewModel.NextPageCommand.CanExecute(null));

        await runtime.ViewModel.PreviousPageCommand.ExecuteAsync(null);

        Assert.Equal(500, runtime.ViewModel.GridRowOffset);
        Assert.Equal(500, runtime.ViewModel.GridRows.Count);
    }

    [Fact]
    public async Task PasteReloadsTheFirstPage()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("Reg\n" + string.Join("\n", Enumerable.Range(1, 700)) + "\n");
        await runtime.ViewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(500, runtime.ViewModel.GridRowOffset);

        await runtime.PasteAsync("Bin\n1\n");

        Assert.Equal(0, runtime.ViewModel.GridRowOffset);
        Assert.Equal(["1", "1"], runtime.Cells[0]);
    }

    // 10
    [Fact]
    public async Task SwitchingWorksheetsLoadsThatWorksheetsPage()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        await runtime.PasteAsync("No\tSITE\n1\t1\n2\t2\n");

        var sheet2 = await runtime.CreateWorksheetAsync("Sheet2");
        Assert.Empty(runtime.ViewModel.GridColumns);
        Assert.Empty(runtime.ViewModel.GridRows);
        Assert.Equal(0, runtime.ViewModel.TotalRowCount);

        await runtime.PasteAsync("Lot\nN123\n");
        Assert.Equal(["Lot"], runtime.Headers);

        await runtime.SelectAsync(sheet1);
        Assert.Equal(["No", "SITE"], runtime.Headers);
        Assert.Equal([["1", "1"], ["2", "2"]], runtime.Cells);
        Assert.Equal(2, runtime.ViewModel.TotalRowCount);

        await runtime.SelectAsync(sheet2);
        Assert.Equal([["N123"]], runtime.Cells);
        Assert.Equal(1, runtime.ViewModel.TotalRowCount);
    }

    [Fact]
    public async Task ClearingTheWorksheetSelectionClearsTheGrid()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(CanonicalText);

        await runtime.SelectAsync(null!);

        Assert.Empty(runtime.ViewModel.GridColumns);
        Assert.Empty(runtime.ViewModel.GridRows);
        Assert.Equal(0, runtime.ViewModel.TotalRowCount);
    }

    // 12
    [Fact]
    public async Task HeaderSelectionDrivesTheSelectedColumnPasteTarget()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync(CanonicalText);
        var bin = runtime.ViewModel.GridColumns[1];

        runtime.ViewModel.ToggleColumnSelectionCommand.Execute(bin.ColumnId);
        Assert.Equal(bin.ColumnId, runtime.ViewModel.SelectedColumn?.Id);

        await runtime.PasteAsync("Wafer\tDie\n7\t8\n");

        Assert.Equal(["No", "Wafer", "Die", "Reg1", "Reg2"], runtime.Headers);
        Assert.Equal(bin.ColumnId, runtime.ViewModel.GridColumns[1].ColumnId);
        Assert.Equal(["1", "7", "8", "5", "0.132"], runtime.Cells[0]);
        Assert.Equal(bin.ColumnId, runtime.ViewModel.SelectedColumn?.Id);

        runtime.ViewModel.ToggleColumnSelectionCommand.Execute(bin.ColumnId);
        Assert.Null(runtime.ViewModel.SelectedColumn);

        runtime.ViewModel.ToggleColumnSelectionCommand.Execute(Guid.NewGuid());
        Assert.Null(runtime.ViewModel.SelectedColumn);
    }

    [Fact]
    public async Task MetadataOnlyColumnAppearsBlankWithoutError()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.PasteAsync("No\n1\n2\n");

        runtime.ViewModel.ColumnName = "Vth";
        await runtime.ViewModel.AddColumnCommand.ExecuteAsync(null);
        await runtime.ViewModel.GridLoadTask;

        Assert.Null(runtime.ViewModel.ErrorMessage);
        Assert.Equal(["No", "Vth"], runtime.Headers);
        Assert.Equal([["1", ""], ["2", ""]], runtime.Cells);
    }

    [Theory]
    [InlineData("Reg\n", "0 rows")]
    [InlineData("Reg\n1\n", "1 row")]
    [InlineData("Reg\n1\n2\n", "2 rows")]
    public async Task SummaryUsesTheRawRowCount(string clipboardText, string expectedRows)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.PasteAsync(clipboardText);

        Assert.Equal($"{expectedRows} · 1 column", runtime.ViewModel.SelectedWorksheetSummary);
    }

    [Fact]
    public async Task StorageFailureWhileLoadingShowsGenericMessage()
    {
        using var directory = new TemporaryDirectory();
        // A directory cannot be opened as a DuckDB database, so reading the row count fails in the storage layer.
        using var runtime = new Runtime(directory.DirectoryPath);

        await runtime.StartAsync();

        Assert.Equal("The worksheet data could not be loaded.", runtime.ViewModel.ErrorMessage);
        Assert.Empty(runtime.ViewModel.GridRows);
        Assert.False(runtime.ViewModel.IsGridLoading);
    }

    // 13
    [Fact]
    public void GridStateIsDisplayTextOnly()
    {
        Assert.Equal(
            [("Cells", typeof(IReadOnlyList<string>)), ("RowNumber", typeof(long))],
            typeof(WorksheetGridRow).GetProperties()
                .Where(property => property.Name != "EqualityContract")
                .Select(property => (property.Name, property.PropertyType))
                .OrderBy(property => property.Name, StringComparer.Ordinal));
        Assert.Equal(500, MainWindowSession.GridPageSize);
    }
}
