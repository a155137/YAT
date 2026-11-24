using System.Text;
using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Application.Ingestion;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

public class DuckDbStreamingWriteTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 5, 1, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DuckDbWorksheetRawDataStore CreateMemoryStore() =>
        new(new DuckDbRawDataStoreSettings(":memory:", memoryLimitBytes: 256L * 1024 * 1024, threads: 1));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2047)]
    [InlineData(2048)]
    [InlineData(2049)]
    [InlineData(16383)]
    [InlineData(16384)]
    [InlineData(16385)]
    [InlineData(122880)]
    [InlineData(122881)]
    public async Task StreamingWriteRoundTripsAcrossVectorAndRowGroupBoundaries(int rowCount)
    {
        using var store = CreateMemoryStore();
        var numId = Guid.NewGuid();
        var strId = Guid.NewGuid();

        await store.WriteColumnsAsync(
            WorksheetId,
            [new RawColumnSchema(numId, WorksheetDataType.Numeric), new RawColumnSchema(strId, WorksheetDataType.String)],
            writer =>
            {
                for (var r = 0; r < rowCount; r++)
                {
                    writer.BeginRow();
                    if (r % 5 == 0)
                    {
                        writer.WriteNull();
                    }
                    else
                    {
                        writer.WriteDouble(r * 0.25);
                    }

                    if (r % 7 == 0)
                    {
                        writer.WriteNull();
                    }
                    else
                    {
                        writer.WriteString($"R{r}".AsSpan());
                    }

                    writer.EndRow();
                }
            },
            Token);

        Assert.Equal(rowCount, await store.GetWorksheetRowCountAsync(WorksheetId, Token));
        if (rowCount > 0)
        {
            var offset = Math.Max(0, rowCount - 3);
            var tail = await store.ReadColumnsAsync(WorksheetId, [numId, strId], offset, 3, Token);
            var nums = Assert.IsType<NumericRawDataColumn>(tail.Columns[0]).Values;
            var strs = Assert.IsType<StringRawDataColumn>(tail.Columns[1]).Values;
            for (var i = 0; i < tail.RowCount; i++)
            {
                var r = offset + i;
                Assert.Equal(r % 5 == 0 ? null : r * 0.25, nums[i]);
                Assert.Equal(r % 7 == 0 ? null : $"R{r}", strs[i]);
            }
        }
    }

    [Fact]
    public async Task StreamingStringHandlesLengthsEmbeddedNulSurrogatesAndUnicodeIdenticallyToLegacy()
    {
        using var store = CreateMemoryStore();
        var legacyCol = Guid.NewGuid();
        var streamCol = Guid.NewGuid();

        string?[] values =
        [
            null,
            "",
            "short",
            new string('a', 11),
            new string('b', 12),
            new string('c', 13),
            "半導體_Vth_µm_Ω_🚀",
            " leading and trailing ",
            "a\0b",
            "\uD800",
            "\uDC00",
            "x\uD800y",
            new string('z', 4095),
            new string('w', 4096),
            new string('q', 4097),
            new string('k', 65_536)
        ];

        await store.WriteColumnsAsync(WorksheetId, new RawDataBlock([new StringRawDataColumn(legacyCol, values)]), Token);
        await store.WriteColumnsAsync(
            WorksheetId,
            [new RawColumnSchema(streamCol, WorksheetDataType.String)],
            writer =>
            {
                foreach (var value in values)
                {
                    writer.BeginRow();
                    if (value is null)
                    {
                        writer.WriteNull();
                    }
                    else
                    {
                        writer.WriteString(value.AsSpan());
                    }

                    writer.EndRow();
                }
            },
            Token);

        var block = await store.ReadColumnsAsync(WorksheetId, [legacyCol, streamCol], 0, values.Length, Token);
        var legacyRead = Assert.IsType<StringRawDataColumn>(block.Columns[0]).Values;
        var streamRead = Assert.IsType<StringRawDataColumn>(block.Columns[1]).Values;
        Assert.Equal(legacyRead, streamRead);
    }

    [Fact]
    public void DuckDbRawRowWriterReusesUtf8ScratchBufferAcrossCells()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE t (row_index BIGINT NOT NULL, c0 VARCHAR)";
            cmd.ExecuteNonQuery();
        }

        using var appender = connection.CreateAppender("t");
        var writer = new DuckDbRawRowWriter(appender, [new RawColumnSchema(Guid.NewGuid(), WorksheetDataType.String)], Token);

        for (var i = 0; i < 1_000; i++)
        {
            writer.BeginRow();
            writer.WriteString("Lot_12345_Wafer_09".AsSpan());
            writer.EndRow();
        }

        Assert.Equal(1, writer.ScratchAllocations);
        Assert.Equal(4096, writer.ScratchCapacity);

        writer.BeginRow();
        writer.WriteString(new string('x', 5_000).AsSpan());
        writer.EndRow();

        Assert.Equal(2, writer.ScratchAllocations);
        Assert.True(writer.ScratchCapacity >= 5_000);
        appender.Close();
    }

    [Fact]
    public async Task StreamingWriterRejectsProtocolViolationsAndLifetimeMisuse()
    {
        using var store = CreateMemoryStore();
        var numId = Guid.NewGuid();
        var strId = Guid.NewGuid();
        RawColumnSchema[] schema =
        [
            new RawColumnSchema(numId, WorksheetDataType.Numeric),
            new RawColumnSchema(strId, WorksheetDataType.String)
        ];

        // EndRow without BeginRow
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => w.EndRow(), Token));

        // Nested BeginRow
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => { w.BeginRow(); w.BeginRow(); }, Token));

        // WriteString on Numeric column
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => { w.BeginRow(); w.WriteString("x"); }, Token));

        // WriteDouble on String column
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => { w.BeginRow(); w.WriteDouble(1); w.WriteDouble(2); }, Token));

        // Non-finite double
        await Assert.ThrowsAsync<ArgumentException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => { w.BeginRow(); w.WriteDouble(double.NaN); }, Token));

        // Too few values on EndRow
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => { w.BeginRow(); w.WriteDouble(1); w.EndRow(); }, Token));

        // Too many values
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => { w.BeginRow(); w.WriteDouble(1); w.WriteNull(); w.WriteNull(); }, Token));

        // Open row left on return
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.WriteColumnsAsync(WorksheetId, schema, w => { w.BeginRow(); w.WriteDouble(1); }, Token));

        // Post-callback lifetime misuse
        IRawDataRowWriter? captured = null;
        await store.WriteColumnsAsync(WorksheetId, schema, w => { captured = w; }, Token);
        Assert.Throws<ObjectDisposedException>(() => captured!.BeginRow());
    }

    [Theory]
    [InlineData(nameof(DuckDbRawWritePhase.AfterAppend))]
    [InlineData(nameof(DuckDbRawWritePhase.AfterCatalogUpdate))]
    [InlineData(nameof(DuckDbRawWritePhase.AfterCleanup))]
    public async Task StreamingFailureOrCancellationRollsBackFileDatabaseCleanly(string failingPhaseName)
    {
        var failingPhase = Enum.Parse<DuckDbRawWritePhase>(failingPhaseName);
        using var database = new TemporaryDatabaseFile();
        var col1 = Guid.NewGuid();
        var col2 = Guid.NewGuid();

        using (var store = new DuckDbWorksheetRawDataStore(new DuckDbRawDataStoreSettings(database.FilePath)))
        {
            await store.WriteColumnsAsync(
                WorksheetId,
                [new RawColumnSchema(col1, WorksheetDataType.Numeric)],
                w => { w.BeginRow(); w.WriteDouble(42); w.EndRow(); },
                Token);

            store.WritePhaseHook = phase =>
            {
                if (phase == failingPhase)
                {
                    throw new InvalidOperationException($"Injected {phase}");
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteColumnsAsync(
                WorksheetId,
                [new RawColumnSchema(col1, WorksheetDataType.Numeric), new RawColumnSchema(col2, WorksheetDataType.Numeric)],
                w => { w.BeginRow(); w.WriteDouble(99); w.WriteDouble(100); w.EndRow(); },
                Token));

            store.WritePhaseHook = null;
            var block = await store.ReadColumnsAsync(WorksheetId, [col1], 0, 10, Token);
            Assert.Equal([42], Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns)).Values);
            await Assert.ThrowsAsync<EntityNotFoundException>(() => store.ReadColumnsAsync(WorksheetId, [col2], 0, 1, Token));
        }
    }

    [Fact]
    public async Task StreamingCancellationTriggersOnWideRowAndRollsBack()
    {
        using var store = CreateMemoryStore();
        var baselineCol = Guid.NewGuid();
        await store.WriteColumnsAsync(WorksheetId, new RawDataBlock([new NumericRawDataColumn(baselineCol, [1])]), Token);

        const int wideColumns = 20_000;
        var schema = Enumerable.Range(0, wideColumns)
            .Select(i => new RawColumnSchema(i == 0 ? baselineCol : Guid.NewGuid(), WorksheetDataType.Numeric))
            .ToArray();

        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WriteColumnsAsync(
            WorksheetId,
            schema,
            writer =>
            {
                writer.BeginRow();
                for (var c = 0; c < wideColumns; c++)
                {
                    if (c == DuckDbRawRowWriter.CancellationCheckInterval)
                    {
                        cts.Cancel();
                    }

                    writer.WriteDouble(c);
                }

                writer.EndRow();
            },
            cts.Token));

        var block = await store.ReadColumnsAsync(WorksheetId, [baselineCol], 0, 10, Token);
        Assert.Equal([1], Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns)).Values);
    }

    [Fact]
    public async Task StreamingPasteOverwritesAndSurvivesProjectDatabaseReopen()
    {
        using var storage = new TemporaryProjectStorage();
        var filePath = storage.File("StreamingProject.yat");
        Guid worksheetId;
        Guid[] initialIds;

        using (var db = storage.Storage.Create(filePath))
        {
            var projects = new DuckDbProjectRepository(db);
            var worksheets = new DuckDbWorksheetRepository(db);
            var columns = new DuckDbWorksheetColumnRepository(db, new FixedTimeProvider(Now));
            var rawStore = new DuckDbWorksheetRawDataStore(db);

            var project = new Project { Id = Guid.NewGuid(), Name = "P", CreatedAt = Now, UpdatedAt = Now };
            await projects.AddAsync(project, Token);
            var worksheet = new Worksheet { Id = Guid.NewGuid(), ProjectId = project.Id, Name = "W", CreatedAt = Now, UpdatedAt = Now };
            await worksheets.AddAsync(worksheet, Token);
            worksheetId = worksheet.Id;

            var planner = new WorksheetPastePlanner(new ColumnDataTypeDetector());
            var service = new PasteExecutionService(worksheets, columns, rawStore);

            var p1 = planner.PlanFromText(worksheetId, [], 0, "No\tBin\tSITE\n1\t10\t1\n2\t20\t2\n3\t30\t3\n", Token);
            var r1 = await service.ExecuteFromTextAsync(p1, "No\tBin\tSITE\n1\t10\t1\n2\t20\t2\n3\t30\t3\n", Token);
            initialIds = r1.Columns.Select(c => c.ColumnId).ToArray();

            var existing = await columns.GetByWorksheetIdAsync(worksheetId, Token);
            var p2 = planner.PlanFromText(worksheetId, existing, 1, "Lot\nN1\n \nN3\nN4\n", Token);
            var r2 = await service.ExecuteFromTextAsync(p2, "Lot\nN1\n \nN3\nN4\n", Token);
            Assert.Equal(initialIds[1], Assert.Single(r2.Columns).ColumnId);
            Assert.False(Assert.Single(r2.Columns).IsNew);
        }

        using (var reopened = storage.Storage.Open(filePath))
        {
            var columns = new DuckDbWorksheetColumnRepository(reopened, new FixedTimeProvider(Now));
            var rawStore = new DuckDbWorksheetRawDataStore(reopened);

            var storedCols = await columns.GetByWorksheetIdAsync(worksheetId, Token);
            Assert.Equal(["No", "Lot", "SITE"], storedCols.Select(c => c.Name));
            Assert.Equal(initialIds, storedCols.Select(c => c.Id));

            var block = await rawStore.ReadColumnsAsync(worksheetId, initialIds, 0, 10, Token);
            Assert.Equal([1, 2, 3, null], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
            Assert.Equal(["N1", null, "N3", "N4"], Assert.IsType<StringRawDataColumn>(block.Columns[1]).Values);
            Assert.Equal([1, 2, 3, null], Assert.IsType<NumericRawDataColumn>(block.Columns[2]).Values);
        }
    }
}
