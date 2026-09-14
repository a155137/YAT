using Avalonia.Input;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;

namespace YAT.App.Tests;

// Multi-column selection (ActiveColumn + SelectedColumns), batch delete and paste compatibility.
public class ColumnSelectionTests
{
    // Seven columns so ranges and "nearest remaining" choices are unambiguous.
    private const string SevenColumns =
        "A\tB\tC\tD\tE\tF\tG\n" +
        "1\t2\t3\t4\t5\t6\t7\n" +
        "11\t12\t13\t14\t15\t16\t17\n";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(":memory:");
            ViewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(ProjectSession, Clipboard, Clipboard));
        }

        public FakeClipboard Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public string[] Headers => ViewModel.GridColumns.Select(column => column.Name).ToArray();

        public string[] Selected => ViewModel.SelectedColumns.Select(column => column.Name).ToArray();

        public string? Active => ViewModel.ActiveColumn?.Name;

        public async Task StartAsync(string text = SevenColumns)
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
            await PasteAsync(text);
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
        }

        public Guid Id(string name) => ViewModel.GridColumns.Single(column => column.Name == name).ColumnId;

        public void Click(string name) => ViewModel.SelectColumn(Id(name));

        public void CtrlClick(string name) => ViewModel.ToggleColumnSelection(Id(name));

        public void ShiftClick(string name) => ViewModel.ExtendColumnSelection(Id(name));

        public void RightClick(string name) => ViewModel.SelectColumnForContextMenu(Id(name));

        public async Task DeleteSelectionAsync()
        {
            await ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
        }

        public void Dispose() => ProjectSession.Dispose();
    }

    // 1
    [Fact]
    public async Task PlainClickSelectsOnlyThatColumnAndMakesItActive()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Click("B");
        runtime.CtrlClick("D");
        runtime.Click("E");

        Assert.Equal(["E"], runtime.Selected);
        Assert.Equal("E", runtime.Active);

        runtime.Click("E");
        Assert.Equal(["E"], runtime.Selected);
        Assert.Equal("E", runtime.Active);
    }

    // 2
    [Fact]
    public async Task CtrlClickAddsAndRemovesColumns()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Click("B");
        runtime.CtrlClick("E");
        runtime.CtrlClick("D");

        Assert.Equal(["B", "D", "E"], runtime.Selected);
        Assert.Equal("D", runtime.Active);

        runtime.CtrlClick("B");

        Assert.Equal(["D", "E"], runtime.Selected);
        Assert.Equal("D", runtime.Active);
    }

    // 3
    [Fact]
    public async Task ShiftClickSelectsTheContiguousIndexRangeFromTheActiveColumn()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Click("C");
        runtime.ShiftClick("F");

        Assert.Equal(["C", "D", "E", "F"], runtime.Selected);
        Assert.Equal("C", runtime.Active);

        // The anchor stays: a second Shift+Click replaces the range, in either direction.
        runtime.ShiftClick("A");

        Assert.Equal(["A", "B", "C"], runtime.Selected);
        Assert.Equal("C", runtime.Active);
    }

    [Fact]
    public async Task ShiftClickWithoutAnActiveColumnActsLikeAPlainClick()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ShiftClick("D");

        Assert.Equal(["D"], runtime.Selected);
        Assert.Equal("D", runtime.Active);
    }

    // 4
    [Fact]
    public async Task RemovingTheActiveColumnPicksTheNearestSelectedColumnToTheRightThenLeft()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Click("B");
        runtime.CtrlClick("F");
        runtime.CtrlClick("D");

        runtime.CtrlClick("D");
        Assert.Equal(["B", "F"], runtime.Selected);
        Assert.Equal("F", runtime.Active);

        runtime.CtrlClick("F");
        Assert.Equal(["B"], runtime.Selected);
        Assert.Equal("B", runtime.Active);

        runtime.CtrlClick("B");
        Assert.Empty(runtime.Selected);
        Assert.Null(runtime.Active);
    }

    // 6, 7, 8
    [Fact]
    public async Task DeleteRemovesTheWholeSelectionAsOneBatch()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var survivors = new[] { "A", "C", "F", "G" }.Select(runtime.Id).ToArray();
        var deleted = new[] { "B", "D", "E" }.Select(runtime.Id).ToArray();

        runtime.Click("B");
        runtime.CtrlClick("D");
        runtime.CtrlClick("E");
        await runtime.DeleteSelectionAsync();

        Assert.Equal(["A", "C", "F", "G"], runtime.Headers);
        Assert.Equal([0, 1, 2, 3], runtime.ViewModel.GridColumns.Select(column => column.Index));
        Assert.Equal(survivors, runtime.ViewModel.GridColumns.Select(column => column.ColumnId));
        Assert.Equal(["1", "3", "6", "7"], runtime.ViewModel.GridRows[0].Cells);
        Assert.Equal("2 rows · 4 columns", runtime.ViewModel.SelectedWorksheetSummary);
        Assert.Empty(runtime.Selected);
        Assert.Null(runtime.Active);

        var worksheetId = runtime.ViewModel.SelectedWorksheet!.Id;
        var stored = await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(worksheetId, Token);
        Assert.Equal([0, 1, 2, 3], stored.Select(column => column.Index));
        var storedRaw = await runtime.ProjectSession.RawDataStore.GetStoredColumnIdsAsync(worksheetId, Token);
        Assert.All(deleted, id => Assert.DoesNotContain(id, storedRaw));
        Assert.All(survivors, id => Assert.Contains(id, storedRaw));
    }

    // 9
    [Fact]
    public async Task DeletingEverySelectedColumnLeavesZeroRowsAndZeroColumns()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.Click("A");
        runtime.ShiftClick("G");
        await runtime.DeleteSelectionAsync();

        Assert.Empty(runtime.ViewModel.GridColumns);
        Assert.Equal(0, runtime.ViewModel.TotalRowCount);
        Assert.Equal("0 rows · 0 columns", runtime.ViewModel.SelectedWorksheetSummary);
        Assert.False(runtime.ViewModel.HasGridColumns);
    }

    // 10, 11: the Delete key maps to this command; text boxes keep Delete (routing rules covered in
    // WorksheetColumnDeletionTests). Without a selection the command cannot run.
    [Fact]
    public async Task DeleteCommandRequiresASelection()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        Assert.False(runtime.ViewModel.DeleteSelectedColumnsCommand.CanExecute(null));
        runtime.Click("A");
        runtime.CtrlClick("B");
        Assert.True(runtime.ViewModel.DeleteSelectedColumnsCommand.CanExecute(null));
        Assert.True(WorksheetKeyRouting.IsDeleteColumnsGesture(Key.Delete, KeyModifiers.None, handled: false, source: null));
    }

    // 12
    [Fact]
    public async Task RightClickOnAnUnselectedHeaderReplacesTheSelection()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Click("A");
        runtime.CtrlClick("B");

        runtime.RightClick("E");

        Assert.Equal(["E"], runtime.Selected);
        Assert.Equal("E", runtime.Active);
    }

    // 13
    [Fact]
    public async Task RightClickOnASelectedHeaderKeepsTheMultiSelection()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Click("A");
        runtime.CtrlClick("C");
        runtime.CtrlClick("E");

        runtime.RightClick("C");

        Assert.Equal(["A", "C", "E"], runtime.Selected);
        Assert.Equal("E", runtime.Active);
    }

    // 14
    [Fact]
    public async Task ContextMenuTextUsesTheSelectedCount()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var changes = new List<string>();
        runtime.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.DeleteSelectedColumnsMenuText))
            {
                changes.Add(runtime.ViewModel.DeleteSelectedColumnsMenuText);
            }
        };

        runtime.Click("A");
        Assert.Equal("Delete Column", runtime.ViewModel.DeleteSelectedColumnsMenuText);

        runtime.CtrlClick("B");
        Assert.Equal("Delete 2 Columns", runtime.ViewModel.DeleteSelectedColumnsMenuText);

        // B is active after the Ctrl+Click, so the range is B..E.
        runtime.ShiftClick("E");
        Assert.Equal("Delete 4 Columns", runtime.ViewModel.DeleteSelectedColumnsMenuText);

        Assert.Equal(["Delete Column", "Delete 2 Columns", "Delete 4 Columns"], changes);
    }

    // 16
    [Fact]
    public async Task PasteStartsAtTheActiveColumnRegardlessOfTheOtherSelectedColumns()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Click("B");
        runtime.CtrlClick("F");
        runtime.CtrlClick("D");

        await runtime.PasteAsync("X\tY\n9\t8\n");

        Assert.Equal(["A", "B", "C", "X", "Y", "F", "G"], runtime.Headers);
        Assert.Equal(["B", "X", "F"], runtime.Selected);
        Assert.Equal("X", runtime.Active);
    }

    // 17
    [Fact]
    public async Task WithoutAnActiveColumnPasteAppends()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Click("C");
        runtime.CtrlClick("C");
        Assert.Null(runtime.Active);

        await runtime.PasteAsync("H\n8\n");

        Assert.Equal(["A", "B", "C", "D", "E", "F", "G", "H"], runtime.Headers);
    }

    [Theory]
    [InlineData(KeyModifiers.None, ColumnHeaderClick.Select)]
    [InlineData(KeyModifiers.Control, ColumnHeaderClick.Toggle)]
    [InlineData(KeyModifiers.Meta, ColumnHeaderClick.Toggle)]
    [InlineData(KeyModifiers.Shift, ColumnHeaderClick.Extend)]
    [InlineData(KeyModifiers.Shift | KeyModifiers.Control, ColumnHeaderClick.Extend)]
    [InlineData(KeyModifiers.Alt, ColumnHeaderClick.Select)]
    public void HeaderClickModifiersMapToSelectionGestures(KeyModifiers modifiers, ColumnHeaderClick expected)
    {
        Assert.Equal(expected, WorksheetKeyRouting.GetHeaderClick(modifiers));
    }

    [Fact]
    public async Task ChangingWorksheetsClearsTheColumnSelection()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        runtime.Click("A");
        runtime.CtrlClick("B");

        runtime.ViewModel.SelectedWorksheet = null;
        Assert.Empty(runtime.Selected);
        Assert.Null(runtime.Active);

        runtime.ViewModel.SelectedWorksheet = sheet1;
        await runtime.ViewModel.GridLoadTask;
        Assert.Empty(runtime.Selected);
    }
}
