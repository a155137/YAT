using Avalonia.Controls;
using Avalonia.Input;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;

namespace YAT.App.Tests;

// Deleting the selected column through MainWindowViewModel → MainWindowSession → handler → repositories and DuckDB.
public class WorksheetColumnDeletionTests
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

        public string[] Headers => ViewModel.GridColumns.Select(column => column.Name).ToArray();

        public string[][] Cells => ViewModel.GridRows.Select(row => row.Cells.ToArray()).ToArray();

        public async Task StartAsync(string? pastedText = CanonicalText)
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
            if (pastedText is not null)
            {
                await PasteAsync(pastedText);
            }
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
        }

        public void Select(string columnName) =>
            ViewModel.SelectColumn(ViewModel.GridColumns.Single(column => column.Name == columnName).ColumnId);

        public async Task DeleteSelectedAsync()
        {
            await ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
        }

        public void Dispose() => ProjectSession.Dispose();
    }

    // 12, 14
    [Fact]
    public async Task DeletingAMiddleColumnRefreshesMetadataAndGridContiguously()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var idsBefore = runtime.ViewModel.GridColumns.ToDictionary(column => column.Name, column => column.ColumnId);

        runtime.Select("SITE");
        await runtime.DeleteSelectedAsync();

        Assert.Equal(["No", "Bin", "Reg1", "Reg2"], runtime.Headers);
        Assert.Equal([0, 1, 2, 3], runtime.ViewModel.GridColumns.Select(column => column.Index));
        Assert.Equal(
            [idsBefore["No"], idsBefore["Bin"], idsBefore["Reg1"], idsBefore["Reg2"]],
            runtime.ViewModel.GridColumns.Select(column => column.ColumnId));
        Assert.Equal([["1", "1", "5", "0.132"], ["2", "2", "7", "0.157"], ["3", "1", "2", "0.122"]], runtime.Cells);
        Assert.Equal(["No", "Bin", "Reg1", "Reg2"], runtime.ViewModel.SelectedWorksheetColumns!.Select(column => column.Name));
        Assert.Equal("3 rows · 4 columns", runtime.ViewModel.SelectedWorksheetSummary);
        Assert.Null(runtime.ViewModel.ErrorMessage);

        var stored = await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(runtime.ViewModel.SelectedWorksheet!.Id, Token);
        Assert.Equal([0, 1, 2, 3], stored.Select(column => column.Index));
        Assert.DoesNotContain(idsBefore["SITE"], await runtime.ProjectSession.RawDataStore.GetStoredColumnIdsAsync(runtime.ViewModel.SelectedWorksheet.Id, Token));
    }

    // 13
    [Fact]
    public async Task DeletingClearsTheSelectedColumn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Select("Bin");
        Assert.True(runtime.ViewModel.DeleteSelectedColumnsCommand.CanExecute(null));
        await runtime.DeleteSelectedAsync();

        Assert.Null(runtime.ViewModel.ActiveColumn);
        Assert.Empty(runtime.ViewModel.SelectedColumns);
        Assert.False(runtime.ViewModel.DeleteSelectedColumnsCommand.CanExecute(null));
    }

    // 15
    [Fact]
    public async Task DeleteWithoutASelectedColumnIsANoOp()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        Assert.False(runtime.ViewModel.DeleteSelectedColumnsCommand.CanExecute(null));
        await runtime.DeleteSelectedAsync();

        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Reg2"], runtime.Headers);
        Assert.Null(runtime.ViewModel.ErrorMessage);
    }

    [Fact]
    public async Task DeletingTheLongestColumnLowersTheRowCount()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync("No\tReg1\n1\t5\n2\t7\n");
        await runtime.PasteAsync("Long\n1\n2\n3\n4\n5\n");
        Assert.Equal(5, runtime.ViewModel.TotalRowCount);

        runtime.Select("Long");
        await runtime.DeleteSelectedAsync();

        Assert.Equal(2, runtime.ViewModel.TotalRowCount);
        Assert.Equal(2, runtime.ViewModel.GridRows.Count);
    }

    [Fact]
    public async Task DeletingTheLastColumnLeavesAnEmptyWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync("SITE\n1\n2\n");

        runtime.Select("SITE");
        await runtime.DeleteSelectedAsync();

        Assert.Empty(runtime.ViewModel.GridColumns);
        Assert.Empty(runtime.ViewModel.GridRows);
        Assert.False(runtime.ViewModel.HasGridColumns);
        Assert.Equal(0, runtime.ViewModel.TotalRowCount);
        Assert.Equal("0 rows · 0 columns", runtime.ViewModel.SelectedWorksheetSummary);
    }

    // 17
    [Fact]
    public async Task SelectedColumnPasteStillWorksAfterADelete()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Select("SITE");
        await runtime.DeleteSelectedAsync();

        runtime.Select("Reg1");
        await runtime.PasteAsync("Vth\n0.45\n");

        Assert.Equal(["No", "Bin", "Vth", "Reg2"], runtime.Headers);
        Assert.Equal(["1", "1", "0.45", "0.132"], runtime.Cells[0]);
        Assert.Equal("Vth", runtime.ViewModel.ActiveColumn?.Name);
    }

    [Fact]
    public async Task MetadataOnlyColumnCanBeDeleted()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync("No\n1\n");
        runtime.ViewModel.ColumnName = "Vth";
        await runtime.ViewModel.AddColumnCommand.ExecuteAsync(null);
        await runtime.ViewModel.GridLoadTask;

        runtime.Select("Vth");
        await runtime.DeleteSelectedAsync();

        Assert.Equal(["No"], runtime.Headers);
        Assert.Null(runtime.ViewModel.ErrorMessage);
    }

    [Fact]
    public async Task StorageFailureShowsAGenericMessageAndKeepsTheColumn()
    {
        using var directory = new TemporaryDirectory();
        // A directory cannot be opened as a DuckDB database, so the raw-storage step of the delete fails.
        using var runtime = new Runtime(directory.DirectoryPath);
        await runtime.StartAsync(pastedText: null);
        runtime.ViewModel.ColumnName = "Vth";
        await runtime.ViewModel.AddColumnCommand.ExecuteAsync(null);
        await runtime.ViewModel.GridLoadTask;
        var vth = Assert.Single(runtime.ViewModel.SelectedWorksheetColumns!);
        runtime.ViewModel.SelectColumn(vth.Id);

        await runtime.DeleteSelectedAsync();

        Assert.Equal("The column could not be deleted.", runtime.ViewModel.ErrorMessage);
        Assert.Same(vth, Assert.Single(runtime.ViewModel.SelectedWorksheetColumns!));
        Assert.Single(await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(runtime.ViewModel.SelectedWorksheet!.Id, Token));
        Assert.False(runtime.ViewModel.IsBusy);
    }

    // 16: Delete routing: only an unhandled Delete outside text editors reaches the column delete.
    [Fact]
    public void DeleteKeyInsideATextBoxIsNotAColumnDelete()
    {
        Assert.False(WorksheetKeyRouting.IsDeleteColumnsGesture(Key.Delete, KeyModifiers.None, handled: false, new TextBox()));
    }

    [Fact]
    public void UnhandledDeleteOutsideTextEditorsIsAColumnDelete()
    {
        var panel = new StackPanel();
        var grid = new TableView();
        panel.Children.Add(grid);

        Assert.True(WorksheetKeyRouting.IsDeleteColumnsGesture(Key.Delete, KeyModifiers.None, handled: false, grid));
        Assert.True(WorksheetKeyRouting.IsDeleteColumnsGesture(Key.Delete, KeyModifiers.None, handled: false, null));
    }

    [Theory]
    [InlineData(Key.Delete, KeyModifiers.None, true)]
    [InlineData(Key.Delete, KeyModifiers.Control, false)]
    [InlineData(Key.Delete, KeyModifiers.Shift, false)]
    [InlineData(Key.Back, KeyModifiers.None, false)]
    public void HandledModifiedOrOtherKeysAreNotAColumnDelete(Key key, KeyModifiers modifiers, bool handled)
    {
        Assert.False(WorksheetKeyRouting.IsDeleteColumnsGesture(key, modifiers, handled, new Border()));
    }

    // 19
    [Fact]
    public void DeleteCommandIsOnTheViewModelAndDependsOnlyOnTheSession()
    {
        Assert.NotNull(typeof(MainWindowViewModel).GetProperty(nameof(MainWindowViewModel.DeleteSelectedColumnsCommand)));
        Assert.Equal(
            [typeof(Guid), typeof(IReadOnlyList<Guid>), typeof(CancellationToken)],
            typeof(MainWindowSession).GetMethod(nameof(MainWindowSession.DeleteColumnsAsync))!.GetParameters().Select(parameter => parameter.ParameterType));
    }
}
