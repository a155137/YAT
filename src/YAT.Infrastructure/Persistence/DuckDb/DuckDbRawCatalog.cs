using System.Globalization;
using DuckDB.NET.Data;
using YAT.Application.Exceptions;
using YAT.Domain.Enums;

namespace YAT.Infrastructure.Persistence.DuckDb;

internal sealed record DuckDbCatalogColumn(
    Guid ColumnId,
    Guid WorksheetId,
    Guid BlockId,
    string TableName,
    long RowCount,
    string PhysicalName,
    WorksheetDataType DataType);

// Raw catalog, schema version 1:
//   raw_schema_info(key, value)                                    schema version
//   raw_block(block_id, worksheet_id, table_name, row_count)       one row per physical block table
//   raw_column(column_id, worksheet_id, block_id, physical_name, data_type)   one row per live raw column
// There are no foreign keys; integrity is maintained by the store inside each transaction.
internal static class DuckDbRawCatalog
{
    public const int SchemaVersion = 1;

    private const string SchemaVersionKey = "raw_schema_version";

    public static void Initialize(DuckDBConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            Execute(connection, "CREATE TABLE IF NOT EXISTS raw_schema_info (key VARCHAR PRIMARY KEY, value VARCHAR NOT NULL)");

            var storedVersion = Scalar(connection, "SELECT value FROM raw_schema_info WHERE key = $key", ("key", SchemaVersionKey));
            if (storedVersion is null)
            {
                Execute(connection, "CREATE TABLE raw_block (block_id UUID PRIMARY KEY, worksheet_id UUID NOT NULL, table_name VARCHAR NOT NULL, row_count BIGINT NOT NULL)");
                Execute(connection, "CREATE TABLE raw_column (column_id UUID PRIMARY KEY, worksheet_id UUID NOT NULL, block_id UUID NOT NULL, physical_name VARCHAR NOT NULL, data_type VARCHAR NOT NULL)");
                Execute(connection, "INSERT INTO raw_schema_info VALUES ($key, $value)",
                    ("key", SchemaVersionKey), ("value", SchemaVersion.ToString(CultureInfo.InvariantCulture)));
            }
            else if (!string.Equals(Convert.ToString(storedVersion, CultureInfo.InvariantCulture), SchemaVersion.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                throw new RawDataStorageException(
                    $"The raw data catalog version '{storedVersion}' is not supported; this version of YAT supports version {SchemaVersion}.");
            }

            transaction.Commit();
        }
        catch
        {
            DuckDbTransactions.RollBack(transaction);
            throw;
        }
    }

    // Catalog entries for the given column ids, keyed by column id. Ids without a live entry are absent.
    public static Dictionary<Guid, DuckDbCatalogColumn> FindColumns(DuckDBConnection connection, IReadOnlyList<Guid> columnIds)
    {
        var placeholders = string.Join(", ", columnIds.Select((_, index) => $"$id{index}"));
        using var command = CreateCommand(
            connection,
            "SELECT c.column_id, c.worksheet_id, c.block_id, b.table_name, b.row_count, c.physical_name, c.data_type " +
            "FROM raw_column c JOIN raw_block b ON b.block_id = c.block_id " +
            $"WHERE c.column_id IN ({placeholders})",
            columnIds.Select((id, index) => ($"id{index}", (object?)id)).ToArray());

        var columns = new Dictionary<Guid, DuckDbCatalogColumn>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var column = new DuckDbCatalogColumn(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                reader.GetInt64(4),
                reader.GetString(5),
                ParseDataType(reader.GetString(6)));
            columns[column.ColumnId] = column;
        }

        return columns;
    }

    // Every column in a block has the block's row count, so the longest live column is the largest referenced block.
    public static long GetWorksheetRowCount(DuckDBConnection connection, Guid worksheetId) =>
        Convert.ToInt64(Scalar(connection,
            "SELECT COALESCE(MAX(b.row_count), 0) FROM raw_column c JOIN raw_block b ON b.block_id = c.block_id " +
            "WHERE c.worksheet_id = $worksheet",
            ("worksheet", worksheetId)), CultureInfo.InvariantCulture);

    public static HashSet<Guid> FindWorksheetColumnIds(DuckDBConnection connection, Guid worksheetId)
    {
        using var command = CreateCommand(connection, "SELECT column_id FROM raw_column WHERE worksheet_id = $worksheet", ("worksheet", worksheetId));
        using var reader = command.ExecuteReader();

        var columnIds = new HashSet<Guid>();
        while (reader.Read())
        {
            columnIds.Add(reader.GetGuid(0));
        }

        return columnIds;
    }

    public static void InsertBlock(DuckDBConnection connection, Guid blockId, Guid worksheetId, string tableName, long rowCount) =>
        Execute(connection, "INSERT INTO raw_block VALUES ($block, $worksheet, $table, $rows)",
            ("block", blockId), ("worksheet", worksheetId), ("table", tableName), ("rows", rowCount));

    public static void UpsertColumn(DuckDBConnection connection, Guid columnId, Guid worksheetId, Guid blockId, string physicalName, WorksheetDataType dataType) =>
        Execute(connection,
            "INSERT INTO raw_column VALUES ($column, $worksheet, $block, $name, $type) " +
            "ON CONFLICT (column_id) DO UPDATE SET worksheet_id = EXCLUDED.worksheet_id, block_id = EXCLUDED.block_id, " +
            "physical_name = EXCLUDED.physical_name, data_type = EXCLUDED.data_type",
            ("column", columnId), ("worksheet", worksheetId), ("block", blockId), ("name", physicalName), ("type", dataType.ToString()));

    public static long CountLiveColumns(DuckDBConnection connection, Guid blockId) =>
        Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM raw_column WHERE block_id = $block", ("block", blockId)), CultureInfo.InvariantCulture);

    public static void DeleteBlock(DuckDBConnection connection, Guid blockId) =>
        Execute(connection, "DELETE FROM raw_block WHERE block_id = $block", ("block", blockId));

    public static void Execute(DuckDBConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    public static object? Scalar(DuckDBConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = CreateCommand(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    public static DuckDBCommand CreateCommand(DuckDBConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(new DuckDBParameter(name, value));
        }

        return command;
    }

    private static WorksheetDataType ParseDataType(string value) => value switch
    {
        nameof(WorksheetDataType.Numeric) => WorksheetDataType.Numeric,
        nameof(WorksheetDataType.String) => WorksheetDataType.String,
        _ => throw new RawDataStorageException($"The raw data catalog contains an unsupported data type '{value}'.")
    };
}

internal static class DuckDbTransactions
{
    // Rolls back after a failure. A rollback failure is ignored so the original exception is the one reported.
    public static void RollBack(DuckDBTransaction transaction)
    {
        try
        {
            transaction.Rollback();
        }
        catch (Exception)
        {
            // Intentionally ignored: the caller rethrows the original failure.
        }
    }
}
