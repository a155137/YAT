using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

// Persistent metadata repositories: the same contract as the in-memory repositories, plus survival across reopening.
public class DuckDbMetadataRepositoryTests
{
    private static readonly DateTimeOffset Created = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset Updated = new(2026, 5, 2, 8, 30, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class SettableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ProjectFile : IDisposable
    {
        private readonly TemporaryProjectStorage _directory = new();

        public ProjectFile()
        {
            Path = _directory.File("MyProject.yat");
            Database = _directory.Storage.Create(Path);
        }

        public string Path { get; }

        public DuckDbProjectDatabase Database { get; private set; }

        public SettableTimeProvider Clock { get; } = new(Created);

        public DuckDbProjectRepository Projects => new(Database);

        public DuckDbWorksheetRepository Worksheets => new(Database);

        public DuckDbWorksheetColumnRepository Columns => new(Database, Clock);

        // Closes the database and opens the file again: nothing is shared with the previous repositories.
        public void Reopen()
        {
            Database.Dispose();
            Database = _directory.Storage.Open(Path);
        }

        public T Inspect<T>(Func<DuckDB.NET.Data.DuckDBConnection, T> query) =>
            Database.Execute(query);

        public void Dispose()
        {
            Database.Dispose();
            _directory.Dispose();
        }
    }

    private static Project NewProject(string name = "ALS_Characterization") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = "CP yield review",
        CreatedAt = Created,
        UpdatedAt = Updated
    };

    private static Worksheet NewWorksheet(Guid projectId, string name) => new()
    {
        Id = Guid.NewGuid(),
        ProjectId = projectId,
        Name = name,
        RowCount = 1200,
        ColumnCount = 3,
        CreatedAt = Created,
        UpdatedAt = Updated
    };

    private static WorksheetColumn NewColumn(Guid worksheetId, int index, string name) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = worksheetId,
        Index = index,
        Name = name,
        DataType = WorksheetDataType.Numeric
    };

    private static void AssertSame(Project expected, Project? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(
            (expected.Id, expected.Name, expected.Description, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.Name, actual.Description, actual.CreatedAt, actual.UpdatedAt));
    }

    private static void AssertSame(Worksheet expected, Worksheet? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(
            (expected.Id, expected.ProjectId, expected.Name, expected.RowCount, expected.ColumnCount, expected.CreatedAt, expected.UpdatedAt),
            (actual.Id, actual.ProjectId, actual.Name, actual.RowCount, actual.ColumnCount, actual.CreatedAt, actual.UpdatedAt));
    }

    private static void AssertSame(WorksheetColumn expected, WorksheetColumn? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(
            (expected.Id, expected.WorksheetId, expected.Index, expected.Name, expected.DataType, expected.SemanticType, expected.Unit),
            (actual.Id, actual.WorksheetId, actual.Index, actual.Name, actual.DataType, actual.SemanticType, actual.Unit));
    }

    // ---- Project ----

    [Fact]
    public async Task ProjectIsAddedReadAndSurvivesReopening()
    {
        using var file = new ProjectFile();
        var project = NewProject();

        await file.Projects.AddAsync(project, Token);

        AssertSame(project, await file.Projects.GetByIdAsync(project.Id, Token));
        file.Reopen();
        AssertSame(project, await file.Projects.GetByIdAsync(project.Id, Token));
        AssertSame(project, await file.Projects.GetFileProjectAsync(Token));
    }

    [Fact]
    public async Task ProjectWithoutDescriptionRoundTripsNull()
    {
        using var file = new ProjectFile();
        var project = NewProject();
        project.Description = null;

        await file.Projects.AddAsync(project, Token);
        file.Reopen();

        Assert.Null((await file.Projects.GetByIdAsync(project.Id, Token))!.Description);
    }

    [Fact]
    public async Task UnknownProjectIsNull()
    {
        using var file = new ProjectFile();

        Assert.Null(await file.Projects.GetByIdAsync(Guid.NewGuid(), Token));
        Assert.Null(await file.Projects.GetFileProjectAsync(Token));
    }

    [Fact]
    public async Task AFileHoldsOnlyOneProject()
    {
        using var file = new ProjectFile();
        var project = NewProject();
        await file.Projects.AddAsync(project, Token);

        var exception = await Assert.ThrowsAsync<ProjectStorageException>(() => file.Projects.AddAsync(NewProject("Other"), Token));

        Assert.Equal(ProjectStorageError.ProjectAlreadyExists, exception.Error);
        AssertSame(project, await file.Projects.GetFileProjectAsync(Token));
    }

    [Fact]
    public async Task AddingTheStoredProjectAgainReplacesIt()
    {
        using var file = new ProjectFile();
        var project = NewProject();
        await file.Projects.AddAsync(project, Token);
        project.Name = "Replacement";

        await file.Projects.AddAsync(project, Token);

        AssertSame(project, await file.Projects.GetFileProjectAsync(Token));
    }

    [Fact]
    public async Task ProjectUpdateReplacesTheStoredValuesAndSurvivesReopening()
    {
        using var file = new ProjectFile();
        var project = NewProject();
        await file.Projects.AddAsync(project, Token);
        var renamed = NewProject("Renamed");
        renamed.Id = project.Id;
        renamed.Description = null;
        renamed.UpdatedAt = Updated.AddDays(1);

        await file.Projects.UpdateAsync(renamed, Token);
        file.Reopen();

        AssertSame(renamed, await file.Projects.GetByIdAsync(project.Id, Token));
    }

    [Fact]
    public async Task UpdateOfAnUnknownProjectThrowsAndStoresNothing()
    {
        using var file = new ProjectFile();
        var project = NewProject();

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => file.Projects.UpdateAsync(project, Token));

        Assert.Equal(project.Id, exception.Id);
        Assert.Null(await file.Projects.GetFileProjectAsync(Token));
    }

    [Fact]
    public async Task TimestampsAreStoredInUtcWithMicrosecondPrecision()
    {
        using var file = new ProjectFile();
        var project = NewProject();
        project.CreatedAt = new DateTimeOffset(2026, 5, 1, 20, 0, 0, TimeSpan.FromHours(8)).AddTicks(1_234_567);
        project.UpdatedAt = project.CreatedAt.AddTicks(9);

        await file.Projects.AddAsync(project, Token);
        var sameSession = await file.Projects.GetByIdAsync(project.Id, Token);
        file.Reopen();
        var reopened = await file.Projects.GetByIdAsync(project.Id, Token);

        var expected = new DateTimeOffset(2026, 5, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(1_234_560);
        Assert.Equal(expected, sameSession!.CreatedAt);
        Assert.Equal(TimeSpan.Zero, sameSession.CreatedAt.Offset);
        Assert.Equal(expected.AddTicks(10), sameSession.UpdatedAt);
        Assert.Equal((sameSession.CreatedAt, sameSession.UpdatedAt), (reopened!.CreatedAt, reopened.UpdatedAt));
        Assert.Equal(TimeSpan.Zero, reopened.CreatedAt.Offset);
    }

    // ---- Worksheet ----

    [Fact]
    public async Task WorksheetIsAddedReadAndSurvivesReopening()
    {
        using var file = new ProjectFile();
        var worksheet = NewWorksheet(Guid.NewGuid(), "WAT_Lot_A");

        await file.Worksheets.AddAsync(worksheet, Token);

        AssertSame(worksheet, await file.Worksheets.GetByIdAsync(worksheet.Id, Token));
        file.Reopen();
        AssertSame(worksheet, await file.Worksheets.GetByIdAsync(worksheet.Id, Token));
    }

    [Fact]
    public async Task UnknownWorksheetIsNull()
    {
        using var file = new ProjectFile();

        Assert.Null(await file.Worksheets.GetByIdAsync(Guid.NewGuid(), Token));
        Assert.Empty(await file.Worksheets.GetByProjectIdAsync(Guid.NewGuid(), Token));
    }

    [Fact]
    public async Task WorksheetsOfAProjectKeepTheirCreationOrderAcrossReopening()
    {
        using var file = new ProjectFile();
        var projectId = Guid.NewGuid();
        var other = NewWorksheet(Guid.NewGuid(), "Other");
        var sheets = new[] { NewWorksheet(projectId, "Zeta"), NewWorksheet(projectId, "Alpha"), NewWorksheet(projectId, "Mid") };

        await file.Worksheets.AddAsync(sheets[0], Token);
        await file.Worksheets.AddAsync(other, Token);
        await file.Worksheets.AddAsync(sheets[1], Token);
        await file.Worksheets.AddAsync(sheets[2], Token);

        Assert.Equal(sheets.Select(sheet => sheet.Id), (await file.Worksheets.GetByProjectIdAsync(projectId, Token)).Select(sheet => sheet.Id));
        file.Reopen();
        Assert.Equal(sheets.Select(sheet => sheet.Id), (await file.Worksheets.GetByProjectIdAsync(projectId, Token)).Select(sheet => sheet.Id));
        Assert.Equal([other.Id], (await file.Worksheets.GetByProjectIdAsync(other.ProjectId, Token)).Select(sheet => sheet.Id));
        Assert.Equal(
            [0, 1, 2],
            file.Inspect(connection => DuckDbMetadata.Query(
                connection, "SELECT position FROM worksheet WHERE project_id = $p ORDER BY position", reader => reader.GetInt32(0), ("p", projectId))));
    }

    [Fact]
    public async Task UpdatingOrReAddingAWorksheetKeepsItsPosition()
    {
        using var file = new ProjectFile();
        var projectId = Guid.NewGuid();
        var first = NewWorksheet(projectId, "Sheet1");
        var second = NewWorksheet(projectId, "Sheet2");
        await file.Worksheets.AddAsync(first, Token);
        await file.Worksheets.AddAsync(second, Token);

        first.Name = "Renamed";
        await file.Worksheets.UpdateAsync(first, Token);
        second.Name = "ReAdded";
        await file.Worksheets.AddAsync(second, Token);
        file.Reopen();

        var stored = await file.Worksheets.GetByProjectIdAsync(projectId, Token);
        Assert.Equal(["Renamed", "ReAdded"], stored.Select(sheet => sheet.Name));
        AssertSame(first, stored[0]);
        AssertSame(second, stored[1]);
    }

    [Fact]
    public async Task WorksheetUpdateReplacesStoredValues()
    {
        using var file = new ProjectFile();
        var worksheet = NewWorksheet(Guid.NewGuid(), "Sheet1");
        await file.Worksheets.AddAsync(worksheet, Token);
        var renamed = NewWorksheet(worksheet.ProjectId, "WAT_Lot_A");
        renamed.Id = worksheet.Id;
        renamed.RowCount = 5;
        renamed.ColumnCount = 7;
        renamed.UpdatedAt = Updated.AddHours(3);

        await file.Worksheets.UpdateAsync(renamed, Token);
        file.Reopen();

        AssertSame(renamed, await file.Worksheets.GetByIdAsync(worksheet.Id, Token));
    }

    [Fact]
    public async Task UpdateOfAnUnknownWorksheetThrowsAndStoresNothing()
    {
        using var file = new ProjectFile();
        var worksheet = NewWorksheet(Guid.NewGuid(), "Sheet1");

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => file.Worksheets.UpdateAsync(worksheet, Token));

        Assert.Equal(worksheet.Id, exception.Id);
        Assert.Null(await file.Worksheets.GetByIdAsync(worksheet.Id, Token));
        Assert.Empty(await file.Worksheets.GetByProjectIdAsync(worksheet.ProjectId, Token));
    }

    [Fact]
    public async Task WorksheetNamesAreNotConstrainedByStorage()
    {
        // Name rules (unique, case-insensitive) belong to Application; the repository stores what it is given.
        using var file = new ProjectFile();
        var projectId = Guid.NewGuid();

        await file.Worksheets.AddAsync(NewWorksheet(projectId, "Sheet1"), Token);
        await file.Worksheets.AddAsync(NewWorksheet(projectId, "SHEET1"), Token);

        Assert.Equal(2, (await file.Worksheets.GetByProjectIdAsync(projectId, Token)).Count);
    }

    // ---- WorksheetColumn ----

    [Fact]
    public async Task ColumnMetadataRoundTripsAcrossReopening()
    {
        using var file = new ProjectFile();
        var worksheetId = Guid.NewGuid();
        var vth = NewColumn(worksheetId, 0, "Vth");
        vth.SemanticType = ColumnSemanticType.TestParameter;
        vth.Unit = "mV";
        var lot = NewColumn(worksheetId, 1, "Lot");
        lot.DataType = WorksheetDataType.String;
        lot.SemanticType = ColumnSemanticType.Lot;

        await file.Columns.AddAsync(vth, Token);
        await file.Columns.AddAsync(lot, Token);
        file.Reopen();

        var stored = await file.Columns.GetByWorksheetIdAsync(worksheetId, Token);
        Assert.Equal(2, stored.Count);
        AssertSame(vth, stored[0]);
        AssertSame(lot, stored[1]);
    }

    [Fact]
    public async Task ColumnsAreReturnedForTheirWorksheetOrderedByIndex()
    {
        using var file = new ProjectFile();
        var worksheetId = Guid.NewGuid();
        var third = NewColumn(worksheetId, 2, "C");
        var first = NewColumn(worksheetId, 0, "A");
        var other = NewColumn(Guid.NewGuid(), 1, "Other");
        var second = NewColumn(worksheetId, 1, "B");

        foreach (var column in new[] { third, first, other, second })
        {
            await file.Columns.AddAsync(column, Token);
        }

        Assert.Equal([first.Id, second.Id, third.Id], (await file.Columns.GetByWorksheetIdAsync(worksheetId, Token)).Select(column => column.Id));
        Assert.Empty(await file.Columns.GetByWorksheetIdAsync(Guid.NewGuid(), Token));
    }

    [Fact]
    public async Task RepeatedAddOfTheSameColumnIdIsReadBackOnce()
    {
        using var file = new ProjectFile();
        var column = NewColumn(Guid.NewGuid(), 0, "Vth");
        await file.Columns.AddAsync(column, Token);
        column.Index = 4;
        column.Name = "Vth_2";

        await file.Columns.AddAsync(column, Token);

        AssertSame(column, Assert.Single(await file.Columns.GetByWorksheetIdAsync(column.WorksheetId, Token)));
    }

    [Fact]
    public async Task ColumnUpdateReplacesStoredValuesWithoutChangingIdentity()
    {
        using var file = new ProjectFile();
        var column = NewColumn(Guid.NewGuid(), 3, "Vth");
        await file.Columns.AddAsync(column, Token);
        var updated = NewColumn(column.WorksheetId, 1, "Vth (reindexed)");
        updated.Id = column.Id;
        updated.DataType = WorksheetDataType.String;
        updated.Unit = "V";

        await file.Columns.UpdateAsync(updated, Token);
        file.Reopen();

        AssertSame(updated, Assert.Single(await file.Columns.GetByWorksheetIdAsync(column.WorksheetId, Token)));
    }

    [Fact]
    public async Task ColumnTimestampsAreManagedByTheRepository()
    {
        using var file = new ProjectFile();
        var column = NewColumn(Guid.NewGuid(), 0, "Vth");
        await file.Columns.AddAsync(column, Token);
        file.Clock.UtcNow = Updated;

        column.Name = "Renamed";
        await file.Columns.UpdateAsync(column, Token);

        var (createdAt, updatedAt) = file.Inspect(connection => DuckDbMetadata.Query(
            connection,
            "SELECT created_at, updated_at FROM worksheet_column WHERE id = $id",
            reader => (DuckDbMetadata.ReadTimestamp(reader, 0), DuckDbMetadata.ReadTimestamp(reader, 1)),
            ("id", column.Id)).Single());
        Assert.Equal(Created, createdAt);
        Assert.Equal(Updated, updatedAt);
    }

    [Fact]
    public async Task UpdateOfAnUnknownColumnThrowsAndStoresNothing()
    {
        using var file = new ProjectFile();
        var column = NewColumn(Guid.NewGuid(), 0, "Vth");

        var exception = await Assert.ThrowsAsync<EntityNotFoundException>(() => file.Columns.UpdateAsync(column, Token));

        Assert.Equal(column.Id, exception.Id);
        Assert.Empty(await file.Columns.GetByWorksheetIdAsync(column.WorksheetId, Token));
    }

    [Fact]
    public async Task DeleteRemovesOnlyThatColumnWithoutReindexingAndSurvivesReopening()
    {
        using var file = new ProjectFile();
        var worksheetId = Guid.NewGuid();
        var a = NewColumn(worksheetId, 0, "A");
        var b = NewColumn(worksheetId, 1, "B");
        var c = NewColumn(worksheetId, 2, "C");
        foreach (var column in new[] { a, b, c })
        {
            await file.Columns.AddAsync(column, Token);
        }

        await file.Columns.DeleteAsync(b.Id, Token);
        file.Reopen();

        var stored = await file.Columns.GetByWorksheetIdAsync(worksheetId, Token);
        Assert.Equal([(a.Id, 0), (c.Id, 2)], stored.Select(column => (column.Id, column.Index)));
    }

    [Fact]
    public async Task DeleteOfAnUnknownOrDeletedColumnThrows()
    {
        using var file = new ProjectFile();
        var column = NewColumn(Guid.NewGuid(), 0, "Vth");
        await file.Columns.AddAsync(column, Token);
        await file.Columns.DeleteAsync(column.Id, Token);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => file.Columns.DeleteAsync(column.Id, Token));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => file.Columns.UpdateAsync(column, Token));
        await Assert.ThrowsAsync<EntityNotFoundException>(() => file.Columns.DeleteAsync(Guid.NewGuid(), Token));
    }

    [Theory]
    [InlineData("data_type", "Integer")]
    [InlineData("data_type", "0")]
    [InlineData("semantic_type", "Unknown")]
    public async Task UnsupportedStoredEnumValuesAreReportedAsStorageFailures(string column, string value)
    {
        using var file = new ProjectFile();
        var stored = NewColumn(Guid.NewGuid(), 0, "Vth");
        await file.Columns.AddAsync(stored, Token);
        file.Inspect(connection => DuckDbMetadata.Execute(connection, $"UPDATE worksheet_column SET {column} = $value", ("value", value)));

        var exception = await Assert.ThrowsAsync<ProjectStorageException>(() => file.Columns.GetByWorksheetIdAsync(stored.WorksheetId, Token));

        Assert.Equal(ProjectStorageError.StorageFailure, exception.Error);
    }
}
