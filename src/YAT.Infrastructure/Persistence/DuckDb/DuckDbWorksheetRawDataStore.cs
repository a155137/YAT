using System.Globalization;
using System.Text;
using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Enums;

namespace YAT.Infrastructure.Persistence.DuckDb;

// DuckDB implementation of IWorksheetRawDataStore for one project database.
// Each write creates a block table blk_<id>(row_index, c_<columnId>, ...) and moves raw_column catalog pointers
// to it in one explicit transaction. Committed live values are never updated; retired physical columns and
// blocks are dropped. row_index is the logical worksheet row, so columns from different blocks align on it.
// DuckDB exceptions are translated to RawDataStorageException and never leave this class.
public sealed class DuckDbWorksheetRawDataStore : IWorksheetRawDataStore, IDisposable
{
    private const int CancellationCheckInterval = 16_384;

    private readonly DuckDbRawDataStoreSettings _settings;
    private readonly Lock _gate = new();
    private DuckDBConnection? _connection;
    private bool _disposed;

    public DuckDbWorksheetRawDataStore(DuckDbRawDataStoreSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    // Test-only fault injection between write phases. Always null in production.
    internal Action<DuckDbRawWritePhase>? WritePhaseHook { get; set; }

    public Task WriteColumnsAsync(Guid worksheetId, RawDataBlock block, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(block);
            cancellationToken.ThrowIfCancellationRequested();

            Execute(connection => WriteColumns(connection, worksheetId, block, cancellationToken));
            return Task.CompletedTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    public Task<RawDataBlock> ReadColumnsAsync(
        Guid worksheetId,
        IReadOnlyList<Guid> columnIds,
        long rowOffset,
        int rowCount,
        CancellationToken cancellationToken)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(columnIds);
            if (columnIds.Count == 0)
            {
                throw new ArgumentException("At least one column id is required.", nameof(columnIds));
            }

            if (columnIds.Distinct().Count() != columnIds.Count)
            {
                throw new ArgumentException("Column ids must not contain duplicates.", nameof(columnIds));
            }

            ArgumentOutOfRangeException.ThrowIfNegative(rowOffset);
            ArgumentOutOfRangeException.ThrowIfNegative(rowCount);
            cancellationToken.ThrowIfCancellationRequested();

            var block = Execute(connection => ReadColumns(connection, worksheetId, columnIds, rowOffset, rowCount, cancellationToken));
            return Task.FromResult(block);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<RawDataBlock>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<RawDataBlock>(exception);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }
    }

    // Test-only probe of an effective DuckDB setting such as memory_limit or threads.
    internal string GetEffectiveSetting(string name) =>
        Execute(connection => Convert.ToString(
            DuckDbRawCatalog.Scalar(connection, "SELECT current_setting($name)", ("name", name)),
            CultureInfo.InvariantCulture) ?? string.Empty);

    private void WriteColumns(DuckDBConnection connection, Guid worksheetId, RawDataBlock block, CancellationToken cancellationToken)
    {
        // Hard rule: raw appends only ever run inside this explicit transaction.
        using var transaction = connection.BeginTransaction();
        try
        {
            var previous = DuckDbRawCatalog.FindColumns(connection, block.Columns.Select(column => column.ColumnId).ToArray());
            var foreignColumn = previous.Values.FirstOrDefault(column => column.WorksheetId != worksheetId);
            if (foreignColumn is not null)
            {
                throw new ArgumentException($"Raw column '{foreignColumn.ColumnId}' belongs to a different worksheet.", nameof(block));
            }

            var blockId = Guid.NewGuid();
            var tableName = DuckDbIdentifiers.BlockTable(blockId);
            CreateBlockTable(connection, tableName, block);
            AppendRows(connection, tableName, block, cancellationToken);
            WritePhaseHook?.Invoke(DuckDbRawWritePhase.AfterAppend);

            DuckDbRawCatalog.InsertBlock(connection, blockId, worksheetId, tableName, block.RowCount);
            foreach (var column in block.Columns)
            {
                DuckDbRawCatalog.UpsertColumn(
                    connection, column.ColumnId, worksheetId, blockId, DuckDbIdentifiers.ValueColumn(column.ColumnId), column.DataType);
            }

            WritePhaseHook?.Invoke(DuckDbRawWritePhase.AfterCatalogUpdate);

            RetireReplacedStorage(connection, previous.Values);
            WritePhaseHook?.Invoke(DuckDbRawWritePhase.AfterCleanup);

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            DuckDbTransactions.RollBack(transaction);
            throw;
        }
    }

    private static void CreateBlockTable(DuckDBConnection connection, string tableName, RawDataBlock block)
    {
        var sql = new StringBuilder($"CREATE TABLE {DuckDbIdentifiers.Quote(tableName)} ({DuckDbIdentifiers.RowIndexColumn} BIGINT NOT NULL");
        foreach (var column in block.Columns)
        {
            var physicalType = column.DataType switch
            {
                WorksheetDataType.Numeric => "DOUBLE",
                WorksheetDataType.String => "VARCHAR",
                _ => throw new NotSupportedException($"Raw data type '{column.DataType}' is not supported.")
            };
            sql.Append($", {DuckDbIdentifiers.Quote(DuckDbIdentifiers.ValueColumn(column.ColumnId))} {physicalType}");
        }

        sql.Append(')');
        DuckDbRawCatalog.Execute(connection, sql.ToString());
    }

    private static void AppendRows(DuckDBConnection connection, string tableName, RawDataBlock block, CancellationToken cancellationToken)
    {
        using var appender = connection.CreateAppender(tableName);
        for (var rowIndex = 0; rowIndex < block.RowCount; rowIndex++)
        {
            if (rowIndex % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var row = appender.CreateRow().AppendValue((long)rowIndex);
            foreach (var column in block.Columns)
            {
                row = column switch
                {
                    NumericRawDataColumn numeric when numeric.Values[rowIndex] is { } number => row.AppendValue(number),
                    StringRawDataColumn text when text.Values[rowIndex] is { } value => row.AppendValue(value),
                    NumericRawDataColumn or StringRawDataColumn => row.AppendNullValue(),
                    _ => throw new NotSupportedException($"Raw data column type '{column.GetType().Name}' is not supported.")
                };
            }

            row.EndRow();
        }

        appender.Close();
    }

    // Replaced columns leave their previous blocks: a block without live columns is dropped, otherwise only
    // the retired physical columns are dropped. Live values are never updated.
    private static void RetireReplacedStorage(DuckDBConnection connection, IEnumerable<DuckDbCatalogColumn> replacedColumns)
    {
        foreach (var previousBlock in replacedColumns.GroupBy(column => (column.BlockId, column.TableName)))
        {
            var table = DuckDbIdentifiers.Quote(previousBlock.Key.TableName);
            if (DuckDbRawCatalog.CountLiveColumns(connection, previousBlock.Key.BlockId) == 0)
            {
                DuckDbRawCatalog.Execute(connection, $"DROP TABLE {table}");
                DuckDbRawCatalog.DeleteBlock(connection, previousBlock.Key.BlockId);
                continue;
            }

            foreach (var retired in previousBlock)
            {
                DuckDbRawCatalog.Execute(connection, $"ALTER TABLE {table} DROP COLUMN {DuckDbIdentifiers.Quote(retired.PhysicalName)}");
            }
        }
    }

    private static RawDataBlock ReadColumns(
        DuckDBConnection connection,
        Guid worksheetId,
        IReadOnlyList<Guid> columnIds,
        long rowOffset,
        int rowCount,
        CancellationToken cancellationToken)
    {
        var catalog = DuckDbRawCatalog.FindColumns(connection, columnIds);
        var requested = new DuckDbCatalogColumn[columnIds.Count];
        for (var index = 0; index < columnIds.Count; index++)
        {
            if (!catalog.TryGetValue(columnIds[index], out var column) || column.WorksheetId != worksheetId)
            {
                throw new EntityNotFoundException("RawDataColumn", columnIds[index]);
            }

            requested[index] = column;
        }

        // Rectangular result: as long as the longest requested column; shorter columns are padded with NULL.
        var longestRowCount = requested.Max(column => column.RowCount);
        var resultRowCount = rowCount == 0 || rowOffset >= longestRowCount
            ? 0
            : (int)Math.Min(rowCount, longestRowCount - rowOffset);

        var values = requested
            .Select(column => column.DataType == WorksheetDataType.Numeric ? (Array)new double?[resultRowCount] : new string?[resultRowCount])
            .ToArray();

        if (resultRowCount > 0)
        {
            ReadWindow(connection, requested, rowOffset, resultRowCount, values, cancellationToken);
        }

        return new RawDataBlock(requested
            .Select((column, index) => column.DataType == WorksheetDataType.Numeric
                ? (RawDataColumn)new NumericRawDataColumn(column.ColumnId, (double?[])values[index])
                : new StringRawDataColumn(column.ColumnId, (string?[])values[index]))
            .ToArray());
    }

    private static void ReadWindow(
        DuckDBConnection connection,
        DuckDbCatalogColumn[] requested,
        long rowOffset,
        int resultRowCount,
        Array[] values,
        CancellationToken cancellationToken)
    {
        var blocks = requested.Select(column => (column.BlockId, column.TableName)).Distinct().ToList();
        var alias = blocks.ToDictionary(block => block.BlockId, block => $"b{blocks.IndexOf(block)}");

        var sql = new StringBuilder($"SELECT w.{DuckDbIdentifiers.RowIndexColumn}");
        foreach (var column in requested)
        {
            sql.Append($", {alias[column.BlockId]}.{DuckDbIdentifiers.Quote(column.PhysicalName)}");
        }

        sql.Append($" FROM range($start, $end) AS w({DuckDbIdentifiers.RowIndexColumn})");
        foreach (var (blockId, tableName) in blocks)
        {
            var blockColumns = string.Concat(requested
                .Where(column => column.BlockId == blockId)
                .Select(column => $", {DuckDbIdentifiers.Quote(column.PhysicalName)}"));
            sql.Append(
                $" LEFT JOIN (SELECT {DuckDbIdentifiers.RowIndexColumn}{blockColumns} FROM {DuckDbIdentifiers.Quote(tableName)}" +
                $" WHERE {DuckDbIdentifiers.RowIndexColumn} >= $start AND {DuckDbIdentifiers.RowIndexColumn} < $end) {alias[blockId]}" +
                $" ON {alias[blockId]}.{DuckDbIdentifiers.RowIndexColumn} = w.{DuckDbIdentifiers.RowIndexColumn}");
        }

        sql.Append($" ORDER BY w.{DuckDbIdentifiers.RowIndexColumn}");

        using var command = DuckDbRawCatalog.CreateCommand(
            connection, sql.ToString(), ("start", rowOffset), ("end", rowOffset + resultRowCount));
        using var reader = command.ExecuteReader();

        var rowIndex = 0;
        while (reader.Read())
        {
            if (rowIndex % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (rowIndex >= resultRowCount || reader.GetInt64(0) != rowOffset + rowIndex)
            {
                throw new RawDataStorageException("The raw data window query returned rows out of sequence.");
            }

            for (var columnIndex = 0; columnIndex < requested.Length; columnIndex++)
            {
                var ordinal = columnIndex + 1;
                switch (values[columnIndex])
                {
                    case double?[] numbers:
                        numbers[rowIndex] = reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
                        break;
                    case string?[] texts:
                        texts[rowIndex] = reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
                        break;
                }
            }

            rowIndex++;
        }

        if (rowIndex != resultRowCount)
        {
            throw new RawDataStorageException("The raw data window query returned an unexpected number of rows.");
        }
    }

    private void Execute(Action<DuckDBConnection> operation) =>
        Execute(connection =>
        {
            operation(connection);
            return true;
        });

    private T Execute<T>(Func<DuckDBConnection, T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            try
            {
                return operation(GetOpenConnection());
            }
            catch (DuckDBException exception)
            {
                throw new RawDataStorageException($"The raw data storage operation failed: {exception.Message}", exception);
            }
        }
    }

    private DuckDBConnection GetOpenConnection()
    {
        if (_connection is not null)
        {
            return _connection;
        }

        var connection = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = _settings.DataSource }.ConnectionString);
        try
        {
            connection.Open();
            ApplyResourceLimits(connection);
            DuckDbRawCatalog.Initialize(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
        return connection;
    }

    private void ApplyResourceLimits(DuckDBConnection connection)
    {
        var memoryLimitKib = (_settings.EffectiveMemoryLimitBytes / 1024).ToString(CultureInfo.InvariantCulture);
        var threads = _settings.EffectiveThreads.ToString(CultureInfo.InvariantCulture);
        DuckDbRawCatalog.Execute(connection, $"SET memory_limit = '{memoryLimitKib}KiB'");
        DuckDbRawCatalog.Execute(connection, $"SET threads = {threads}");
    }
}

internal enum DuckDbRawWritePhase
{
    AfterAppend,
    AfterCatalogUpdate,
    AfterCleanup
}
