using YAT.App.Tests.TestDoubles;
using YAT.Application.Exceptions;
using YAT.Application.Features.Worksheets.AddWorksheetColumn;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Enums;
using CreateWorksheetRequest = YAT.Application.Features.Worksheets.CreateWorksheet.CreateWorksheetCommand;
using RenameWorksheetRequest = YAT.Application.Features.Worksheets.RenameWorksheet.RenameWorksheetCommand;

namespace YAT.App.Tests;

// MainWindowSession support for the project lifecycle: ProjectModified after successful edits only, and suspension and
// retirement for session switches.
public class MainWindowSessionLifecycleTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            var composition = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = composition.CreateProjectSession(":memory:");
            Session = composition.CreateMainWindowSession(ProjectSession, Clipboard, Clipboard);
            ViewModel = composition.CreateMainWindowViewModel(Session);
            Session.ProjectModified += (sender, _) =>
            {
                Assert.Same(Session, sender);
                Modifications++;
            };
        }

        public FakeClipboard Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowSession Session { get; }

        public MainWindowViewModel ViewModel { get; }

        public int Modifications { get; set; }

        public async Task StartAsync()
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
            Modifications = 0;
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
        }

        public void Dispose() => ProjectSession.Dispose();
    }

    [Fact]
    public async Task SuccessfulEditsRaiseProjectModified()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.PasteAsync("Lot\tVth\nA\t0.41\n");
        Assert.Equal(1, runtime.Modifications);

        runtime.ViewModel.SelectColumn(runtime.ViewModel.SelectedWorksheetColumns![0].Id);
        await runtime.ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
        Assert.Equal(2, runtime.Modifications);

        await runtime.ViewModel.NewWorksheetCommand.ExecuteAsync(null);
        Assert.Equal(3, runtime.Modifications);

        var explorer = runtime.ViewModel.ProjectExplorer;
        explorer.BeginRename(explorer.ProjectItem!);
        explorer.ProjectItem!.EditName = "Renamed";
        Assert.True(await explorer.CommitRenameAsync(explorer.ProjectItem));
        Assert.Equal(4, runtime.Modifications);

        var sheet = explorer.WorksheetItems.First();
        explorer.BeginRename(sheet);
        sheet.EditName = "Lot Data";
        Assert.True(await explorer.CommitRenameAsync(sheet));
        Assert.Equal(5, runtime.Modifications);

        runtime.ViewModel.ColumnName = "Idsat";
        runtime.ViewModel.SelectedColumnSemanticType = ColumnSemanticType.TestParameter;
        await runtime.ViewModel.AddColumnCommand.ExecuteAsync(null);
        Assert.Equal(6, runtime.Modifications);
    }

    [Fact]
    public async Task ReadsCopiesAndFailedOrRejectedEditsDoNotRaiseProjectModified()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var worksheetId = runtime.ViewModel.SelectedWorksheet!.Id;

        await runtime.PasteAsync("Lot\tVth\nA\n");            // malformed: rejected
        runtime.Clipboard.Text = "   ";
        await runtime.ViewModel.PasteCommand.ExecuteAsync(null);  // nothing to paste
        await Assert.ThrowsAsync<ValidationException>(() =>
            runtime.Session.RenameWorksheetAsync(new RenameWorksheetRequest(worksheetId, "  "), Token));
        await Assert.ThrowsAsync<ValidationException>(() =>
            runtime.Session.CreateWorksheetAsync(new CreateWorksheetRequest(runtime.ViewModel.CurrentProject!.Id, "sheet1"), Token));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => runtime.Session.AddWorksheetColumnAsync(
            new AddWorksheetColumnCommand(Guid.NewGuid(), 0, "Vth", WorksheetDataType.Numeric, null, null), Token));
        Assert.Equal(0, runtime.Modifications);

        await runtime.PasteAsync("Lot\nA\n");
        runtime.Modifications = 0;
        await runtime.Session.LoadWorksheetPageAsync(worksheetId, 0, Token);
        await runtime.Session.LoadProjectAsync(Token);
        runtime.ViewModel.SelectColumn(runtime.ViewModel.SelectedWorksheetColumns![0].Id);
        await runtime.ViewModel.CopySelectedColumnsCommand.ExecuteAsync(null);

        Assert.Equal(0, runtime.Modifications);
    }

    [Fact]
    public async Task SuspendedSessionHoldsOperationsUntilResumed()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var worksheetId = runtime.ViewModel.SelectedWorksheet!.Id;

        await runtime.Session.SuspendAsync(Token);
        var page = runtime.Session.LoadWorksheetPageAsync(worksheetId, 0, Token);
        await Task.Delay(100, Token);

        Assert.False(page.IsCompleted);

        runtime.Session.Resume();
        Assert.Equal(0, (await page).TotalRowCount);
    }

    [Fact]
    public async Task RetiredSessionRejectsWaitingAndLaterOperations()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var worksheetId = runtime.ViewModel.SelectedWorksheet!.Id;

        await runtime.Session.SuspendAsync(Token);
        var waiting = runtime.Session.RenameWorksheetAsync(new RenameWorksheetRequest(worksheetId, "Renamed"), Token);
        runtime.Session.Retire();

        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => waiting);
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => runtime.Session.LoadWorksheetPageAsync(worksheetId, 0, Token));
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => runtime.Session.SuspendAsync(Token));
        runtime.Clipboard.Text = "Lot\nA\n";
        await Assert.ThrowsAsync<ProjectSessionClosedException>(() => runtime.Session.PasteFromClipboardAsync(worksheetId, null, Token));

        Assert.Equal("Sheet1", (await runtime.ProjectSession.Worksheets.GetByIdAsync(worksheetId, Token))!.Name);
        Assert.Empty(await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(worksheetId, Token));
        Assert.Equal(0, runtime.Modifications);
    }

    [Fact]
    public async Task ViewModelCommandsOnARetiredSessionChangeNothingAndDoNotThrow()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var worksheetId = runtime.ViewModel.SelectedWorksheet!.Id;
        await runtime.Session.SuspendAsync(Token);
        runtime.Session.Retire();

        await runtime.PasteAsync("Lot\nA\n");
        await runtime.ViewModel.NewWorksheetCommand.ExecuteAsync(null);
        await runtime.ViewModel.NextPageCommand.ExecuteAsync(null);

        Assert.Null(runtime.ViewModel.ErrorMessage);
        Assert.False(runtime.ViewModel.IsBusy);
        Assert.Empty(await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(worksheetId, Token));
    }
}
