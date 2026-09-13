using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Ingestion;
using YAT.app.Composition;
using YAT.Domain.Entities;

namespace YAT.App.Tests;

// ProjectSession composition over the real in-memory metadata repositories and real DuckDB raw stores.
public class ProjectSessionCompositionTests
{
    private const string MemoryDatabase = ":memory:";

    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static CompositionRoot CreateCompositionRoot() => new(new FixedTimeProvider(Now));

    private static async Task<Worksheet> AddWorksheetAsync(ProjectSession session)
    {
        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "WAT_Lot_A" };
        await session.Worksheets.AddAsync(worksheet, Token);
        return worksheet;
    }

    private static async Task<PasteExecutionResult> PasteAsync(ProjectSession session, Guid worksheetId, string text)
    {
        var data = new TabularTextParser().Parse(text);
        var existing = await session.WorksheetColumns.GetByWorksheetIdAsync(worksheetId, Token);
        var plan = new WorksheetPastePlanner(new ColumnDataTypeDetector()).Plan(worksheetId, existing, 0, data);

        return await session.PasteExecution.ExecuteAsync(plan, data, Token);
    }

    private static Task<RawDataBlock> ReadAllAsync(ProjectSession session, Guid worksheetId, PasteExecutionResult result) =>
        session.RawDataStore.ReadColumnsAsync(
            worksheetId, result.Columns.Select(column => column.ColumnId).ToArray(), 0, int.MaxValue, Token);

    // 1
    [Fact]
    public void CreatesSessionWithAllServices()
    {
        using var session = CreateCompositionRoot().CreateProjectSession(MemoryDatabase);

        Assert.NotNull(session.Worksheets);
        Assert.NotNull(session.WorksheetColumns);
        Assert.NotNull(session.RawDataStore);
        Assert.NotNull(session.PasteExecution);
    }

    // 2
    [Fact]
    public async Task PasteExecutionUsesTheSessionsOwnRepositoriesAndRawStore()
    {
        using var session = CreateCompositionRoot().CreateProjectSession(MemoryDatabase);
        var worksheet = await AddWorksheetAsync(session);

        var result = await PasteAsync(session, worksheet.Id, "SITE\tReg2\n1\t0.132\n2\t0.157\n");

        var columns = await session.WorksheetColumns.GetByWorksheetIdAsync(worksheet.Id, Token);
        Assert.Equal(result.Columns.Select(column => column.ColumnId), columns.Select(column => column.Id));
        Assert.Equal(["SITE", "Reg2"], columns.Select(column => column.Name));

        var block = await ReadAllAsync(session, worksheet.Id, result);
        Assert.Equal([1, 2], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
        Assert.Equal([0.132, 0.157], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
    }

    [Fact]
    public async Task PasteExecutionDoesNotSeeWorksheetsOfAnotherSession()
    {
        var compositionRoot = CreateCompositionRoot();
        using var first = compositionRoot.CreateProjectSession(MemoryDatabase);
        using var second = compositionRoot.CreateProjectSession(MemoryDatabase);
        var worksheet = await AddWorksheetAsync(second);

        await Assert.ThrowsAsync<EntityNotFoundException>(() => PasteAsync(first, worksheet.Id, "SITE\n1\n"));

        Assert.Empty(await second.WorksheetColumns.GetByWorksheetIdAsync(worksheet.Id, Token));
    }

    // 3
    [Fact]
    public async Task SessionsHaveIsolatedMetadataRepositories()
    {
        var compositionRoot = CreateCompositionRoot();
        using var first = compositionRoot.CreateProjectSession(MemoryDatabase);
        using var second = compositionRoot.CreateProjectSession(MemoryDatabase);

        Assert.NotSame(first.Worksheets, second.Worksheets);
        Assert.NotSame(first.WorksheetColumns, second.WorksheetColumns);
        Assert.NotSame(first.RawDataStore, second.RawDataStore);
        Assert.NotSame(first.PasteExecution, second.PasteExecution);

        var worksheet = await AddWorksheetAsync(first);
        await PasteAsync(first, worksheet.Id, "SITE\n1\n");

        Assert.Null(await second.Worksheets.GetByIdAsync(worksheet.Id, Token));
        Assert.Empty(await second.WorksheetColumns.GetByWorksheetIdAsync(worksheet.Id, Token));
    }

    // 4
    [Fact]
    public async Task SessionsWithDifferentDatabasePathsDoNotShareRawStorage()
    {
        using var directory = new TemporaryDirectory();
        var compositionRoot = CreateCompositionRoot();
        var firstPath = directory.File("first.duckdb");
        var secondPath = directory.File("second.duckdb");
        Guid worksheetId;
        PasteExecutionResult result;

        using (var first = compositionRoot.CreateProjectSession(firstPath))
        using (var second = compositionRoot.CreateProjectSession(secondPath))
        {
            var worksheet = await AddWorksheetAsync(first);
            worksheetId = worksheet.Id;
            result = await PasteAsync(first, worksheetId, "SITE\n1\n2\n");

            Assert.Equal(2, (await ReadAllAsync(first, worksheetId, result)).RowCount);
            await Assert.ThrowsAsync<EntityNotFoundException>(() => ReadAllAsync(second, worksheetId, result));
        }

        Assert.True(File.Exists(firstPath));

        // The raw values live in the first project's database file and are readable there once reopened.
        using var reopened = compositionRoot.CreateProjectSession(firstPath);
        var block = await ReadAllAsync(reopened, worksheetId, result);
        Assert.Equal([1, 2], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
    }

    [Fact]
    public async Task NewSessionOnSameDatabaseReadsRawDataButStartsWithEmptyMetadata()
    {
        using var directory = new TemporaryDirectory();
        var compositionRoot = CreateCompositionRoot();
        var path = directory.File("project.duckdb");

        var session = compositionRoot.CreateProjectSession(path);
        var worksheetId = (await AddWorksheetAsync(session)).Id;
        var result = await PasteAsync(session, worksheetId, "SITE\n1\n");
        session.Dispose();
        session.Dispose();

        // Raw values committed by the disposed session remain in the project database and a new session can use it.
        using var next = compositionRoot.CreateProjectSession(path);
        var block = await ReadAllAsync(next, worksheetId, result);
        Assert.Equal([1], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);

        // Metadata repositories are still in-memory and belong to the disposed session.
        Assert.Null(await next.Worksheets.GetByIdAsync(worksheetId, Token));
        Assert.Empty(await next.WorksheetColumns.GetByWorksheetIdAsync(worksheetId, Token));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsBlankDatabasePath(string databasePath)
    {
        Assert.Throws<ArgumentException>(() => CreateCompositionRoot().CreateProjectSession(databasePath));
    }

    // 5
    [Fact]
    public void CreatesSessionWithoutMainWindowViewModelOrUi()
    {
        var compositionRoot = CreateCompositionRoot();

        using var session = compositionRoot.CreateProjectSession(MemoryDatabase);

        Assert.NotNull(session.PasteExecution);
    }

    [Fact]
    public void SessionExposesNoInfrastructureOrUiTypes()
    {
        string[] forbiddenPrefixes = ["YAT.Infrastructure", "YAT.app.ViewModels", "YAT.app.Views", "Avalonia", "DuckDB"];

        var exposedTypes = typeof(ProjectSession).GetProperties()
            .Select(property => property.PropertyType)
            .Concat(typeof(ProjectSession).GetMethods().SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)))
            .Select(type => type.FullName!);

        Assert.DoesNotContain(exposedTypes, name => forbiddenPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)));
        Assert.Empty(typeof(ProjectSession).GetConstructors());
    }
}
