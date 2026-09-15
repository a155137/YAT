using System.Collections;
using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;

namespace YAT.App.Tests;

// Project Explorer tree: the current Project as the root item, its worksheets below it, and worksheet navigation.
public class ProjectExplorerTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

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

        public ProjectExplorerProjectItem ProjectItem => Assert.IsType<ProjectExplorerProjectItem>(Assert.Single(Explorer.Items));

        public ProjectExplorerWorksheetItem[] WorksheetItems =>
            ProjectItem.Children.Select(child => Assert.IsType<ProjectExplorerWorksheetItem>(child)).ToArray();

        public async Task StartAsync()
        {
            await ViewModel.CreateDefaultWorkspaceAsync();
            await ViewModel.GridLoadTask;
        }

        public async Task<Worksheet> NewWorksheetAsync()
        {
            await ViewModel.NewWorksheetCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
            return ViewModel.SelectedWorksheet!;
        }

        public async Task<Worksheet> CreateWorksheetAsync(string name)
        {
            ViewModel.WorksheetName = name;
            await ViewModel.CreateWorksheetCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
            return ViewModel.SelectedWorksheet!;
        }

        public async Task SelectItemAsync(ProjectExplorerItem item)
        {
            Explorer.SelectedItem = item;
            await ViewModel.GridLoadTask;
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
        }

        public ProjectExplorerWorksheetItem ItemFor(Worksheet worksheet) =>
            Assert.Single(WorksheetItems, item => item.WorksheetId == worksheet.Id);

        public void Dispose() => ProjectSession.Dispose();
    }

    [Fact]
    public void TreeIsEmptyWithoutAProject()
    {
        using var runtime = new Runtime();

        Assert.Empty(runtime.Explorer.Items);
        Assert.Null(runtime.Explorer.ProjectItem);
        Assert.Null(runtime.Explorer.SelectedItem);
        Assert.Empty(runtime.Explorer.WorksheetItems);
        Assert.False(runtime.ViewModel.NewWorksheetCommand.CanExecute(null));
    }

    [Fact]
    public async Task DefaultWorkspaceShowsUntitledProjectWithActiveSheet1()
    {
        using var runtime = new Runtime();

        await runtime.StartAsync();

        var projectItem = runtime.ProjectItem;
        Assert.Same(runtime.ViewModel.CurrentProject, projectItem.Project);
        Assert.Equal("Untitled Project", projectItem.Name);
        Assert.True(projectItem.IsExpanded);
        Assert.Same(projectItem, runtime.Explorer.ProjectItem);

        var sheet1 = Assert.Single(runtime.WorksheetItems);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Same(runtime.ViewModel.SelectedWorksheet, sheet1.Worksheet);
        Assert.True(sheet1.IsActive);
        Assert.Same(sheet1, runtime.Explorer.SelectedItem);
    }

    [Fact]
    public async Task ProjectContainsWorksheetsInCreationOrder()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.NewWorksheetAsync();
        await runtime.CreateWorksheetAsync("WAT_Lot_A");
        await runtime.NewWorksheetAsync();

        Assert.Equal(["Sheet1", "Sheet2", "WAT_Lot_A", "Sheet3"], runtime.WorksheetItems.Select(item => item.Name));
        Assert.Equal(runtime.ViewModel.Worksheets.Select(worksheet => worksheet.Id), runtime.WorksheetItems.Select(item => item.WorksheetId));
        Assert.All(runtime.WorksheetItems, item => Assert.Equal(runtime.ViewModel.CurrentProject!.Id, item.Worksheet.ProjectId));
    }

    [Fact]
    public async Task SelectingAWorksheetItemMakesItTheActiveWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        var sheet2 = await runtime.NewWorksheetAsync();

        await runtime.SelectItemAsync(runtime.ItemFor(sheet1));

        Assert.Same(sheet1, runtime.ViewModel.SelectedWorksheet);
        Assert.True(runtime.ItemFor(sheet1).IsActive);
        Assert.False(runtime.ItemFor(sheet2).IsActive);
        Assert.Same(runtime.ItemFor(sheet1), runtime.Explorer.SelectedItem);
        Assert.Single(runtime.WorksheetItems, item => item.IsActive);
    }

    [Fact]
    public async Task SwitchingWorksheetsShowsTheSelectedWorksheetsData()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        await runtime.PasteAsync("Lot\tYield\nA\t0.91\nB\t0.87\n");
        var sheet2 = await runtime.NewWorksheetAsync();
        await runtime.PasteAsync("Wafer\n1\n2\n3\n");

        await runtime.SelectItemAsync(runtime.ItemFor(sheet1));

        Assert.Equal(["Lot", "Yield"], runtime.ViewModel.GridColumns.Select(column => column.Name));
        Assert.Equal(2, runtime.ViewModel.TotalRowCount);

        await runtime.SelectItemAsync(runtime.ItemFor(sheet2));

        Assert.Equal(["Wafer"], runtime.ViewModel.GridColumns.Select(column => column.Name));
        Assert.Equal(3, runtime.ViewModel.TotalRowCount);
    }

    [Fact]
    public async Task ChangingTheSelectedWorksheetUpdatesTheTree()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        var sheet2 = await runtime.NewWorksheetAsync();

        runtime.ViewModel.SelectedWorksheet = sheet1;

        Assert.Same(runtime.ItemFor(sheet1), runtime.Explorer.SelectedItem);
        Assert.True(runtime.ItemFor(sheet1).IsActive);
        Assert.False(runtime.ItemFor(sheet2).IsActive);

        runtime.ViewModel.SelectedWorksheet = null;

        Assert.Null(runtime.Explorer.SelectedItem);
        Assert.DoesNotContain(runtime.WorksheetItems, item => item.IsActive);
    }

    [Fact]
    public async Task SelectingTheProjectItemKeepsTheActiveWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;

        await runtime.SelectItemAsync(runtime.ProjectItem);

        Assert.Same(runtime.ProjectItem, runtime.Explorer.SelectedItem);
        Assert.Same(sheet1, runtime.ViewModel.SelectedWorksheet);
        Assert.True(runtime.ItemFor(sheet1).IsActive);
    }

    [Fact]
    public async Task ClearingTheTreeSelectionKeepsTheActiveWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;

        runtime.Explorer.SelectedItem = null;

        Assert.Same(sheet1, runtime.ViewModel.SelectedWorksheet);
        Assert.True(runtime.ItemFor(sheet1).IsActive);
    }

    [Fact]
    public async Task CollapsingAndExpandingTheProjectKeepsWorksheetsAndSelection()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet2 = await runtime.NewWorksheetAsync();
        var items = runtime.WorksheetItems;

        runtime.ProjectItem.IsExpanded = false;

        Assert.False(runtime.ProjectItem.IsExpanded);
        Assert.Equal(items, runtime.WorksheetItems);
        Assert.Same(sheet2, runtime.ViewModel.SelectedWorksheet);

        runtime.ProjectItem.IsExpanded = true;

        Assert.True(runtime.ProjectItem.IsExpanded);
        Assert.Equal(items, runtime.WorksheetItems);
        Assert.Same(runtime.ItemFor(sheet2), runtime.Explorer.SelectedItem);
    }

    [Fact]
    public async Task NewWorksheetUsesTheNextDefaultNameAndSelectsIt()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        runtime.ProjectItem.IsExpanded = false;

        var sheet2 = await runtime.NewWorksheetAsync();

        Assert.Equal("Sheet2", sheet2.Name);
        Assert.Same(runtime.ItemFor(sheet2), runtime.Explorer.SelectedItem);
        Assert.True(runtime.ItemFor(sheet2).IsActive);
        Assert.True(runtime.ProjectItem.IsExpanded);
        Assert.Null(runtime.ViewModel.ErrorMessage);
        Assert.Same(await runtime.ProjectSession.Worksheets.GetByIdAsync(sheet2.Id, TestContext.Current.CancellationToken), sheet2);
    }

    [Fact]
    public async Task NewWorksheetNamesFollowTheHighestSheetNumber()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        await runtime.CreateWorksheetAsync("Sheet7");
        await runtime.CreateWorksheetAsync("Sheet");
        await runtime.CreateWorksheetAsync("Sheet-9");

        var next = await runtime.NewWorksheetAsync();

        Assert.Equal("Sheet8", next.Name);
    }

    [Fact]
    public async Task ProjectItemContextMenuCommandIsNewWorksheet()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        Assert.Same(runtime.ViewModel.NewWorksheetCommand, runtime.ProjectItem.NewWorksheetCommand);
    }

    [Fact]
    public async Task CreatingAProjectReplacesTheTree()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var untitledItem = runtime.ProjectItem;

        runtime.ViewModel.ProjectName = "ALS_2026_09";
        await runtime.ViewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.NotSame(untitledItem, runtime.ProjectItem);
        Assert.Equal("ALS_2026_09", runtime.ProjectItem.Name);
        Assert.Empty(runtime.ProjectItem.Children);
        Assert.Null(runtime.Explorer.SelectedItem);

        var sheet1 = await runtime.NewWorksheetAsync();

        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Same(runtime.ItemFor(sheet1), runtime.Explorer.SelectedItem);
    }

    [Fact]
    public async Task PasteCopyAndDeleteKeepTheTreeItemsAndSelectedIdentity()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();
        var sheet1 = runtime.ViewModel.SelectedWorksheet!;
        var projectItem = runtime.ProjectItem;
        var sheet1Item = runtime.ItemFor(sheet1);

        await runtime.PasteAsync("No\tBin\n1\t1\n2\t2\n");
        runtime.ViewModel.SelectColumn(runtime.ViewModel.SelectedWorksheetColumns![0].Id);
        await runtime.ViewModel.CopySelectedColumnsCommand.ExecuteAsync(null);
        await runtime.ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);

        Assert.Same(projectItem, runtime.ProjectItem);
        Assert.Same(sheet1Item, Assert.Single(runtime.WorksheetItems));
        Assert.Same(sheet1Item, runtime.Explorer.SelectedItem);
        Assert.Equal(sheet1.Id, sheet1Item.WorksheetId);
        Assert.Same(sheet1, runtime.ViewModel.SelectedWorksheet);
    }

    // Tree items are navigation metadata: apart from the project's child items, they expose no collections, so worksheet
    // values have nowhere to live in the tree.
    [Theory]
    [InlineData(typeof(ProjectExplorerItem))]
    [InlineData(typeof(ProjectExplorerProjectItem))]
    [InlineData(typeof(ProjectExplorerWorksheetItem))]
    [InlineData(typeof(ProjectExplorerViewModel))]
    public void TreeTypesExposeNoValueCollections(Type type)
    {
        var collectionProperties = type.GetProperties()
            .Where(property => property.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType))
            .Where(property => !typeof(IEnumerable<ProjectExplorerItem>).IsAssignableFrom(property.PropertyType))
            .Select(property => property.Name);

        Assert.Empty(collectionProperties);
    }

    [Fact]
    public async Task WorksheetItemsHoldOnlyWorksheetMetadata()
    {
        using var runtime = new Runtime();
        await runtime.StartAsync();

        await runtime.PasteAsync("No\n1\n2\n3\n");

        Assert.Equal(
            [nameof(ProjectExplorerItem.EditName), nameof(ProjectExplorerWorksheetItem.IsActive), nameof(ProjectExplorerItem.IsEditing),
             nameof(ProjectExplorerItem.IsExpanded), nameof(ProjectExplorerItem.Name), nameof(ProjectExplorerWorksheetItem.Worksheet),
             nameof(ProjectExplorerWorksheetItem.WorksheetId)],
            typeof(ProjectExplorerWorksheetItem).GetProperties().Select(property => property.Name).Order());
        Assert.Same(runtime.ViewModel.SelectedWorksheet, Assert.Single(runtime.WorksheetItems).Worksheet);
    }
}
