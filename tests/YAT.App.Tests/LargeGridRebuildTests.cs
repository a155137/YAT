using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

public class LargeGridRebuildTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static (MainWindowViewModel ViewModel, FakeClipboard Clipboard, ProjectSession Session) CreateRuntime()
    {
        var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
        var session = compositionRoot.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        var viewModel = compositionRoot.CreateMainWindowViewModel(
            compositionRoot.CreateMainWindowSession(session, clipboard, clipboard));
        return (viewModel, clipboard, session);
    }

    [Fact]
    public async Task ApplyGridPage_ClearsGridRowsBeforeUpdatingGridColumns_WhenColumnStructureChanges()
    {
        var (viewModel, clipboard, session) = CreateRuntime();
        using var _ = session;
        await viewModel.CreateDefaultWorkspaceAsync();
        await viewModel.GridLoadTask;

        clipboard.Text = "A\tB\n1\t2\n";
        await viewModel.PasteCommand.ExecuteAsync(null);
        Assert.Single(viewModel.GridRows);

        var rowCountsDuringColumnsChange = new List<int>();
        var events = new List<string>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainWindowViewModel.GridRows) or nameof(MainWindowViewModel.GridColumns))
            {
                events.Add(e.PropertyName);
                if (e.PropertyName == nameof(MainWindowViewModel.GridColumns))
                {
                    rowCountsDuringColumnsChange.Add(viewModel.GridRows.Count);
                }
            }
        };

        viewModel.SelectColumn(viewModel.SelectedWorksheetColumns![0].Id);
        clipboard.Text = "C\tD\n3\t4\n";
        await viewModel.PasteCommand.ExecuteAsync(null);

        Assert.Equal([nameof(MainWindowViewModel.GridRows), nameof(MainWindowViewModel.GridColumns), nameof(MainWindowViewModel.GridRows)], events);
        Assert.Equal([0], rowCountsDuringColumnsChange);
        Assert.Equal(["C", "D"], viewModel.GridColumns.Select(c => c.Name));
        Assert.Equal(["3", "4"], Assert.Single(viewModel.GridRows).Cells);
    }

    [Fact]
    public async Task ApplyGridPage_DoesNotClearGridRows_WhenColumnStructureIsUnchanged()
    {
        var (viewModel, clipboard, session) = CreateRuntime();
        using var _ = session;
        await viewModel.CreateDefaultWorkspaceAsync();
        await viewModel.GridLoadTask;

        clipboard.Text = "A\tB\n1\t2\n";
        await viewModel.PasteCommand.ExecuteAsync(null);
        Assert.Single(viewModel.GridRows);

        var rowCountsOnRowsChanged = new List<int>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.GridRows))
            {
                rowCountsOnRowsChanged.Add(viewModel.GridRows.Count);
            }
        };

        viewModel.SelectColumn(viewModel.SelectedWorksheetColumns![0].Id);
        clipboard.Text = "A\tB\n10\t20\n";
        await viewModel.PasteCommand.ExecuteAsync(null);

        Assert.Equal([1], rowCountsOnRowsChanged);
        Assert.Equal(["10", "20"], Assert.Single(viewModel.GridRows).Cells);
    }

    [Fact]
    public async Task ApplyGridPage_ClearsGridRowsOnMixedOverwriteAndAppend_AndKeepsColumnsOnPagination()
    {
        var (viewModel, clipboard, session) = CreateRuntime();
        using var _ = session;
        await viewModel.CreateDefaultWorkspaceAsync();
        await viewModel.GridLoadTask;

        var headers1 = string.Join('\t', Enumerable.Range(0, 50).Select(i => $"A{i}"));
        var row1 = string.Join('\t', Enumerable.Range(0, 50).Select(i => i.ToString()));
        clipboard.Text = $"{headers1}\n{row1}\n";
        await viewModel.PasteCommand.ExecuteAsync(null);
        Assert.Equal(50, viewModel.GridColumns.Count);
        Assert.Single(viewModel.GridRows);

        var rowCountsDuringColumnsChange = new List<int>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.GridColumns))
            {
                rowCountsDuringColumnsChange.Add(viewModel.GridRows.Count);
            }
        };

        // Mixed overwrite + append (from column 45, 10 columns -> 55 total columns) with > 500 rows.
        viewModel.SelectColumn(viewModel.SelectedWorksheetColumns![45].Id);
        var headers2 = string.Join('\t', Enumerable.Range(45, 10).Select(i => $"M{i}"));
        var rows2 = string.Join('\n', Enumerable.Range(0, 505).Select(r =>
            string.Join('\t', Enumerable.Range(45, 10).Select(c => $"{r}_{c}"))));
        clipboard.Text = $"{headers2}\n{rows2}\n";
        await viewModel.PasteCommand.ExecuteAsync(null);

        Assert.Equal([0], rowCountsDuringColumnsChange);
        Assert.Equal(55, viewModel.GridColumns.Count);
        Assert.Equal(505, viewModel.TotalRowCount);
        Assert.Equal(500, viewModel.GridRows.Count);
        Assert.Equal("A0", viewModel.GridColumns[0].Name);
        Assert.Equal("M45", viewModel.GridColumns[45].Name);
        Assert.Equal("M54", viewModel.GridColumns[54].Name);
        Assert.Equal("0_45", viewModel.GridRows[0].Cells[45]);
        Assert.Equal("0_54", viewModel.GridRows[0].Cells[54]);

        // Pagination to page 2 does not clear GridRows (SequenceEqual columns).
        await viewModel.NextPageCommand.ExecuteAsync(null);
        Assert.Equal([0, 500], rowCountsDuringColumnsChange);
        Assert.Equal(5, viewModel.GridRows.Count);
        Assert.Equal(501L, viewModel.GridRows[0].RowNumber);
        Assert.Equal("504_54", viewModel.GridRows[4].Cells[54]);
    }
}
