using System.ComponentModel;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;

namespace YAT.App.Tests;

// Project Explorer inline rename: edit state in the tree, names stored through MainWindowSession → Application handlers.
public class ProjectExplorerRenameTests
{
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

        public ProjectExplorerViewModel Explorer => ViewModel.ProjectExplorer;

        public ProjectExplorerProjectItem ProjectItem => Explorer.ProjectItem!;

        public ProjectExplorerWorksheetItem ItemNamed(string name) => Assert.Single(Explorer.WorksheetItems, item => item.Name == name);

        public async Task StartAsync()
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
        }

        public async Task NewWorksheetAsync()
        {
            await ViewModel.NewWorksheetCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
        }

        // Double-click, type, Enter.
        public Task<bool> RenameAsync(ProjectExplorerItem item, string name)
        {
            Explorer.BeginRename(item);
            item.EditName = name;
            return Explorer.CommitRenameAsync(item);
        }

        public async Task<Worksheet> StoredWorksheetAsync(Guid id) => (await ProjectSession.Worksheets.GetByIdAsync(id, Token))!;

        public void Dispose() => ProjectSession.Dispose();
    }

    [Fact]
    public async Task BeginRenameEditsTheCurrentName()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");

        runtime.Explorer.BeginRename(sheet1);

        Assert.True(sheet1.IsEditing);
        Assert.Equal("Sheet1", sheet1.EditName);
        Assert.False(runtime.ProjectItem.IsEditing);
    }

    [Fact]
    public async Task OnlyOneItemIsEditedAtATime()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        runtime.Explorer.BeginRename(sheet1);
        sheet1.EditName = "Draft";

        runtime.Explorer.BeginRename(runtime.ProjectItem);

        Assert.False(sheet1.IsEditing);
        Assert.Equal("Sheet1", sheet1.EditName);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.True(runtime.ProjectItem.IsEditing);
    }

    [Fact]
    public async Task EnterRenamesTheWorksheetThroughTheSession()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1Item = runtime.ItemNamed("Sheet1");
        var original = sheet1Item.Worksheet;

        var renamed = await runtime.RenameAsync(sheet1Item, "  WAT_Lot_A  ");

        Assert.True(renamed);
        Assert.False(sheet1Item.IsEditing);
        Assert.Equal("WAT_Lot_A", sheet1Item.Name);
        Assert.Equal("WAT_Lot_A", sheet1Item.EditName);
        Assert.Null(runtime.ViewModel.ErrorMessage);

        // Stored through the repository as a new instance; the instance the UI held before is not modified in place.
        var stored = await runtime.StoredWorksheetAsync(original.Id);
        Assert.Equal("WAT_Lot_A", stored.Name);
        Assert.Same(stored, sheet1Item.Worksheet);
        Assert.NotSame(original, stored);
        Assert.Equal("Sheet1", original.Name);
    }

    [Fact]
    public async Task RenamingTheActiveWorksheetUpdatesTitleAndBreadcrumbSources()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1Item = runtime.ItemNamed("Sheet1");
        var changed = new List<string?>();
        runtime.ViewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await runtime.RenameAsync(sheet1Item, "WAT_Lot_A");

        Assert.Equal("WAT_Lot_A", runtime.ViewModel.SelectedWorksheet!.Name);
        Assert.Same(sheet1Item.Worksheet, runtime.ViewModel.SelectedWorksheet);
        Assert.Same(sheet1Item.Worksheet, Assert.Single(runtime.ViewModel.Worksheets));
        Assert.Contains(nameof(MainWindowViewModel.SelectedWorksheet), changed);
        Assert.True(sheet1Item.IsActive);
        Assert.Same(sheet1Item, runtime.Explorer.SelectedItem);
    }

    [Fact]
    public async Task RenamingTheActiveWorksheetKeepsTheGridPageAndColumnSelection()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.Clipboard.Text = "Lot\tWafer\tYield\nA\t1\t0.91\nB\t2\t0.87\n";
        await runtime.ViewModel.PasteCommand.ExecuteAsync(null);
        var columns = runtime.ViewModel.SelectedWorksheetColumns!;
        runtime.ViewModel.SelectColumn(columns[0].Id);
        runtime.ViewModel.ExtendColumnSelection(columns[1].Id);
        var gridRows = runtime.ViewModel.GridRows;
        var gridColumns = runtime.ViewModel.GridColumns;
        var gridLoad = runtime.ViewModel.GridLoadTask;
        var activeColumn = runtime.ViewModel.ActiveColumn;
        var selectedColumns = runtime.ViewModel.SelectedColumns;

        await runtime.RenameAsync(runtime.ItemNamed("Sheet1"), "WAT_Lot_A");

        Assert.Same(gridLoad, runtime.ViewModel.GridLoadTask);
        Assert.Same(gridRows, runtime.ViewModel.GridRows);
        Assert.Same(gridColumns, runtime.ViewModel.GridColumns);
        Assert.Same(columns, runtime.ViewModel.SelectedWorksheetColumns);
        Assert.Same(activeColumn, runtime.ViewModel.ActiveColumn);
        Assert.Same(selectedColumns, runtime.ViewModel.SelectedColumns);
        Assert.Equal("2 rows · 3 columns", runtime.ViewModel.SelectedWorksheetSummary);
    }

    [Fact]
    public async Task RenamingAnotherWorksheetKeepsTheActiveWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.NewWorksheetAsync();
        var sheet2 = runtime.ViewModel.SelectedWorksheet;

        await runtime.RenameAsync(runtime.ItemNamed("Sheet1"), "WAT_Lot_A");

        Assert.Same(sheet2, runtime.ViewModel.SelectedWorksheet);
        Assert.Equal(["WAT_Lot_A", "Sheet2"], runtime.ViewModel.Worksheets.Select(worksheet => worksheet.Name));
        Assert.Equal(["WAT_Lot_A", "Sheet2"], runtime.Explorer.WorksheetItems.Select(item => item.Name));
        Assert.True(runtime.ItemNamed("Sheet2").IsActive);
    }

    [Fact]
    public async Task EnterRenamesTheProjectThroughTheSession()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var projectItem = runtime.ProjectItem;
        var original = runtime.ViewModel.CurrentProject!;
        var sheet1Item = runtime.ItemNamed("Sheet1");

        var renamed = await runtime.RenameAsync(projectItem, " ALS_2026_09 ");

        Assert.True(renamed);
        Assert.False(projectItem.IsEditing);
        Assert.Same(projectItem, runtime.ProjectItem);
        Assert.Equal("ALS_2026_09", projectItem.Name);
        Assert.Equal("ALS_2026_09", runtime.ViewModel.CurrentProject!.Name);
        Assert.Same(projectItem.Project, runtime.ViewModel.CurrentProject);
        Assert.Equal(original.Id, runtime.ViewModel.CurrentProject.Id);
        Assert.NotSame(original, runtime.ViewModel.CurrentProject);
        Assert.Equal("Untitled Project", original.Name);

        // The tree is updated in place: worksheets, expansion and selection stay.
        Assert.Same(sheet1Item, Assert.Single(runtime.Explorer.WorksheetItems));
        Assert.Same(sheet1Item, runtime.Explorer.SelectedItem);
        Assert.True(projectItem.IsExpanded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task EnterWithABlankWorksheetNameStaysInEditModeAndShowsTheError(string name)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");

        var renamed = await runtime.RenameAsync(sheet1, name);

        Assert.False(renamed);
        Assert.True(sheet1.IsEditing);
        Assert.Equal(name, sheet1.EditName);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Equal("Worksheet name must not be empty.", runtime.ViewModel.ErrorMessage);
        Assert.Equal("Sheet1", (await runtime.StoredWorksheetAsync(sheet1.WorksheetId)).Name);
    }

    [Fact]
    public async Task EnterWithABlankProjectNameStaysInEditModeAndShowsTheError()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        var renamed = await runtime.RenameAsync(runtime.ProjectItem, "  ");

        Assert.False(renamed);
        Assert.True(runtime.ProjectItem.IsEditing);
        Assert.Equal("Untitled Project", runtime.ProjectItem.Name);
        Assert.Equal("Untitled Project", runtime.ViewModel.CurrentProject!.Name);
        Assert.Equal("Project name must not be empty.", runtime.ViewModel.ErrorMessage);
    }

    [Theory]
    [InlineData("Sheet2")]
    [InlineData("sheet2")]
    [InlineData("SHEET2")]
    public async Task EnterWithADuplicateWorksheetNameStaysInEditModeAndShowsTheError(string name)
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.NewWorksheetAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");

        var renamed = await runtime.RenameAsync(sheet1, name);

        Assert.False(renamed);
        Assert.True(sheet1.IsEditing);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Equal($"A worksheet named '{name}' already exists in this project.", runtime.ViewModel.ErrorMessage);
    }

    [Fact]
    public async Task ACorrectedNameAfterARejectedEnterIsStored()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        await runtime.RenameAsync(sheet1, "");

        sheet1.EditName = "WAT_Lot_A";
        var renamed = await runtime.Explorer.CommitRenameAsync(sheet1);

        Assert.True(renamed);
        Assert.False(sheet1.IsEditing);
        Assert.Equal("WAT_Lot_A", sheet1.Name);
        Assert.Null(runtime.ViewModel.ErrorMessage);
    }

    [Fact]
    public async Task FocusLossCommitsAValidName()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        runtime.Explorer.BeginRename(sheet1);
        sheet1.EditName = "WAT_Lot_A";

        var renamed = await runtime.Explorer.CommitOrCancelRenameAsync(sheet1);

        Assert.True(renamed);
        Assert.False(sheet1.IsEditing);
        Assert.Equal("WAT_Lot_A", (await runtime.StoredWorksheetAsync(sheet1.WorksheetId)).Name);
    }

    [Fact]
    public async Task FocusLossWithAnInvalidNameRevertsAndKeepsTheError()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.NewWorksheetAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        runtime.Explorer.BeginRename(sheet1);
        sheet1.EditName = "SHEET2";

        var renamed = await runtime.Explorer.CommitOrCancelRenameAsync(sheet1);

        Assert.False(renamed);
        Assert.False(sheet1.IsEditing);
        Assert.Equal("Sheet1", sheet1.EditName);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Equal("A worksheet named 'SHEET2' already exists in this project.", runtime.ViewModel.ErrorMessage);
    }

    [Fact]
    public async Task EscapeCancelsAndRestoresTheName()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        var worksheet = sheet1.Worksheet;
        runtime.Explorer.BeginRename(sheet1);
        sheet1.EditName = "WAT_Lot_A";

        runtime.Explorer.CancelRename(sheet1);

        Assert.False(sheet1.IsEditing);
        Assert.Equal("Sheet1", sheet1.EditName);
        Assert.Same(worksheet, sheet1.Worksheet);
        Assert.Same(worksheet, await runtime.StoredWorksheetAsync(worksheet.Id));

        // A later focus loss of the hidden editor stores nothing.
        Assert.False(await runtime.Explorer.CommitOrCancelRenameAsync(sheet1));
        Assert.Equal("Sheet1", (await runtime.StoredWorksheetAsync(worksheet.Id)).Name);
    }

    [Fact]
    public async Task CasingOnlyRenameIsStored()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");

        var renamed = await runtime.RenameAsync(sheet1, "SHEET1");

        Assert.True(renamed);
        Assert.Equal("SHEET1", sheet1.Name);
        Assert.Equal("SHEET1", (await runtime.StoredWorksheetAsync(sheet1.WorksheetId)).Name);
    }

    [Fact]
    public async Task AnUnchangedNameEndsEditingWithoutARename()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        var worksheet = sheet1.Worksheet;

        var renamed = await runtime.RenameAsync(sheet1, " Sheet1 ");

        Assert.False(renamed);
        Assert.False(sheet1.IsEditing);
        Assert.Same(worksheet, await runtime.StoredWorksheetAsync(worksheet.Id));
        Assert.Null(runtime.ViewModel.ErrorMessage);
    }

    [Fact]
    public async Task RenameIsNotStoredWhileAnotherOperationIsRunning()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        var clipboardGate = new TaskCompletionSource();
        runtime.Clipboard.Gate = clipboardGate.Task;
        runtime.Clipboard.Text = "Lot\nA\n";
        var paste = runtime.ViewModel.PasteCommand.ExecuteAsync(null);

        var renamed = await runtime.RenameAsync(sheet1, "WAT_Lot_A");

        Assert.False(renamed);
        Assert.True(sheet1.IsEditing);
        Assert.Equal("Sheet1", (await runtime.StoredWorksheetAsync(sheet1.WorksheetId)).Name);

        clipboardGate.SetResult();
        await paste;
        Assert.True(await runtime.Explorer.CommitRenameAsync(sheet1));
        Assert.Equal("WAT_Lot_A", sheet1.Name);
    }

    [Fact]
    public async Task NewWorksheetNamingUsesRenamedNames()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.NewWorksheetAsync();
        await runtime.RenameAsync(runtime.ItemNamed("Sheet2"), "Sheet9");

        await runtime.NewWorksheetAsync();

        Assert.Equal(["Sheet1", "Sheet9", "Sheet10"], runtime.Explorer.WorksheetItems.Select(item => item.Name));
    }

    [Fact]
    public async Task CreatingADuplicateWorksheetNameIsRejected()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        runtime.ViewModel.WorksheetName = "sheet1";
        await runtime.ViewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.Equal("A worksheet named 'sheet1' already exists in this project.", runtime.ViewModel.ErrorMessage);
        Assert.Single(runtime.ViewModel.Worksheets);
        Assert.Single(runtime.Explorer.WorksheetItems);
    }

    [Fact]
    public async Task RenamedItemsRaiseNameChanges()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ItemNamed("Sheet1");
        var changed = new List<string?>();
        ((INotifyPropertyChanged)sheet1).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        await runtime.RenameAsync(sheet1, "WAT_Lot_A");

        Assert.Contains(nameof(ProjectExplorerItem.Name), changed);
        Assert.Contains(nameof(ProjectExplorerItem.IsEditing), changed);
    }
}
