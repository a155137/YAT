using YAT.App.Tests.TestDoubles;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

public class WorksheetColumnWorkflowTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    // Built through the real CompositionRoot: real Application handlers over a ProjectSession's in-memory repositories.
    private static MainWindowViewModel CreateViewModel()
    {
        var compositionRoot = new CompositionRoot(new FixedTimeProvider(Now));
        var projectSession = compositionRoot.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        return compositionRoot.CreateMainWindowViewModel(
            compositionRoot.CreateMainWindowSession(projectSession, clipboard, clipboard));
    }

    private static async Task<MainWindowViewModel> CreateViewModelWithWorksheetAsync(string worksheetName = "WAT_Lot_A")
    {
        var viewModel = CreateViewModel();
        viewModel.ProjectName = "ALS_2026_09";
        await viewModel.CreateProjectCommand.ExecuteAsync(null);
        await CreateWorksheetAsync(viewModel, worksheetName);
        return viewModel;
    }

    private static async Task<Worksheet> CreateWorksheetAsync(MainWindowViewModel viewModel, string name)
    {
        viewModel.WorksheetName = name;
        await viewModel.CreateWorksheetCommand.ExecuteAsync(null);

        // Selecting the new Worksheet starts its grid page load; let it finish so later commands start from idle.
        await viewModel.GridLoadTask;
        return viewModel.Worksheets.Single(w => w.Name == name);
    }

    private static async Task AddColumnAsync(
        MainWindowViewModel viewModel,
        string name,
        WorksheetDataType dataType = WorksheetDataType.Numeric,
        ColumnSemanticType? semanticType = null,
        string unit = "")
    {
        viewModel.ColumnName = name;
        viewModel.SelectedColumnDataType = dataType;
        viewModel.SelectedColumnSemanticType = semanticType;
        viewModel.ColumnUnit = unit;
        await viewModel.AddColumnCommand.ExecuteAsync(null);
    }

    [Fact]
    public void ColumnInputsHaveDefaults()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(string.Empty, viewModel.ColumnName);
        Assert.Equal(WorksheetDataType.Numeric, viewModel.SelectedColumnDataType);
        Assert.Null(viewModel.SelectedColumnSemanticType);
        Assert.Equal(string.Empty, viewModel.ColumnUnit);
        Assert.Null(viewModel.SelectedWorksheetColumns);
    }

    [Fact]
    public void SelectorOptionsMirrorDomainEnums()
    {
        var viewModel = CreateViewModel();

        Assert.Equal(Enum.GetValues<WorksheetDataType>(), viewModel.DataTypeOptions);
        Assert.Equal(
            [null, .. Enum.GetValues<ColumnSemanticType>().Cast<ColumnSemanticType?>()],
            viewModel.SemanticTypeOptions.Select(option => option.Value));
        Assert.Equal("None", viewModel.SemanticTypeOptions[0].Label);
    }

    [Fact]
    public void NoneSemanticTypeOptionMapsToNull()
    {
        var viewModel = CreateViewModel();

        viewModel.SelectedSemanticTypeOption = viewModel.SemanticTypeOptions.Single(o => o.Value == ColumnSemanticType.Wafer);
        Assert.Equal(ColumnSemanticType.Wafer, viewModel.SelectedColumnSemanticType);

        viewModel.SelectedSemanticTypeOption = viewModel.SemanticTypeOptions[0];
        Assert.Null(viewModel.SelectedColumnSemanticType);
        Assert.Equal("None", viewModel.SelectedSemanticTypeOption.Label);
    }

    [Fact]
    public async Task AddColumnCannotExecuteWithoutSelectedWorksheet()
    {
        var viewModel = CreateViewModel();
        viewModel.ProjectName = "ALS_2026_09";
        await viewModel.CreateProjectCommand.ExecuteAsync(null);
        viewModel.ColumnName = "Vth";

        Assert.False(viewModel.AddColumnCommand.CanExecute(null));

        await viewModel.AddColumnCommand.ExecuteAsync(null);

        Assert.NotNull(viewModel.ErrorMessage);
        Assert.Null(viewModel.SelectedWorksheetColumns);
    }

    [Fact]
    public async Task SelectingAWorksheetEnablesAddColumn()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();
        var worksheet = Assert.Single(viewModel.Worksheets);

        viewModel.SelectedWorksheet = null;
        Assert.False(viewModel.AddColumnCommand.CanExecute(null));

        viewModel.SelectedWorksheet = worksheet;
        Assert.True(viewModel.AddColumnCommand.CanExecute(null));
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Empty(viewModel.SelectedWorksheetColumns);
    }

    [Fact]
    public async Task ColumnsGetSequentialIndices()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();

        await AddColumnAsync(viewModel, "Vth");
        await AddColumnAsync(viewModel, "Wafer", WorksheetDataType.String);
        await AddColumnAsync(viewModel, "Temperature");

        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Equal([0, 1, 2], viewModel.SelectedWorksheetColumns.Select(c => c.Index));
    }

    [Fact]
    public async Task CreatedColumnPreservesMetadataAndBelongsToSelectedWorksheet()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();
        var worksheet = viewModel.SelectedWorksheet;

        await AddColumnAsync(viewModel, "  Vth  ", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "V");

        Assert.NotNull(worksheet);
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        var column = Assert.Single(viewModel.SelectedWorksheetColumns);
        Assert.NotEqual(Guid.Empty, column.Id);
        Assert.Equal(worksheet.Id, column.WorksheetId);
        Assert.Equal("Vth", column.Name);
        Assert.Equal(WorksheetDataType.Numeric, column.DataType);
        Assert.Equal(ColumnSemanticType.TestParameter, column.SemanticType);
        Assert.Equal("V", column.Unit);
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task NonNumericDataTypeAndNullSemanticTypeArePreserved()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();

        await AddColumnAsync(viewModel, "Comment", WorksheetDataType.String, semanticType: null);

        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        var column = Assert.Single(viewModel.SelectedWorksheetColumns);
        Assert.Equal(WorksheetDataType.String, column.DataType);
        Assert.Null(column.SemanticType);
    }

    [Fact]
    public async Task BlankUnitMapsToNull()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();

        await AddColumnAsync(viewModel, "Wafer", WorksheetDataType.String, ColumnSemanticType.Wafer, "   ");

        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Null(Assert.Single(viewModel.SelectedWorksheetColumns).Unit);
    }

    [Fact]
    public async Task InputsResetAfterSuccessfulAddButDataTypeIsKept()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();

        await AddColumnAsync(viewModel, "Wafer", WorksheetDataType.String, ColumnSemanticType.Wafer, "ea");

        Assert.Equal(string.Empty, viewModel.ColumnName);
        Assert.Equal(string.Empty, viewModel.ColumnUnit);
        Assert.Null(viewModel.SelectedColumnSemanticType);
        Assert.Equal(WorksheetDataType.String, viewModel.SelectedColumnDataType);
    }

    [Fact]
    public async Task BlankColumnNameSurfacesErrorAndAddsNothing()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();

        await AddColumnAsync(viewModel, "   ", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "V");

        Assert.Equal("Column name must not be empty.", viewModel.ErrorMessage);
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Empty(viewModel.SelectedWorksheetColumns);
        Assert.False(viewModel.IsBusy);
        Assert.Equal(ColumnSemanticType.TestParameter, viewModel.SelectedColumnSemanticType);
        Assert.Equal("V", viewModel.ColumnUnit);
    }

    [Fact]
    public async Task FailedAddDoesNotAdvanceIndex()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();

        await AddColumnAsync(viewModel, "Vth");
        await AddColumnAsync(viewModel, "");
        await AddColumnAsync(viewModel, "Idsat");

        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Equal([0, 1], viewModel.SelectedWorksheetColumns.Select(c => c.Index));
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task NewWorksheetStartsWithNoColumns()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync("WAT_Lot_A");
        await AddColumnAsync(viewModel, "Vth");

        await CreateWorksheetAsync(viewModel, "WAT_Lot_B");

        Assert.Equal("WAT_Lot_B", viewModel.SelectedWorksheet?.Name);
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Empty(viewModel.SelectedWorksheetColumns);
    }

    [Fact]
    public async Task ColumnsAreKeptPerWorksheetWhenSwitchingSelection()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync("WAT_Lot_A");
        var worksheetA = viewModel.SelectedWorksheet;
        await AddColumnAsync(viewModel, "Vth", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "V");
        await AddColumnAsync(viewModel, "Wafer", WorksheetDataType.String, ColumnSemanticType.Wafer);

        var worksheetB = await CreateWorksheetAsync(viewModel, "WAT_Lot_B");
        await AddColumnAsync(viewModel, "Ioff", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "pA");

        viewModel.SelectedWorksheet = worksheetA;
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Equal(["Vth", "Wafer"], viewModel.SelectedWorksheetColumns.Select(c => c.Name));
        Assert.All(viewModel.SelectedWorksheetColumns, c => Assert.Equal(worksheetA!.Id, c.WorksheetId));

        viewModel.SelectedWorksheet = worksheetB;
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        var onlyB = Assert.Single(viewModel.SelectedWorksheetColumns);
        Assert.Equal("Ioff", onlyB.Name);
        Assert.Equal(0, onlyB.Index);
        Assert.Equal(worksheetB.Id, onlyB.WorksheetId);

        viewModel.SelectedWorksheet = worksheetA;
        Assert.Equal(["Vth", "Wafer"], viewModel.SelectedWorksheetColumns.Select(c => c.Name));
    }

    [Fact]
    public async Task AddingColumnDoesNotMutateWorksheetColumnCount()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();

        await AddColumnAsync(viewModel, "Vth");
        await AddColumnAsync(viewModel, "Idsat");

        Assert.NotNull(viewModel.SelectedWorksheet);
        Assert.Equal(0, viewModel.SelectedWorksheet.ColumnCount);
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Equal(2, viewModel.SelectedWorksheetColumns.Count);
    }

    [Fact]
    public async Task SecondProjectClearsSelectionAndColumnSessionState()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();
        var oldWorksheet = viewModel.SelectedWorksheet;
        await AddColumnAsync(viewModel, "Vth");

        viewModel.ProjectName = "ALS_2026_10";
        await viewModel.CreateProjectCommand.ExecuteAsync(null);

        Assert.Null(viewModel.SelectedWorksheet);
        Assert.Null(viewModel.SelectedWorksheetColumns);
        Assert.False(viewModel.AddColumnCommand.CanExecute(null));

        // Re-selecting the old Worksheet object must not resurrect its previous column session state.
        viewModel.SelectedWorksheet = oldWorksheet;
        Assert.NotNull(viewModel.SelectedWorksheetColumns);
        Assert.Empty(viewModel.SelectedWorksheetColumns);
    }

    [Fact]
    public async Task HidingProjectPanelKeepsSelectedWorksheetAndColumns()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();
        var worksheet = viewModel.SelectedWorksheet;
        await AddColumnAsync(viewModel, "Vth");
        var columns = viewModel.SelectedWorksheetColumns;

        viewModel.ToggleProjectPanelCommand.Execute(null);

        Assert.False(viewModel.IsProjectPanelVisible);
        Assert.Same(worksheet, viewModel.SelectedWorksheet);
        Assert.Same(columns, viewModel.SelectedWorksheetColumns);
        Assert.NotNull(columns);
        Assert.Single(columns);

        viewModel.ToggleProjectPanelCommand.Execute(null);

        Assert.Same(worksheet, viewModel.SelectedWorksheet);
        Assert.Same(columns, viewModel.SelectedWorksheetColumns);
    }

    [Fact]
    public async Task SelectingAnotherWorksheetClearsStaleErrorMessage()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync("WAT_Lot_A");
        var worksheetA = viewModel.SelectedWorksheet;
        var worksheetB = await CreateWorksheetAsync(viewModel, "WAT_Lot_B");
        viewModel.SelectedWorksheet = worksheetA;

        await AddColumnAsync(viewModel, "   ");
        Assert.NotNull(viewModel.ErrorMessage);

        viewModel.SelectedWorksheet = worksheetB;

        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public async Task SummaryUsesSingularAndPluralColumnCounts()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync("WAT_Lot_A");
        var worksheetA = viewModel.SelectedWorksheet;
        var notifications = 0;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.SelectedWorksheetSummary))
            {
                notifications++;
            }
        };

        Assert.Equal("0 rows · 0 columns", viewModel.SelectedWorksheetSummary);

        await AddColumnAsync(viewModel, "Vth");
        Assert.Equal("0 rows · 1 column", viewModel.SelectedWorksheetSummary);

        await AddColumnAsync(viewModel, "Wafer");
        Assert.Equal("0 rows · 2 columns", viewModel.SelectedWorksheetSummary);
        Assert.Equal(2, notifications);

        await CreateWorksheetAsync(viewModel, "WAT_Lot_B");
        Assert.Equal("0 rows · 0 columns", viewModel.SelectedWorksheetSummary);

        viewModel.SelectedWorksheet = worksheetA;
        Assert.Equal("0 rows · 2 columns", viewModel.SelectedWorksheetSummary);

        viewModel.SelectedWorksheet = null;
        Assert.Null(viewModel.SelectedWorksheetSummary);
    }

    [Fact]
    public async Task CommandsAreDisabledWhileAddingColumn()
    {
        var viewModel = await CreateViewModelWithWorksheetAsync();
        var observed = new List<(bool IsBusy, bool CanAddColumn, bool CanCreateProject, bool CanCreateWorksheet)>();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsBusy))
            {
                observed.Add((
                    viewModel.IsBusy,
                    viewModel.AddColumnCommand.CanExecute(null),
                    viewModel.CreateProjectCommand.CanExecute(null),
                    viewModel.CreateWorksheetCommand.CanExecute(null)));
            }
        };

        await AddColumnAsync(viewModel, "Vth");

        Assert.Equal([(true, false, false, false), (false, true, true, true)], observed);
    }
}
