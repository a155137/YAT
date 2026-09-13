using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Infrastructure.Persistence.DuckDb;
using YAT.Infrastructure.Tests.TestDoubles;

namespace YAT.Infrastructure.Tests;

// Physical storage, atomicity and reopen behavior against a temporary database file.
public partial class DuckDbRawDataStoreFileTests
{
    private static readonly Guid WorksheetId = Guid.NewGuid();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static DuckDbWorksheetRawDataStore OpenStore(TemporaryDatabaseFile database) =>
        new(new DuckDbRawDataStoreSettings(database.FilePath, memoryLimitBytes: 256L * 1024 * 1024, threads: 1));

    private static RawDataBlock Block(params RawDataColumn[] columns) => new(columns);

    private static string[] BlockTables(DuckDBConnection connection) =>
        Query(connection, "SELECT table_name FROM duckdb_tables() WHERE table_name LIKE 'blk%' ORDER BY table_name", reader => reader.GetString(0));

    private static string[] ColumnsOf(DuckDBConnection connection, string table) =>
        Query(connection, $"SELECT column_name FROM duckdb_columns() WHERE table_name = '{table}' ORDER BY column_index", reader => reader.GetString(0));

    private static long Scalar(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static T[] Query<T>(DuckDBConnection connection, string sql, Func<DuckDBDataReader, T> map)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return [.. rows];
    }

    [Fact]
    public async Task ReopeningPreservesDataAndCatalog()
    {
        using var database = new TemporaryDatabaseFile();
        var reg1 = Guid.NewGuid();
        var lot = Guid.NewGuid();
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(
                new NumericRawDataColumn(reg1, [1.5, null, 3.25]),
                new StringRawDataColumn(lot, ["N1", "", null])), Token);
        }

        using (var reopened = OpenStore(database))
        {
            var block = await reopened.ReadColumnsAsync(WorksheetId, [lot, reg1], 0, 10, Token);

            Assert.Equal(["N1", "", null], Assert.IsType<StringRawDataColumn>(block.Columns[0]).Values);
            Assert.Equal([1.5, null, 3.25], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
        }

        database.Inspect(connection =>
        {
            Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM raw_schema_info"));
            Assert.Equal(1, Scalar(connection, "SELECT CAST(value AS BIGINT) FROM raw_schema_info WHERE key = 'raw_schema_version'"));
            Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM raw_block"));
            Assert.Equal(2, Scalar(connection, "SELECT count(*) FROM raw_column"));
            return true;
        });
    }

    [Fact]
    public async Task FullyRetiredBlockIsDropped()
    {
        using var database = new TemporaryDatabaseFile();
        var reg1 = Guid.NewGuid();
        var reg2 = Guid.NewGuid();
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [1, 2]), new NumericRawDataColumn(reg2, [3, 4])), Token);
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [5]), new NumericRawDataColumn(reg2, [6])), Token);
        }

        database.Inspect(connection =>
        {
            var table = Assert.Single(BlockTables(connection));
            Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM raw_block"));
            Assert.Equal(1, Scalar(connection, $"SELECT count(*) FROM \"{table}\""));
            return true;
        });
    }

    [Fact]
    public async Task PartlyRetiredBlockDropsOnlyTheRetiredPhysicalColumn()
    {
        using var database = new TemporaryDatabaseFile();
        var site = Guid.NewGuid();
        var reg1 = Guid.NewGuid();
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(site, [1, 2, 3]), new NumericRawDataColumn(reg1, [10, 20, 30])), Token);
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [11, 21])), Token);

            var block = await store.ReadColumnsAsync(WorksheetId, [site, reg1], 0, 10, Token);
            Assert.Equal([1, 2, 3], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
            Assert.Equal([11, 21, null], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
        }

        database.Inspect(connection =>
        {
            var tables = BlockTables(connection);
            Assert.Equal(2, tables.Length);
            var physicalColumns = tables.Select(table => ColumnsOf(connection, table)).ToArray();
            Assert.Contains(["row_index", $"c_{site:N}"], physicalColumns);
            Assert.Contains(["row_index", $"c_{reg1:N}"], physicalColumns);
            Assert.Equal(2, Scalar(connection, "SELECT count(*) FROM raw_block"));
            return true;
        });
    }

    [Fact]
    public async Task PhysicalNamesAreDerivedOnlyFromGuids()
    {
        using var database = new TemporaryDatabaseFile();
        var columnIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(columnIds[0], [1]), new StringRawDataColumn(columnIds[1], ["Robert'); DROP TABLE raw_block;--"])), Token);
        }

        database.Inspect(connection =>
        {
            var table = Assert.Single(BlockTables(connection));
            Assert.Matches(BlockTableName(), table);
            Assert.Equal(["row_index", .. columnIds.Select(id => $"c_{id:N}")], ColumnsOf(connection, table));
            Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM raw_block"));
            return true;
        });
    }

    [Theory]
    [InlineData(nameof(DuckDbRawWritePhase.AfterAppend))]
    [InlineData(nameof(DuckDbRawWritePhase.AfterCatalogUpdate))]
    [InlineData(nameof(DuckDbRawWritePhase.AfterCleanup))]
    public async Task FailureInAnyWritePhaseRollsBackTheWholeWrite(string failingPhaseName)
    {
        var failingPhase = Enum.Parse<DuckDbRawWritePhase>(failingPhaseName);
        using var database = new TemporaryDatabaseFile();
        var site = Guid.NewGuid();
        var reg1 = Guid.NewGuid();
        var reg2 = Guid.NewGuid();
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(site, [1, 2, 3]), new NumericRawDataColumn(reg1, [10, 20, 30])), Token);
            store.WritePhaseHook = phase =>
            {
                if (phase == failingPhase)
                {
                    throw new InvalidOperationException($"Injected failure at {phase}.");
                }
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteColumnsAsync(WorksheetId, Block(
                new NumericRawDataColumn(reg1, [11]),
                new NumericRawDataColumn(reg2, [99])), Token));

            store.WritePhaseHook = null;
            var block = await store.ReadColumnsAsync(WorksheetId, [site, reg1], 0, 10, Token);
            Assert.Equal([1, 2, 3], Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values);
            Assert.Equal([10, 20, 30], Assert.IsType<NumericRawDataColumn>(block.Columns[1]).Values);
            await Assert.ThrowsAsync<EntityNotFoundException>(() => store.ReadColumnsAsync(WorksheetId, [reg2], 0, 1, Token));
        }

        database.Inspect(connection =>
        {
            var table = Assert.Single(BlockTables(connection));
            Assert.Equal(["row_index", $"c_{site:N}", $"c_{reg1:N}"], ColumnsOf(connection, table));
            Assert.Equal(1, Scalar(connection, "SELECT count(*) FROM raw_block"));
            Assert.Equal(2, Scalar(connection, "SELECT count(*) FROM raw_column"));
            return true;
        });
    }

    [Fact]
    public async Task CancellationBeforeCommitRollsBackTheWholeWrite()
    {
        using var database = new TemporaryDatabaseFile();
        var reg1 = Guid.NewGuid();
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [1, 2])), Token);

            using var cancellation = new CancellationTokenSource();
            store.WritePhaseHook = phase =>
            {
                if (phase == DuckDbRawWritePhase.AfterCleanup)
                {
                    cancellation.Cancel();
                }
            };

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [7, 8, 9])), cancellation.Token));

            store.WritePhaseHook = null;
            var block = await store.ReadColumnsAsync(WorksheetId, [reg1], 0, 10, Token);
            Assert.Equal([1, 2], Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns)).Values);
        }

        database.Inspect(connection =>
        {
            Assert.Single(BlockTables(connection));
            return true;
        });
    }

    [Fact]
    public async Task ReplacementSurvivesReopen()
    {
        using var database = new TemporaryDatabaseFile();
        var reg1 = Guid.NewGuid();
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [1, 2, 3, 4])), Token);
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(reg1, [5, 6])), Token);
        }

        using (var reopened = OpenStore(database))
        {
            var block = await reopened.ReadColumnsAsync(WorksheetId, [reg1], 0, 10, Token);
            Assert.Equal([5, 6], Assert.IsType<NumericRawDataColumn>(Assert.Single(block.Columns)).Values);
        }

        database.Inspect(connection =>
        {
            Assert.Single(BlockTables(connection));
            return true;
        });
    }

    [Fact]
    public async Task UnsupportedCatalogVersionIsReportedAsStorageFailure()
    {
        using var database = new TemporaryDatabaseFile();
        using (var store = OpenStore(database))
        {
            await store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(Guid.NewGuid(), [1])), Token);
        }

        database.Inspect(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE raw_schema_info SET value = '2' WHERE key = 'raw_schema_version'";
            return command.ExecuteNonQuery();
        });

        using var newer = OpenStore(database);
        var exception = await Assert.ThrowsAsync<RawDataStorageException>(
            () => newer.ReadColumnsAsync(WorksheetId, [Guid.NewGuid()], 0, 1, Token));

        Assert.Contains("'2'", exception.Message);
    }

    [Fact]
    public async Task DuckDbFailuresAreTranslatedToRawDataStorageException()
    {
        using var database = new TemporaryDatabaseFile();
        await File.WriteAllTextAsync(database.FilePath, "This file is not a DuckDB database. It only contains plain text for this test.", Token);
        using var store = OpenStore(database);

        var exception = await Assert.ThrowsAsync<RawDataStorageException>(
            () => store.WriteColumnsAsync(WorksheetId, Block(new NumericRawDataColumn(Guid.NewGuid(), [1])), Token));

        Assert.IsType<DuckDBException>(exception.InnerException);
        Assert.DoesNotContain("DuckDB", exception.GetType().Namespace!, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^blk_[0-9a-f]{32}$")]
    private static partial Regex BlockTableName();
}
