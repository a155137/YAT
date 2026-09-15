using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// Persistence acceptance: a project edited through the real UI boundary (MainWindowViewModel → MainWindowSession →
// Application → DuckDB) is closed, every piece of infrastructure is disposed, and the .yat file is opened again by a new
// CompositionRoot and workspace. The reopened project must be functionally equivalent.
public class ProjectPersistenceRoundTripTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // One application run: its own CompositionRoot, workspace and UI objects. Disposing it closes the project.
    private sealed class AppRun : IDisposable
    {
        private readonly CompositionRoot _composition;

        public AppRun(string temporaryRoot)
        {
            _composition = new CompositionRoot(new FixedTimeProvider(Now), temporaryRoot);
            Workspace = _composition.CreateProjectWorkspace();
        }

        public ProjectWorkspace Workspace { get; }

        public FakeClipboard Clipboard { get; } = new();

        public MainWindowViewModel ViewModel { get; private set; } = null!;

        public ProjectSession Session => Workspace.CurrentSession!;

        public async Task ShowAsync(ProjectSession session)
        {
            ViewModel = _composition.CreateMainWindowViewModel(_composition.CreateMainWindowSession(session, Clipboard, Clipboard));
            await ViewModel.LoadProjectAsync();
            await ViewModel.GridLoadTask;
        }

        public ProjectExplorerWorksheetItem Item(string name) =>
            Assert.Single(ViewModel.ProjectExplorer.WorksheetItems, item => item.Name == name);

        public async Task RenameAsync(ProjectExplorerItem item, string name)
        {
            ViewModel.ProjectExplorer.BeginRename(item);
            item.EditName = name;
            Assert.True(await ViewModel.ProjectExplorer.CommitRenameAsync(item), ViewModel.ErrorMessage);
        }

        public async Task SelectAsync(string worksheetName)
        {
            ViewModel.ProjectExplorer.SelectedItem = Item(worksheetName);
            await ViewModel.GridLoadTask;
        }

        public async Task NewWorksheetAsync()
        {
            await ViewModel.NewWorksheetCommand.ExecuteAsync(null);
            await ViewModel.GridLoadTask;
        }

        public async Task PasteAsync(string text)
        {
            Clipboard.Text = text;
            await ViewModel.PasteCommand.ExecuteAsync(null);
            Assert.Null(ViewModel.ErrorMessage);
        }

        public void Dispose() => Workspace.Dispose();
    }

    // Everything stored for a project, read through a session's repositories and raw data store.
    private sealed record ProjectSnapshot(
        Project Project,
        IReadOnlyList<Worksheet> Worksheets,
        IReadOnlyDictionary<Guid, IReadOnlyList<WorksheetColumn>> Columns,
        IReadOnlyDictionary<Guid, long> RowCounts,
        IReadOnlyDictionary<Guid, IReadOnlySet<Guid>> StoredColumnIds,
        IReadOnlyDictionary<Guid, RawDataBlock> Values);

    private static async Task<ProjectSnapshot> SnapshotAsync(ProjectSession session)
    {
        var project = (await session.Projects.GetByIdAsync(session.ProjectId!.Value, Token))!;
        var worksheets = await session.Worksheets.GetByProjectIdAsync(project.Id, Token);
        var columns = new Dictionary<Guid, IReadOnlyList<WorksheetColumn>>();
        var rowCounts = new Dictionary<Guid, long>();
        var storedIds = new Dictionary<Guid, IReadOnlySet<Guid>>();
        var values = new Dictionary<Guid, RawDataBlock>();

        foreach (var worksheet in worksheets)
        {
            columns[worksheet.Id] = await session.WorksheetColumns.GetByWorksheetIdAsync(worksheet.Id, Token);
            rowCounts[worksheet.Id] = await session.RawDataStore.GetWorksheetRowCountAsync(worksheet.Id, Token);
            storedIds[worksheet.Id] = await session.RawDataStore.GetStoredColumnIdsAsync(worksheet.Id, Token);
            if (storedIds[worksheet.Id].Count > 0)
            {
                var ids = columns[worksheet.Id].Select(column => column.Id).Where(storedIds[worksheet.Id].Contains).ToArray();
                values[worksheet.Id] = await session.RawDataStore.ReadColumnsAsync(worksheet.Id, ids, 0, int.MaxValue, Token);
            }
        }

        return new ProjectSnapshot(project, worksheets, columns, rowCounts, storedIds, values);
    }

    private static void AssertEquivalent(ProjectSnapshot expected, ProjectSnapshot actual)
    {
        Assert.Equal(
            (expected.Project.Id, expected.Project.Name, expected.Project.Description, expected.Project.CreatedAt, expected.Project.UpdatedAt),
            (actual.Project.Id, actual.Project.Name, actual.Project.Description, actual.Project.CreatedAt, actual.Project.UpdatedAt));

        Assert.Equal(
            expected.Worksheets.Select(w => (w.Id, w.ProjectId, w.Name, w.RowCount, w.ColumnCount, w.CreatedAt, w.UpdatedAt)),
            actual.Worksheets.Select(w => (w.Id, w.ProjectId, w.Name, w.RowCount, w.ColumnCount, w.CreatedAt, w.UpdatedAt)));

        foreach (var worksheet in expected.Worksheets)
        {
            Assert.Equal(
                expected.Columns[worksheet.Id].Select(c => (c.Id, c.WorksheetId, c.Index, c.Name, c.DataType, c.SemanticType, c.Unit)),
                actual.Columns[worksheet.Id].Select(c => (c.Id, c.WorksheetId, c.Index, c.Name, c.DataType, c.SemanticType, c.Unit)));
            Assert.Equal(expected.RowCounts[worksheet.Id], actual.RowCounts[worksheet.Id]);
            Assert.Equal(expected.StoredColumnIds[worksheet.Id].Order(), actual.StoredColumnIds[worksheet.Id].Order());
            Assert.Equal(expected.Values.ContainsKey(worksheet.Id), actual.Values.ContainsKey(worksheet.Id));
            if (expected.Values.TryGetValue(worksheet.Id, out var expectedBlock))
            {
                AssertSameValues(expectedBlock, actual.Values[worksheet.Id]);
            }
        }
    }

    private static void AssertSameValues(RawDataBlock expected, RawDataBlock actual)
    {
        Assert.Equal(expected.RowCount, actual.RowCount);
        Assert.Equal(expected.Columns.Select(column => (column.ColumnId, column.DataType)), actual.Columns.Select(column => (column.ColumnId, column.DataType)));
        for (var i = 0; i < expected.Columns.Count; i++)
        {
            switch (expected.Columns[i])
            {
                case NumericRawDataColumn numeric:
                    Assert.Equal(numeric.Values, Assert.IsType<NumericRawDataColumn>(actual.Columns[i]).Values);
                    break;
                case StringRawDataColumn text:
                    Assert.Equal(text.Values, Assert.IsType<StringRawDataColumn>(actual.Columns[i]).Values);
                    break;
            }
        }
    }

    [Fact]
    public async Task EditedProjectFileIsFunctionallyEquivalentAfterReopening()
    {
        using var directory = new TemporaryDirectory();
        var temporaryRoot = directory.File("temp");
        var path = directory.File("ALS_Characterization.yat");
        ProjectSnapshot saved;

        using (var run = new AppRun(temporaryRoot))
        {
            await run.ShowAsync(await run.Workspace.CreateProjectAsync(path, Token));

            // Project and worksheets: rename the project, create two more worksheets and rename two of them.
            await run.RenameAsync(run.ViewModel.ProjectExplorer.ProjectItem!, "ALS Characterization Q3");
            await run.NewWorksheetAsync();
            await run.NewWorksheetAsync();
            await run.RenameAsync(run.Item("Sheet1"), "Lot Data");
            await run.RenameAsync(run.Item("Sheet3"), "Summary");

            // Numeric and String columns with empty cells (stored as null), in two worksheets.
            await run.SelectAsync("Lot Data");
            await run.PasteAsync(
                "Lot\tWafer\tVth\tNote\n" +
                "A01\t1\t0.412\tok\n" +
                "A01\t2\t\t\n" +
                "\t3\t0.398\tretest\n" +
                "A02\t\t0.405\t\n");
            await run.SelectAsync("Summary");
            await run.PasteAsync("Bin\tCount\n1\t118\n7\t\n");

            // A column with semantic type and unit (added through the column command, not pasted).
            run.ViewModel.ColumnName = "Idsat";
            run.ViewModel.SelectedColumnDataType = WorksheetDataType.Numeric;
            run.ViewModel.SelectedColumnSemanticType = ColumnSemanticType.TestParameter;
            run.ViewModel.ColumnUnit = "mA";
            await run.ViewModel.AddColumnCommand.ExecuteAsync(null);
            Assert.Null(run.ViewModel.ErrorMessage);

            saved = await SnapshotAsync(run.Session);
        }

        // Closed: the file is released and only the project file remains in its folder.
        Assert.Equal(["ALS_Characterization.yat"], Directory.GetFiles(directory.DirectoryPath).Select(Path.GetFileName));
        Assert.False(Directory.Exists(temporaryRoot) && Directory.EnumerateFileSystemEntries(temporaryRoot).Any());

        // The expected content, independent of the snapshot.
        Assert.Equal("ALS Characterization Q3", saved.Project.Name);
        Assert.Equal(["Lot Data", "Sheet2", "Summary"], saved.Worksheets.Select(worksheet => worksheet.Name));
        var lotData = saved.Worksheets[0];
        var summary = saved.Worksheets[2];
        Assert.Equal([(0, "Lot"), (1, "Wafer"), (2, "Vth"), (3, "Note")], saved.Columns[lotData.Id].Select(column => (column.Index, column.Name)));
        Assert.Equal(
            [WorksheetDataType.String, WorksheetDataType.Numeric, WorksheetDataType.Numeric, WorksheetDataType.String],
            saved.Columns[lotData.Id].Select(column => column.DataType));
        Assert.Equal(4, saved.RowCounts[lotData.Id]);
        var lotValues = saved.Values[lotData.Id];
        Assert.Equal(["A01", "A01", null, "A02"], Assert.IsType<StringRawDataColumn>(lotValues.Columns[0]).Values);
        Assert.Equal([1, 2, 3, null], Assert.IsType<NumericRawDataColumn>(lotValues.Columns[1]).Values);
        Assert.Equal([0.412, null, 0.398, 0.405], Assert.IsType<NumericRawDataColumn>(lotValues.Columns[2]).Values);
        Assert.Equal(["ok", null, "retest", null], Assert.IsType<StringRawDataColumn>(lotValues.Columns[3]).Values);
        var idsat = saved.Columns[summary.Id].Single(column => column.Name == "Idsat");
        Assert.Equal((2, ColumnSemanticType.TestParameter, "mA"), (idsat.Index, idsat.SemanticType, idsat.Unit));
        Assert.Empty(saved.Columns[saved.Worksheets[1].Id]);

        using (var reopenedRun = new AppRun(temporaryRoot))
        {
            var reopened = await reopenedRun.Workspace.OpenProjectAsync(path, Token);

            AssertEquivalent(saved, await SnapshotAsync(reopened));

            // The UI shows the reopened project: tree order and names, the first worksheet's grid, and paging data.
            await reopenedRun.ShowAsync(reopened);
            Assert.Equal("ALS Characterization Q3", reopenedRun.ViewModel.CurrentProject!.Name);
            Assert.Equal(["Lot Data", "Sheet2", "Summary"], reopenedRun.ViewModel.ProjectExplorer.WorksheetItems.Select(item => item.Name));
            Assert.Equal(lotData.Id, reopenedRun.ViewModel.SelectedWorksheet!.Id);
            Assert.Equal(["Lot", "Wafer", "Vth", "Note"], reopenedRun.ViewModel.GridColumns.Select(column => column.Name));
            Assert.Equal(4, reopenedRun.ViewModel.TotalRowCount);
            Assert.Equal(["", "3", "0.398", "retest"], reopenedRun.ViewModel.GridRows[2].Cells);
        }
    }

    [Fact]
    public async Task ProjectAndWorksheetRenamesPersist()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("MyProject.yat");
        Guid projectId;
        Guid sheet1Id;

        using (var run = new AppRun(directory.File("temp")))
        {
            await run.ShowAsync(await run.Workspace.CreateProjectAsync(path, Token));
            projectId = run.ViewModel.CurrentProject!.Id;
            sheet1Id = run.Item("Sheet1").WorksheetId;

            await run.RenameAsync(run.ViewModel.ProjectExplorer.ProjectItem!, "Renamed Project");
            await run.RenameAsync(run.Item("Sheet1"), "Renamed Sheet");
            await run.RenameAsync(run.Item("Renamed Sheet"), "RENAMED SHEET");
        }

        using var reopenedRun = new AppRun(directory.File("temp"));
        var reopened = await reopenedRun.Workspace.OpenProjectAsync(path, Token);

        var project = await reopened.Projects.GetByIdAsync(projectId, Token);
        Assert.Equal("Renamed Project", project!.Name);
        Assert.Equal(Now, project.UpdatedAt);
        var worksheet = Assert.Single(await reopened.Worksheets.GetByProjectIdAsync(projectId, Token));
        Assert.Equal((sheet1Id, "RENAMED SHEET"), (worksheet.Id, worksheet.Name));
    }

    [Fact]
    public async Task DeletedColumnsStayDeletedAndSurvivorsKeepTheirIdsAndOrder()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("MyProject.yat");
        Guid sheet1Id;
        IReadOnlyDictionary<string, Guid> idsBefore;

        using (var run = new AppRun(directory.File("temp")))
        {
            await run.ShowAsync(await run.Workspace.CreateProjectAsync(path, Token));
            sheet1Id = run.ViewModel.SelectedWorksheet!.Id;
            await run.PasteAsync("No\tBin\tSITE\tReg1\tReg2\n1\t1\t1\t5\t.132\n2\t2\t2\t7\t.157\n3\t1\t3\t\t.122\n");
            await run.ViewModel.GridLoadTask;
            idsBefore = run.ViewModel.GridColumns.ToDictionary(column => column.Name, column => column.ColumnId);

            run.ViewModel.SelectColumn(idsBefore["Bin"]);
            run.ViewModel.ToggleColumnSelection(idsBefore["Reg1"]);
            await run.ViewModel.DeleteSelectedColumnsCommand.ExecuteAsync(null);
            await run.ViewModel.GridLoadTask;
            Assert.Null(run.ViewModel.ErrorMessage);
        }

        using var reopenedRun = new AppRun(directory.File("temp"));
        var reopened = await reopenedRun.Workspace.OpenProjectAsync(path, Token);

        var columns = await reopened.WorksheetColumns.GetByWorksheetIdAsync(sheet1Id, Token);
        Assert.Equal(
            [(idsBefore["No"], 0, "No"), (idsBefore["SITE"], 1, "SITE"), (idsBefore["Reg2"], 2, "Reg2")],
            columns.Select(column => (column.Id, column.Index, column.Name)));

        // Raw state matches metadata: exactly the surviving columns have stored values, with their original values.
        Assert.Equal(columns.Select(column => column.Id).Order(), (await reopened.RawDataStore.GetStoredColumnIdsAsync(sheet1Id, Token)).Order());
        var block = await reopened.RawDataStore.ReadColumnsAsync(sheet1Id, columns.Select(column => column.Id).ToArray(), 0, 10, Token);
        Assert.Equal([1, 2, 3], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal([1, 2, 3], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
        Assert.Equal([0.132, 0.157, 0.122], Assert.IsType<NumericRawDataColumn>(block.Columns[2]).Values);

        await reopenedRun.ShowAsync(reopened);
        Assert.Equal(["No", "SITE", "Reg2"], reopenedRun.ViewModel.GridColumns.Select(column => column.Name));
    }

    [Fact]
    public async Task TemporaryProjectSavedAsAFileReopensWithItsData()
    {
        using var directory = new TemporaryDirectory();
        var temporaryRoot = directory.File("temp");
        var path = directory.File("Saved.yat");
        ProjectSnapshot beforeSave;
        string temporaryPath;

        using (var run = new AppRun(temporaryRoot))
        {
            await run.ShowAsync(await run.Workspace.CreateTemporaryProjectAsync(Token));
            temporaryPath = run.Session.Database!.FilePath;
            await run.PasteAsync("Lot\tVth\nA\t0.41\n\t\nC\t0.43\n");
            await run.RenameAsync(run.Item("Sheet1"), "Measurements");
            beforeSave = await SnapshotAsync(run.Session);

            await run.Workspace.SaveAsAsync(path, Token);

            Assert.False(File.Exists(temporaryPath));
        }

        Assert.False(Directory.Exists(Path.GetDirectoryName(temporaryPath)));

        using var reopenedRun = new AppRun(temporaryRoot);
        var reopened = await reopenedRun.Workspace.OpenProjectAsync(path, Token);
        AssertEquivalent(beforeSave, await SnapshotAsync(reopened));
        Assert.Equal("Untitled Project", beforeSave.Project.Name);
    }

    [Fact]
    public async Task ProductionStartupShowsTheTemporaryProjectAndEditsReachItsDatabase()
    {
        using var directory = new TemporaryDirectory();
        using var run = new AppRun(directory.File("temp"));
        var session = await run.Workspace.CreateTemporaryProjectAsync(Token);

        await run.ShowAsync(session);

        Assert.Equal("Untitled Project", run.ViewModel.CurrentProject!.Name);
        var sheet1 = Assert.Single(run.ViewModel.Worksheets);
        Assert.Equal("Sheet1", sheet1.Name);
        Assert.Same(sheet1, run.ViewModel.SelectedWorksheet);
        Assert.True(run.ViewModel.ProjectExplorer.ProjectItem!.IsExpanded);
        Assert.Same(run.Item("Sheet1"), run.ViewModel.ProjectExplorer.SelectedItem);
        Assert.True(run.ViewModel.PasteCommand.CanExecute(null));

        await run.PasteAsync("Vth\n0.41\n");

        Assert.Equal(["Vth"], (await session.WorksheetColumns.GetByWorksheetIdAsync(sheet1.Id, Token)).Select(column => column.Name));
        Assert.Equal(1, await session.RawDataStore.GetWorksheetRowCountAsync(sheet1.Id, Token));
        Assert.IsAssignableFrom<IProjectDatabase>(session.Database);
    }

    [Fact]
    public async Task LoadingALegacySessionWithoutAStoredProjectDoesNothing()
    {
        var composition = new CompositionRoot(new FixedTimeProvider(Now));
        using var session = composition.CreateProjectSession(":memory:");
        var clipboard = new FakeClipboard();
        var viewModel = composition.CreateMainWindowViewModel(composition.CreateMainWindowSession(session, clipboard, clipboard));

        await viewModel.LoadProjectAsync();

        Assert.Null(viewModel.CurrentProject);
        Assert.Empty(viewModel.Worksheets);
    }
}
