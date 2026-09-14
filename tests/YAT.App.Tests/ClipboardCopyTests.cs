using System.Reflection;
using Avalonia.Controls;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;

namespace YAT.App.Tests;

// Ctrl+C / "Copy N Columns": selected columns → MainWindowSession → chunked export → clipboard text.
public class ClipboardCopyTests
{
    private const string Dataset =
        "No\tBin\tSITE\tReg1\tLot\n" +
        "1\t1\t1\t5\tN123\n" +
        "2\t2\t2\t\t\n" +
        "3\t1\t3\t.132\tN125\n";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class Runtime : IDisposable
    {
        public Runtime(string databasePath = ":memory:")
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(databasePath);
            ViewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(ProjectSession, Clipboard, Clipboard));
        }

        public FakeClipboard Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public string[] Headers => ViewModel.GridColumns.Select(column => column.Name).ToArray();

        public async Task StartAsync(string? dataset = Dataset)
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
            if (dataset is not null)
            {
                await PasteAsync(dataset);
            }
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
        }

        public Guid Id(string name) => ViewModel.GridColumns.Single(column => column.Name == name).ColumnId;

        public async Task<string> CopyAsync()
        {
            await ViewModel.CopySelectedColumnsCommand.ExecuteAsync(null);
            return Assert.Single(Clipboard.Writes);
        }

        public void Dispose() => ProjectSession.Dispose();
    }

    // 1
    [Fact]
    public async Task OneSelectedColumnCopiesItsHeaderAndValues()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.SelectColumn(runtime.Id("SITE"));
        var text = await runtime.CopyAsync();

        Assert.Equal("SITE\r\n1\r\n2\r\n3\r\n", text);
        Assert.Null(runtime.ViewModel.ErrorMessage);
        Assert.False(runtime.ViewModel.IsBusy);
    }

    // 2, 4, 5
    [Fact]
    public async Task ARangeCopiesInIndexOrderWithEmptyFieldsForEmptyCells()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.SelectColumn(runtime.Id("Lot"));
        runtime.ViewModel.ExtendColumnSelection(runtime.Id("SITE"));
        var text = await runtime.CopyAsync();

        Assert.Equal("SITE\tReg1\tLot\r\n1\t5\tN123\r\n2\t\t\r\n3\t0.132\tN125\r\n", text);
    }

    // 3
    [Fact]
    public async Task NonContiguousSelectionCopiesTheSelectedColumnsInIndexOrder()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.SelectColumn(runtime.Id("Lot"));
        runtime.ViewModel.ToggleColumnSelection(runtime.Id("No"));
        runtime.ViewModel.ToggleColumnSelection(runtime.Id("SITE"));
        var text = await runtime.CopyAsync();

        Assert.Equal("No\tSITE\tLot\r\n1\t1\tN123\r\n2\t2\t\r\n3\t3\tN125\r\n", text);
    }

    // 6
    [Fact]
    public async Task WithoutSelectedColumnsCopyIsANoOp()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        Assert.False(runtime.ViewModel.CopySelectedColumnsCommand.CanExecute(null));
        await runtime.ViewModel.CopySelectedColumnsCommand.ExecuteAsync(null);

        Assert.Empty(runtime.Clipboard.Writes);
        Assert.Null(runtime.ViewModel.ErrorMessage);
    }

    // 7
    [Fact]
    public void CtrlCInsideATextBoxIsNotAWorksheetCopy()
    {
        var panel = new StackPanel();
        var grid = new TableView();
        panel.Children.Add(grid);

        Assert.False(WorksheetKeyRouting.IsCopyColumnsGesture(matchesCopyGesture: true, handled: false, new TextBox()));
        Assert.True(WorksheetKeyRouting.IsCopyColumnsGesture(matchesCopyGesture: true, handled: false, grid));
        Assert.False(WorksheetKeyRouting.IsCopyColumnsGesture(matchesCopyGesture: true, handled: true, grid));
        Assert.False(WorksheetKeyRouting.IsCopyColumnsGesture(matchesCopyGesture: false, handled: false, grid));
    }

    // 8, 9
    [Fact]
    public async Task CopyMenuTextUsesTheSelectedCount()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.SelectColumn(runtime.Id("No"));
        Assert.Equal("Copy Column", runtime.ViewModel.CopySelectedColumnsMenuText);

        runtime.ViewModel.ExtendColumnSelection(runtime.Id("SITE"));
        Assert.Equal("Copy 3 Columns", runtime.ViewModel.CopySelectedColumnsMenuText);
        Assert.Equal("Delete 3 Columns", runtime.ViewModel.DeleteSelectedColumnsMenuText);
        Assert.True(runtime.ViewModel.CopySelectedColumnsCommand.CanExecute(null));
    }

    // 10
    [Fact]
    public async Task CopiedValuesAreNotKeptOnTheViewModel()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.ViewModel.SelectColumn(runtime.Id("Lot"));
        runtime.ViewModel.ExtendColumnSelection(runtime.Id("No"));

        var text = await runtime.CopyAsync();

        const BindingFlags members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var stringValues = typeof(MainWindowViewModel).GetFields(members)
            .Where(field => field.FieldType == typeof(string))
            .Select(field => (string?)field.GetValue(runtime.ViewModel));
        Assert.DoesNotContain(stringValues, value => value is not null && (value == text || value.Contains('\t')));
        Assert.DoesNotContain(typeof(MainWindowViewModel).GetFields(members), field => field.FieldType.Namespace == "YAT.app.Clipboard");
    }

    // 11
    [Fact]
    public async Task CopyLeavesSelectionPasteAndDeleteWorking()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.ViewModel.SelectColumn(runtime.Id("Bin"));
        runtime.ViewModel.ExtendColumnSelection(runtime.Id("SITE"));

        var copied = await runtime.CopyAsync();

        Assert.Equal(["Bin", "SITE"], runtime.ViewModel.SelectedColumns.Select(column => column.Name));
        Assert.Equal("Bin", runtime.ViewModel.ActiveColumn?.Name);

        // The copied text is valid paste input: pasting it after the last column appends the same two columns.
        runtime.ViewModel.ClearColumnSelection();
        await runtime.PasteAsync(copied);
        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Lot", "Bin_1", "SITE_1"], runtime.Headers);
        Assert.Equal(["1", "1", "1", "5", "N123", "1", "1"], runtime.ViewModel.GridRows[0].Cells);

        runtime.ViewModel.SelectColumn(runtime.Id("Bin_1"));
        runtime.ViewModel.ExtendColumnSelection(runtime.Id("SITE_1"));
        await runtime.ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        await runtime.ViewModel.GridLoadTask;
        Assert.Equal(["No", "Bin", "SITE", "Reg1", "Lot"], runtime.Headers);
    }

    [Fact]
    public async Task StorageFailureShowsAGenericMessageAndLeavesTheClipboardUnchanged()
    {
        using var directory = new TemporaryDirectory();
        // A directory cannot be opened as a DuckDB database, so reading the columns for the copy fails.
        using var runtime = new Runtime(directory.DirectoryPath);
        await runtime.StartAsync(dataset: null);
        runtime.ViewModel.ColumnName = "Vth";
        await runtime.ViewModel.AddColumnCommand.ExecuteAsync(null);
        await runtime.ViewModel.GridLoadTask;
        runtime.ViewModel.SelectColumn(Assert.Single(runtime.ViewModel.SelectedWorksheetColumns!).Id);

        await runtime.ViewModel.CopySelectedColumnsCommand.ExecuteAsync(null);

        Assert.Equal("The column could not be copied.", runtime.ViewModel.ErrorMessage);
        Assert.Empty(runtime.Clipboard.Writes);
        Assert.False(runtime.ViewModel.IsBusy);
    }
}
