using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

public class WorksheetWindowSessionTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SessionLoadsMetadataAndColumnWindow_PreservingLegacyPageAndLifecycle()
    {
        var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
        using var projectSession = compositionRoot.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        var session = compositionRoot.CreateMainWindowSession(projectSession, clipboard, clipboard);
        var viewModel = compositionRoot.CreateMainWindowViewModel(session);

        await viewModel.CreateDefaultWorkspaceAsync();
        await viewModel.GridLoadTask;

        clipboard.Text = "A\tB\tC\n1\tHello\t\n2\t\t3.5\n";
        await viewModel.PasteCommand.ExecuteAsync(null);
        var worksheetId = viewModel.SelectedWorksheet!.Id;

        var meta = await session.LoadWorksheetPageMetadataAsync(worksheetId, 0, Token);
        Assert.Equal(2, meta.TotalRowCount);
        Assert.Equal(2, meta.RowCount);
        Assert.Equal(["A", "B", "C"], meta.Columns.Select(c => c.Name));

        var window = await session.LoadWorksheetColumnWindowAsync(
            worksheetId,
            0,
            [meta.Columns[2].Id, meta.Columns[0].Id],
            Token);

        Assert.Equal(2, window.RowCount);
        Assert.Equal(["C", "A"], window.Columns.Select(c => c.Column.Name));
        Assert.Equal([null, 3.5d], Assert.IsType<NumericRawDataColumn>(window.Columns[0].Values).Values);
        Assert.Equal([1d, 2d], Assert.IsType<NumericRawDataColumn>(window.Columns[1].Values).Values);

        // Legacy LoadWorksheetPageAsync continues to return formatted rows.
        var legacy = await session.LoadWorksheetPageAsync(worksheetId, 0, Token);
        Assert.Equal(["1", "Hello", ""], legacy.Rows[0].Cells);
        Assert.Equal(["2", "", "3.5"], legacy.Rows[1].Cells);

        // Cancellation is respected.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            session.LoadWorksheetColumnWindowAsync(worksheetId, 0, [meta.Columns[0].Id], cts.Token));

        // Retired session rejects subsequent window calls.
        await session.SuspendAsync(Token);
        session.Retire();
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() =>
            session.LoadWorksheetPageMetadataAsync(worksheetId, 0, Token));
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() =>
            session.LoadWorksheetColumnWindowAsync(worksheetId, 0, [meta.Columns[0].Id], Token));
    }
}
