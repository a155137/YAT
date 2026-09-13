using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.App.Tests;

public class MainWindowViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    // Built through the real CompositionRoot: real Application handlers over a ProjectSession's in-memory repositories.
    private static MainWindowViewModel CreateViewModel()
    {
        var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
        var projectSession = compositionRoot.CreateProjectSession(":memory:");
        return compositionRoot.CreateMainWindowViewModel(
            compositionRoot.CreateMainWindowSession(projectSession, new FakeClipboardTextReader()));
    }

    private static async Task<MainWindowViewModel> CreateViewModelWithProjectAsync(string name = "ALS_2026_09")
    {
        var viewModel = CreateViewModel();
        viewModel.ProjectName = name;
        await viewModel.CreateProjectCommand.ExecuteAsync(null);
        return viewModel;
    }

    [Fact]
    public void StartsWithNoProjectAndNoWorksheets()
    {
        var viewModel = CreateViewModel();

        Assert.Null(viewModel.CurrentProject);
        Assert.False(viewModel.HasCurrentProject);
        Assert.Empty(viewModel.Worksheets);
        Assert.Null(viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ValidProjectCreationSetsCurrentProject()
    {
        var viewModel = CreateViewModel();
        viewModel.ProjectName = "  ALS_2026_09  ";
        viewModel.ProjectDescription = "CP yield review";

        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        var project = viewModel.CurrentProject;
        Assert.NotNull(project);
        Assert.True(viewModel.HasCurrentProject);
        Assert.NotEqual(Guid.Empty, project.Id);
        Assert.Equal("ALS_2026_09", project.Name);
        Assert.Equal("CP yield review", project.Description);
        Assert.Equal(Now, project.CreatedAt);
        Assert.Equal(Now, project.UpdatedAt);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task BlankProjectDescriptionIsPassedAsNull()
    {
        var viewModel = CreateViewModel();
        viewModel.ProjectName = "ALS_2026_09";
        viewModel.ProjectDescription = "   ";

        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CurrentProject);
        Assert.Null(viewModel.CurrentProject.Description);
    }

    [Fact]
    public async Task ProjectInputsClearAfterSuccessfulCreation()
    {
        var viewModel = CreateViewModel();
        viewModel.ProjectName = "ALS_2026_09";
        viewModel.ProjectDescription = "CP yield review";

        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.ProjectName);
        Assert.Equal(string.Empty, viewModel.ProjectDescription);
    }

    [Fact]
    public async Task InvalidProjectNameSurfacesErrorMessage()
    {
        var viewModel = CreateViewModel();
        viewModel.ProjectName = "   ";

        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.Null(viewModel.CurrentProject);
        Assert.Equal("Project name must not be empty.", viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task FailedProjectCreationDoesNotReplaceCurrentProject()
    {
        var viewModel = await CreateViewModelWithProjectAsync();
        var original = viewModel.CurrentProject;
        viewModel.WorksheetName = "WAT_Lot_A";
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        viewModel.ProjectName = "";
        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.Same(original, viewModel.CurrentProject);
        Assert.Single(viewModel.Worksheets);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task SuccessfulActionClearsPreviousErrorMessage()
    {
        var viewModel = CreateViewModel();
        await viewModel.CreateProjectCommand.ExecuteAsync(null);
        Assert.NotNull(viewModel.ErrorMessage);

        viewModel.ProjectName = "ALS_2026_09";
        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CannotCreateWorksheetWithoutCurrentProject()
    {
        var viewModel = CreateViewModel();
        viewModel.WorksheetName = "WAT_Lot_A";

        Assert.False(viewModel.CreateWorksheetCommand.CanExecute(null));

        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Worksheets);
        Assert.NotNull(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task WorksheetCreationBecomesAvailableAfterProjectCreation()
    {
        var viewModel = await CreateViewModelWithProjectAsync();

        Assert.True(viewModel.CreateWorksheetCommand.CanExecute(null));
    }

    [Fact]
    public async Task CreatedWorksheetIsAddedToVisibleCollection()
    {
        var viewModel = await CreateViewModelWithProjectAsync();
        viewModel.WorksheetName = "WAT_Lot_A";

        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        var worksheet = Assert.Single(viewModel.Worksheets);
        Assert.Equal("WAT_Lot_A", worksheet.Name);
        Assert.Equal(0, worksheet.RowCount);
        Assert.Equal(0, worksheet.ColumnCount);
        Assert.Same(worksheet, viewModel.SelectedWorksheet);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task CreatedWorksheetBelongsToCurrentProject()
    {
        var viewModel = await CreateViewModelWithProjectAsync();
        viewModel.WorksheetName = "WAT_Lot_A";

        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CurrentProject);
        Assert.Equal(viewModel.CurrentProject.Id, Assert.Single(viewModel.Worksheets).ProjectId);
    }

    [Fact]
    public async Task MultipleWorksheetsCanBeCreatedUnderTheSameProject()
    {
        var viewModel = await CreateViewModelWithProjectAsync();

        viewModel.WorksheetName = "WAT_Lot_A";
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);
        viewModel.WorksheetName = "WAT_Lot_B";
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.Equal(["WAT_Lot_A", "WAT_Lot_B"], viewModel.Worksheets.Select(w => w.Name));
    }

    [Fact]
    public async Task WorksheetNameClearsAfterSuccessfulCreation()
    {
        var viewModel = await CreateViewModelWithProjectAsync();
        viewModel.WorksheetName = "WAT_Lot_A";

        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, viewModel.WorksheetName);
    }

    [Fact]
    public async Task InvalidWorksheetNameSurfacesErrorMessageAndAddsNothing()
    {
        var viewModel = await CreateViewModelWithProjectAsync();
        viewModel.WorksheetName = "   ";

        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.Empty(viewModel.Worksheets);
        Assert.Equal("Worksheet name must not be empty.", viewModel.ErrorMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task SecondProjectReplacesCurrentProjectAndClearsWorksheets()
    {
        var viewModel = await CreateViewModelWithProjectAsync("ALS_2026_09");
        var first = viewModel.CurrentProject;
        viewModel.WorksheetName = "WAT_Lot_A";
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        viewModel.ProjectName = "ALS_2026_10";
        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.CurrentProject);
        Assert.NotSame(first, viewModel.CurrentProject);
        Assert.Equal("ALS_2026_10", viewModel.CurrentProject.Name);
        Assert.Empty(viewModel.Worksheets);
        Assert.Null(viewModel.SelectedWorksheet);
    }

    [Fact]
    public void ProjectPanelIsVisibleByDefault()
    {
        Assert.True(CreateViewModel().IsProjectPanelVisible);
    }

    [Fact]
    public void ToggleProjectPanelHidesAndShowsThePanel()
    {
        var viewModel = CreateViewModel();

        viewModel.ToggleProjectPanelCommand.Execute(null);
        Assert.False(viewModel.IsProjectPanelVisible);

        viewModel.ToggleProjectPanelCommand.Execute(null);
        Assert.True(viewModel.IsProjectPanelVisible);
    }

    [Fact]
    public async Task HidingProjectPanelKeepsProjectAndWorksheets()
    {
        var viewModel = await CreateViewModelWithProjectAsync();
        var project = viewModel.CurrentProject;
        viewModel.WorksheetName = "WAT_Lot_A";
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);
        var worksheet = Assert.Single(viewModel.Worksheets);

        viewModel.ToggleProjectPanelCommand.Execute(null);

        Assert.False(viewModel.IsProjectPanelVisible);
        Assert.Same(project, viewModel.CurrentProject);
        Assert.Same(worksheet, Assert.Single(viewModel.Worksheets));
        Assert.Same(worksheet, viewModel.SelectedWorksheet);

        viewModel.ToggleProjectPanelCommand.Execute(null);

        Assert.True(viewModel.IsProjectPanelVisible);
        Assert.Same(project, viewModel.CurrentProject);
        Assert.Same(worksheet, Assert.Single(viewModel.Worksheets));
    }

    [Fact]
    public async Task CreateCommandsAreDisabledWhileBusy()
    {
        var viewModel = await CreateViewModelWithProjectAsync();
        var observed = new List<(bool IsBusy, bool CanCreateProject, bool CanCreateWorksheet)>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
            {
                observed.Add((
                    viewModel.IsBusy,
                    viewModel.CreateProjectCommand.CanExecute(null),
                    viewModel.CreateWorksheetCommand.CanExecute(null)));
            }
        };

        viewModel.WorksheetName = "WAT_Lot_A";
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        Assert.Equal([(true, false, false), (false, true, true)], observed);
    }
}
