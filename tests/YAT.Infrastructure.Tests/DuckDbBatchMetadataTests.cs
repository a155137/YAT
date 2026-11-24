using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Ingestion;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Persistence.InMemory;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

public class DuckDbBatchMetadataTests
{
    private static readonly DateTimeOffset Created = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Updated = new(2026, 5, 2, 8, 30, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class SettableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class FailingBatchColumnRepository(IWorksheetColumnRepository inner, Exception failure) : IWorksheetColumnRepository
    {
        public Task<IReadOnlyList<WorksheetColumn>> GetByWorksheetIdAsync(Guid worksheetId, CancellationToken cancellationToken) =>
            inner.GetByWorksheetIdAsync(worksheetId, cancellationToken);

        public Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken) =>
            inner.AddAsync(column, cancellationToken);

        public Task UpdateAsync(WorksheetColumn column, CancellationToken cancellationToken) =>
            inner.UpdateAsync(column, cancellationToken);

        public Task DeleteAsync(Guid columnId, CancellationToken cancellationToken) =>
            inner.DeleteAsync(columnId, cancellationToken);

        public Task SaveBatchAsync(
            IReadOnlyList<WorksheetColumn> added,
            IReadOnlyList<WorksheetColumn> updated,
            CancellationToken cancellationToken) =>
            Task.FromException(failure);
    }

    private static WorksheetColumn MakeColumn(
        Guid worksheetId,
        int index,
        string name,
        WorksheetDataType dataType = WorksheetDataType.Numeric,
        ColumnSemanticType? semanticType = null,
        string? unit = null) => new()
    {
        Id = Guid.NewGuid(),
        WorksheetId = worksheetId,
        Index = index,
        Name = name,
        DataType = dataType,
        SemanticType = semanticType,
        Unit = unit
    };

    [Fact]
    public async Task SaveBatchPersistsWideAddMixedUpdateAndPreservesTimestampsAcrossReopen()
    {
        using var storage = new TemporaryProjectStorage();
        var filePath = storage.File("BatchMetadata.yat");
        var clock = new SettableTimeProvider(Created);
        var worksheetId = Guid.NewGuid();

        var initial = Enumerable.Range(0, 600)
            .Select(i => MakeColumn(
                worksheetId,
                i,
                $"Col_{i}",
                i % 5 == 0 ? WorksheetDataType.String : WorksheetDataType.Numeric,
                i % 3 == 0 ? ColumnSemanticType.TestParameter : null,
                i % 3 == 0 ? "V" : null))
            .ToArray();

        using (var db = storage.Storage.Create(filePath))
        {
            var repo = new DuckDbWorksheetColumnRepository(db, clock);
            await repo.SaveBatchAsync(initial, [], Token);

            clock.UtcNow = Updated;
            var updated = initial.Take(200).Select(c => new WorksheetColumn
            {
                Id = c.Id,
                WorksheetId = c.WorksheetId,
                Index = c.Index,
                Name = c.Name + "_Replaced",
                DataType = WorksheetDataType.String,
                SemanticType = null,
                Unit = null
            }).ToArray();

            var added = Enumerable.Range(600, 100)
                .Select(i => MakeColumn(worksheetId, i, $"Col_{i}"))
                .ToArray();

            await repo.SaveBatchAsync(added, updated, Token);
        }

        using (var reopened = storage.Storage.Open(filePath))
        {
            var repo = new DuckDbWorksheetColumnRepository(reopened, clock);
            var stored = await repo.GetByWorksheetIdAsync(worksheetId, Token);
            Assert.Equal(700, stored.Count);
            Assert.Equal(Enumerable.Range(0, 700), stored.Select(c => c.Index));
            Assert.Equal("Col_0_Replaced", stored[0].Name);
            Assert.Null(stored[0].SemanticType);
            Assert.Null(stored[0].Unit);
            Assert.Equal("Col_201", stored[201].Name);
            Assert.Equal("V", stored[201].Unit);
            Assert.Equal("Col_699", stored[699].Name);

            var timestamps = reopened.Execute(connection => DuckDbMetadata.Query(
                connection,
                "SELECT id, created_at, updated_at FROM worksheet_column WHERE worksheet_id = $w",
                r => (r.GetGuid(0), DuckDbMetadata.ReadTimestamp(r, 1), DuckDbMetadata.ReadTimestamp(r, 2)),
                ("w", worksheetId)))
                .ToDictionary(t => t.Item1, t => (t.Item2, t.Item3));

            Assert.Equal((Created, Updated), timestamps[initial[0].Id]);
            Assert.Equal((Created, Created), timestamps[initial[201].Id]);
            Assert.Equal((Updated, Updated), timestamps[stored[600].Id]);
        }
    }

    [Fact]
    public async Task SaveBatchWithMissingUpdateIdRollsBackEntireBatchAndAllowsRetry()
    {
        using var storage = new TemporaryProjectStorage();
        var filePath = storage.File("BatchRollback.yat");
        var clock = new SettableTimeProvider(Created);
        var worksheetId = Guid.NewGuid();

        var existing1 = MakeColumn(worksheetId, 0, "A");
        var existing2 = MakeColumn(worksheetId, 1, "B");
        var missing = MakeColumn(worksheetId, 2, "Missing");
        var newCol1 = MakeColumn(worksheetId, 3, "C");
        var newCol2 = MakeColumn(worksheetId, 4, "D");

        using var db = storage.Storage.Create(filePath);
        var repo = new DuckDbWorksheetColumnRepository(db, clock);
        await repo.SaveBatchAsync([existing1, existing2], [], Token);

        var updated1 = new WorksheetColumn
        {
            Id = existing1.Id,
            WorksheetId = worksheetId,
            Index = 0,
            Name = "A_New",
            DataType = WorksheetDataType.String
        };

        var ex = await Assert.ThrowsAsync<EntityNotFoundException>(() =>
            repo.SaveBatchAsync([newCol1, newCol2], [updated1, missing], Token));
        Assert.Equal(missing.Id, ex.Id);

        // Entire batch rolled back: neither newCol1/newCol2 nor updated1 was persisted.
        var storedAfterFail = await repo.GetByWorksheetIdAsync(worksheetId, Token);
        Assert.Equal(["A", "B"], storedAfterFail.Select(c => c.Name));

        // Temp staging tables were cleaned up, so a valid retry on the same connection succeeds.
        await repo.SaveBatchAsync([newCol1, newCol2], [updated1], Token);
        var storedAfterRetry = await repo.GetByWorksheetIdAsync(worksheetId, Token);
        Assert.Equal(["A_New", "B", "C", "D"], storedAfterRetry.Select(c => c.Name));
    }

    [Fact]
    public async Task InMemorySaveBatchIsAllOrNothingWhenUpdateIdIsMissing()
    {
        var repo = new InMemoryWorksheetColumnRepository();
        var worksheetId = Guid.NewGuid();
        var existing = MakeColumn(worksheetId, 0, "A");
        await repo.AddAsync(existing, Token);

        var validUpdate = new WorksheetColumn
        {
            Id = existing.Id,
            WorksheetId = worksheetId,
            Index = 0,
            Name = "A_New",
            DataType = WorksheetDataType.String
        };
        var missing = MakeColumn(worksheetId, 1, "Missing");
        var added = MakeColumn(worksheetId, 2, "Added");

        await Assert.ThrowsAsync<EntityNotFoundException>(() =>
            repo.SaveBatchAsync([added], [validUpdate, missing], Token));

        var stored = await repo.GetByWorksheetIdAsync(worksheetId, Token);
        Assert.Same(existing, Assert.Single(stored));
    }

    [Fact]
    public async Task RawCatalogBatchUpsertHandlesSixHundredColumnsOverwriteAndRetry()
    {
        using var database = new TemporaryDatabaseFile();
        using var store = new DuckDbWorksheetRawDataStore(new DuckDbRawDataStoreSettings(database.FilePath));
        var worksheetId = Guid.NewGuid();
        var ids = Enumerable.Range(0, 600).Select(_ => Guid.NewGuid()).ToArray();
        var schema = ids.Select(id => new RawColumnSchema(id, WorksheetDataType.Numeric)).ToArray();

        await store.WriteColumnsAsync(
            worksheetId,
            schema,
            writer =>
            {
                writer.BeginRow();
                for (var c = 0; c < schema.Length; c++)
                {
                    writer.WriteDouble(c);
                }

                writer.EndRow();
            },
            Token);

        // Inject failure after catalog update, verify rollback, then retry overwrite cleanly.
        store.WritePhaseHook = phase =>
        {
            if (phase == DuckDbRawWritePhase.AfterCatalogUpdate)
            {
                throw new InvalidOperationException("Simulated catalog phase failure.");
            }
        };

        var overwriteSchema = ids.Take(100)
            .Select(id => new RawColumnSchema(id, WorksheetDataType.String))
            .ToArray();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteColumnsAsync(
            worksheetId,
            overwriteSchema,
            writer =>
            {
                writer.BeginRow();
                for (var c = 0; c < overwriteSchema.Length; c++)
                {
                    writer.WriteString($"S{c}".AsSpan());
                }

                writer.EndRow();
            },
            Token));

        store.WritePhaseHook = null;
        var beforeRetry = await store.ReadColumnsAsync(worksheetId, [ids[0], ids[599]], 0, 5, Token);
        Assert.Equal([0], Assert.IsType<NumericRawDataColumn>(beforeRetry.Columns[0]).Values);
        Assert.Equal([599], Assert.IsType<NumericRawDataColumn>(beforeRetry.Columns[1]).Values);

        await store.WriteColumnsAsync(
            worksheetId,
            overwriteSchema,
            writer =>
            {
                writer.BeginRow();
                for (var c = 0; c < overwriteSchema.Length; c++)
                {
                    writer.WriteString($"S{c}".AsSpan());
                }

                writer.EndRow();
            },
            Token);

        var afterRetry = await store.ReadColumnsAsync(worksheetId, [ids[0], ids[599]], 0, 5, Token);
        Assert.Equal(["S0"], Assert.IsType<StringRawDataColumn>(afterRetry.Columns[0]).Values);
        Assert.Equal([599], Assert.IsType<NumericRawDataColumn>(afterRetry.Columns[1]).Values);
    }

    [Fact]
    public async Task RawCommitFollowedByMetadataBatchFailureSurvivesReopenAndRecoversOnRetry()
    {
        using var storage = new TemporaryProjectStorage();
        var filePath = storage.File("RawCommitMetadataFail.yat");
        var clock = new SettableTimeProvider(Created);
        Guid worksheetId;
        Guid[] initialIds;

        using (var db = storage.Storage.Create(filePath))
        {
            var projects = new DuckDbProjectRepository(db);
            var worksheets = new DuckDbWorksheetRepository(db);
            var columns = new DuckDbWorksheetColumnRepository(db, clock);
            var rawStore = new DuckDbWorksheetRawDataStore(db);

            var project = new Project { Id = Guid.NewGuid(), Name = "P", CreatedAt = Created, UpdatedAt = Created };
            await projects.AddAsync(project, Token);
            var worksheet = new Worksheet { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "W", CreatedAt = Created, UpdatedAt = Created };
            await worksheets.AddAsync(worksheet, Token);
            worksheetId = worksheet.Id;

            var planner = new WorksheetPastePlanner(new ColumnDataTypeDetector());
            var service = new PasteExecutionService(worksheets, columns, rawStore);
            const string initialText = "No\tBin\tSITE\n1\t10\t1\n2\t20\t2\n";
            var initialPlan = planner.PlanFromText(worksheetId, [], 0, initialText, Token);
            var initialResult = await service.ExecuteFromTextAsync(initialPlan, initialText, Token);
            initialIds = initialResult.Columns.Select(c => c.ColumnId).ToArray();

            // Simulate metadata batch failure after raw commit.
            var failingColumns = new FailingBatchColumnRepository(columns, new InvalidOperationException("Simulated batch metadata failure."));
            var failingService = new PasteExecutionService(worksheets, failingColumns, rawStore);
            const string overwriteText = "Lot\tReg1\nN1\t5\nN2\t7\n";
            var overwritePlan = planner.PlanFromText(worksheetId, await columns.GetByWorksheetIdAsync(worksheetId, Token), 1, overwriteText, Token);

            await Assert.ThrowsAsync<InvalidOperationException>(() => failingService.ExecuteFromTextAsync(overwritePlan, overwriteText, Token));
        }

        // Reopen: raw data has overwritten Bin (now strings N1, N2) while metadata stayed untouched (No, Bin, SITE).
        using (var reopened = storage.Storage.Open(filePath))
        {
            var worksheets = new DuckDbWorksheetRepository(reopened);
            var columns = new DuckDbWorksheetColumnRepository(reopened, clock);
            var rawStore = new DuckDbWorksheetRawDataStore(reopened);

            var storedCols = await columns.GetByWorksheetIdAsync(worksheetId, Token);
            Assert.Equal(["No", "Bin", "SITE"], storedCols.Select(c => c.Name));
            Assert.Equal(initialIds, storedCols.Select(c => c.Id));

            var binRaw = await rawStore.ReadColumnsAsync(worksheetId, [initialIds[1]], 0, 10, Token);
            Assert.Equal(["N1", "N2"], Assert.IsType<StringRawDataColumn>(Assert.Single(binRaw.Columns)).Values);

            // Safe recovery retry: re-plan and re-execute the paste with healthy repository.
            var planner = new WorksheetPastePlanner(new ColumnDataTypeDetector());
            var healthyService = new PasteExecutionService(worksheets, columns, rawStore);
            const string recoveryText = "Lot\tReg1\nN1\t5\nN2\t7\n";
            var recoveryPlan = planner.PlanFromText(worksheetId, storedCols, 1, recoveryText, Token);
            var recovered = await healthyService.ExecuteFromTextAsync(recoveryPlan, recoveryText, Token);

            Assert.Equal(initialIds[1], recovered.Columns[0].ColumnId);
            Assert.False(recovered.Columns[0].IsNew);

            var finalCols = await columns.GetByWorksheetIdAsync(worksheetId, Token);
            Assert.Equal(["No", "Lot", "Reg1"], finalCols.Select(c => c.Name));
            var finalRaw = await rawStore.ReadColumnsAsync(worksheetId, finalCols.Select(c => c.Id).ToArray(), 0, 10, Token);
            Assert.Equal([1, 2], Assert.IsType<NumericRawDataColumn>(finalRaw.Columns[0]).Values);
            Assert.Equal(["N1", "N2"], Assert.IsType<StringRawDataColumn>(finalRaw.Columns[1]).Values);
            Assert.Equal([5, 7], Assert.IsType<NumericRawDataColumn>(finalRaw.Columns[2]).Values);
        }
    }
}
