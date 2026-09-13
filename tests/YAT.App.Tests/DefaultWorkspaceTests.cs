using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

// Startup workspace: "Untitled Project" with "Sheet1" selected, created through the normal commands.
public class DefaultWorkspaceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Runtime : IDisposable
    {
        public Runtime()
        {
            var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
            ProjectSession = compositionRoot.CreateProjectSession(":memory:");
            ViewModel = compositionRoot.CreateMainWindowViewModel(compositionRoot.CreateMainWindowSession(ProjectSession, Clipboard));
        }

        public FakeClipboardTextReader Clipboard { get; } = new();

        public ProjectSession ProjectSession { get; }

        public MainWindowViewModel ViewModel { get; }

        public void Dispose() => ProjectSession.Dispose();
    }

    [Fact]
    public void DefaultNamesMatchANewWorkbook()
    {
        Assert.Equal("Untitled Project", MainWindowViewModel.DefaultProjectName);
        Assert.Equal("Sheet1", MainWindowViewModel.DefaultWorksheetName);
    }

    // 1
    [Fact]
    public async Task CreatesUntitledProject()
    {
        using var runtime = new Runtime();

        await runtime.ViewModel.CreateDefaultWorkspaceAsync();

        var project = runtime.ViewModel.CurrentProject;
        Assert.NotNull(project);
        Assert.True(runtime.ViewModel.HasCurrentProject);
        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.Equal("Untitled Project", project.Name);
        Assert.Null(project.Description);
        Assert.Equal(Now, project.CreatedAt);
    }

    // 2
    [Fact]
    public async Task Sheet1ExistsInTheProjectSession()
    {
        using var runtime = new Runtime();

        await runtime.ViewModel.CreateDefaultWorkspaceAsync();

        var sheet1 = Assert.Single(runtime.ViewModel.Worksheets);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Equal(runtime.ViewModel.CurrentProject!.Id, sheet1.ProjectId);
        Assert.Same(sheet1, await runtime.ProjectSession.Worksheets.GetByIdAsync(sheet1.Id, Token));
    }

    // 3
    [Fact]
    public async Task Sheet1IsSelected()
    {
        using var runtime = new Runtime();

        await runtime.ViewModel.CreateDefaultWorkspaceAsync();

        Assert.Same(Assert.Single(runtime.ViewModel.Worksheets), runtime.ViewModel.SelectedWorksheet);
        Assert.NotNull(runtime.ViewModel.SelectedWorksheetColumns);
        Assert.Empty(runtime.ViewModel.SelectedWorksheetColumns);
        Assert.Null(runtime.ViewModel.SelectedColumn);
        Assert.Equal("0 rows · 0 columns", runtime.ViewModel.SelectedWorksheetSummary);
    }

    // 4
    [Fact]
    public async Task UiStateMatchesManualCreation()
    {
        using var runtime = new Runtime();

        await runtime.ViewModel.CreateDefaultWorkspaceAsync();

        Assert.Equal(string.Empty, runtime.ViewModel.ProjectName);
        Assert.Equal(string.Empty, runtime.ViewModel.ProjectDescription);
        Assert.Equal(string.Empty, runtime.ViewModel.WorksheetName);
        Assert.Null(runtime.ViewModel.ErrorMessage);
        Assert.False(runtime.ViewModel.IsBusy);
        Assert.True(runtime.ViewModel.CreateProjectCommand.CanExecute(null));
        Assert.True(runtime.ViewModel.CreateWorksheetCommand.CanExecute(null));
        Assert.True(runtime.ViewModel.AddColumnCommand.CanExecute(null));
    }

    // 5
    [Fact]
    public async Task PasteIsAvailableImmediatelyAfterStartup()
    {
        using var runtime = new Runtime();

        await runtime.ViewModel.CreateDefaultWorkspaceAsync();

        Assert.True(runtime.ViewModel.PasteCommand.CanExecute(null));

        runtime.Clipboard.Text = "No\tSITE\tReg1\n1\t1\t5\n";
        await runtime.ViewModel.PasteCommand.ExecuteAsync(null);

        Assert.Equal(["No", "SITE", "Reg1"], runtime.ViewModel.SelectedWorksheetColumns!.Select(column => column.Name));
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        Assert.Equal(3, (await runtime.ProjectSession.WorksheetColumns.GetByWorksheetIdAsync(sheet1.Id, Token)).Count);
    }

    // 6
    [Fact]
    public async Task CreatingWorksheetsAndProjectsWorksAsBeforeAfterStartup()
    {
        using var runtime = new Runtime();
        await runtime.ViewModel.CreateDefaultWorkspaceAsync();
        var untitled = runtime.ViewModel.CurrentProject;

        runtime.ViewModel.WorksheetName = "WAT_Lot_A";
        await runtime.ViewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.Equal(["Sheet1", "WAT_Lot_A"], runtime.ViewModel.Worksheets.Select(worksheet => worksheet.Name));
        Assert.Equal("WAT_Lot_A", runtime.ViewModel.SelectedWorksheet?.Name);

        runtime.ViewModel.ProjectName = "ALS_2026_09";
        await runtime.ViewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.NotSame(untitled, runtime.ViewModel.CurrentProject);
        Assert.Equal("ALS_2026_09", runtime.ViewModel.CurrentProject?.Name);
        Assert.Empty(runtime.ViewModel.Worksheets);
        Assert.Null(runtime.ViewModel.SelectedWorksheet);
    }

    [Fact]
    public async Task DoesNothingWhenAProjectAlreadyExists()
    {
        using var runtime = new Runtime();
        await runtime.ViewModel.CreateDefaultWorkspaceAsync();
        var project = runtime.ViewModel.CurrentProject;
        var sheet1 = runtime.ViewModel.SelectedWorksheet;

        await runtime.ViewModel.CreateDefaultWorkspaceAsync();

        Assert.Same(project, runtime.ViewModel.CurrentProject);
        Assert.Same(sheet1, Assert.Single(runtime.ViewModel.Worksheets));
    }

    [Fact]
    public void ViewModelStillStartsEmptyUntilTheDefaultWorkspaceIsCreated()
    {
        using var runtime = new Runtime();

        Assert.Null(runtime.ViewModel.CurrentProject);
        Assert.Empty(runtime.ViewModel.Worksheets);
    }
}
