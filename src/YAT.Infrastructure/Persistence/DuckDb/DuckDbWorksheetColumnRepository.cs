using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.Infrastructure.Persistence.DuckDb;

// Column metadata of a YAT project file. Id is identity and column_index the worksheet position. created_at and
// updated_at are managed here, because the domain entity has no timestamps.
public sealed class DuckDbWorksheetColumnRepository : IWorksheetColumnRepository
{
    private const string SelectColumns =
        "SELECT id, worksheet_id, column_index, name, data_type, semantic_type, unit FROM worksheet_column";

    private readonly DuckDbProjectDatabase _database;
    private readonly TimeProvider _timeProvider;

    public DuckDbWorksheetColumnRepository(DuckDbProjectDatabase database, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _database = database;
        _timeProvider = timeProvider;
    }

    public Task<IReadOnlyList<WorksheetColumn>> GetByWorksheetIdAsync(Guid worksheetId, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync<IReadOnlyList<WorksheetColumn>>(_database, connection =>
            DuckDbMetadata.Query(
                connection, $"{SelectColumns} WHERE worksheet_id = $worksheet ORDER BY column_index, id", Read, ("worksheet", worksheetId)),
            cancellationToken);

    public Task AddAsync(WorksheetColumn column, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            ArgumentNullException.ThrowIfNull(column);
            DuckDbMetadata.Execute(connection,
                "INSERT INTO worksheet_column VALUES ($id, $worksheet, $index, $name, $type, $semantic, $unit, $now, $now) " +
                "ON CONFLICT (id) DO UPDATE SET worksheet_id = EXCLUDED.worksheet_id, column_index = EXCLUDED.column_index, " +
                "name = EXCLUDED.name, data_type = EXCLUDED.data_type, semantic_type = EXCLUDED.semantic_type, " +
                "unit = EXCLUDED.unit, updated_at = EXCLUDED.updated_at",
                Parameters(column));
        }, cancellationToken);

    public Task UpdateAsync(WorksheetColumn column, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            ArgumentNullException.ThrowIfNull(column);
            var updated = DuckDbMetadata.Execute(connection,
                "UPDATE worksheet_column SET worksheet_id = $worksheet, column_index = $index, name = $name, data_type = $type, " +
                "semantic_type = $semantic, unit = $unit, updated_at = $now WHERE id = $id",
                Parameters(column));
            if (updated == 0)
            {
                throw new EntityNotFoundException(nameof(WorksheetColumn), column.Id);
            }
        }, cancellationToken);

    public Task DeleteAsync(Guid columnId, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            if (DuckDbMetadata.Execute(connection, "DELETE FROM worksheet_column WHERE id = $id", ("id", columnId)) == 0)
            {
                throw new EntityNotFoundException(nameof(WorksheetColumn), columnId);
            }
        }, cancellationToken);

    public Task SaveBatchAsync(
        IReadOnlyList<WorksheetColumn> added,
        IReadOnlyList<WorksheetColumn> updated,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(updated);

        var addedCopy = added.ToArray();
        var updatedCopy = updated.ToArray();
        if (addedCopy.Any(column => column is null))
        {
            throw new ArgumentException("Added columns must not contain null entries.", nameof(added));
        }

        if (updatedCopy.Any(column => column is null))
        {
            throw new ArgumentException("Updated columns must not contain null entries.", nameof(updated));
        }

        if (addedCopy.Length == 0 && updatedCopy.Length == 0)
        {
            return cancellationToken.IsCancellationRequested
                ? Task.FromCanceled(cancellationToken)
                : Task.CompletedTask;
        }

        return DuckDbMetadata.RunAsync(_database, connection => DuckDbMetadata.InTransaction(connection, () =>
        {
            var now = DuckDbMetadata.ToStoredTimestamp(_timeProvider.GetUtcNow());
            if (addedCopy.Length == 1)
            {
                AddSingle(connection, addedCopy[0], now);
            }
            else if (addedCopy.Length > 1)
            {
                AddBatch(connection, addedCopy, now);
            }

            if (updatedCopy.Length == 1)
            {
                UpdateSingle(connection, updatedCopy[0], now);
            }
            else if (updatedCopy.Length > 1)
            {
                UpdateBatch(connection, updatedCopy, now);
            }

            return true;
        }), cancellationToken);
    }

    private static void AddSingle(DuckDBConnection connection, WorksheetColumn column, DateTimeOffset now) =>
        DuckDbMetadata.Execute(connection,
            "INSERT INTO worksheet_column VALUES ($id, $worksheet, $index, $name, $type, $semantic, $unit, $now, $now) " +
            "ON CONFLICT (id) DO UPDATE SET worksheet_id = EXCLUDED.worksheet_id, column_index = EXCLUDED.column_index, " +
            "name = EXCLUDED.name, data_type = EXCLUDED.data_type, semantic_type = EXCLUDED.semantic_type, " +
            "unit = EXCLUDED.unit, updated_at = EXCLUDED.updated_at",
            Parameters(column, now));

    private static void UpdateSingle(DuckDBConnection connection, WorksheetColumn column, DateTimeOffset now)
    {
        var updated = DuckDbMetadata.Execute(connection,
            "UPDATE worksheet_column SET worksheet_id = $worksheet, column_index = $index, name = $name, data_type = $type, " +
            "semantic_type = $semantic, unit = $unit, updated_at = $now WHERE id = $id",
            Parameters(column, now));
        if (updated == 0)
        {
            throw new EntityNotFoundException(nameof(WorksheetColumn), column.Id);
        }
    }

    private static void AddBatch(DuckDBConnection connection, WorksheetColumn[] columns, DateTimeOffset now)
    {
        const string stagingTable = "_yat_col_add_stage";
        DuckDbRawCatalog.Execute(connection, $"DROP TABLE IF EXISTS {stagingTable}");
        DuckDbRawCatalog.Execute(connection,
            $"CREATE TEMP TABLE {stagingTable} (id UUID NOT NULL, worksheet_id UUID NOT NULL, column_index INTEGER NOT NULL, " +
            "name VARCHAR NOT NULL, data_type VARCHAR NOT NULL, semantic_type VARCHAR, unit VARCHAR, " +
            "created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL)");
        try
        {
            DuckDbRawCatalog.AppendStagingRows(connection, stagingTable, columns, (row, column) =>
            {
                row.AppendValue(column.Id)
                    .AppendValue(column.WorksheetId)
                    .AppendValue(column.Index)
                    .AppendValue(column.Name)
                    .AppendValue(column.DataType.ToString());
                if (column.SemanticType is { } semantic)
                {
                    row.AppendValue(semantic.ToString());
                }
                else
                {
                    row.AppendNullValue();
                }

                if (column.Unit is { } unit)
                {
                    row.AppendValue(unit);
                }
                else
                {
                    row.AppendNullValue();
                }

                row.AppendValue(now).AppendValue(now);
            });

            DuckDbRawCatalog.Execute(connection,
                $"INSERT INTO worksheet_column SELECT id, worksheet_id, column_index, name, data_type, semantic_type, unit, created_at, updated_at FROM {stagingTable} " +
                "ON CONFLICT (id) DO UPDATE SET worksheet_id = EXCLUDED.worksheet_id, column_index = EXCLUDED.column_index, " +
                "name = EXCLUDED.name, data_type = EXCLUDED.data_type, semantic_type = EXCLUDED.semantic_type, " +
                "unit = EXCLUDED.unit, updated_at = EXCLUDED.updated_at");
        }
        finally
        {
            DuckDbRawCatalog.TryDropTempTable(connection, stagingTable);
        }
    }

    private static void UpdateBatch(DuckDBConnection connection, WorksheetColumn[] columns, DateTimeOffset now)
    {
        const string stagingTable = "_yat_col_upd_stage";
        DuckDbRawCatalog.Execute(connection, $"DROP TABLE IF EXISTS {stagingTable}");
        DuckDbRawCatalog.Execute(connection,
            $"CREATE TEMP TABLE {stagingTable} (id UUID NOT NULL, worksheet_id UUID NOT NULL, column_index INTEGER NOT NULL, " +
            "name VARCHAR NOT NULL, data_type VARCHAR NOT NULL, semantic_type VARCHAR, unit VARCHAR, updated_at TIMESTAMPTZ NOT NULL)");
        try
        {
            DuckDbRawCatalog.AppendStagingRows(connection, stagingTable, columns, (row, column) =>
            {
                row.AppendValue(column.Id)
                    .AppendValue(column.WorksheetId)
                    .AppendValue(column.Index)
                    .AppendValue(column.Name)
                    .AppendValue(column.DataType.ToString());
                if (column.SemanticType is { } semantic)
                {
                    row.AppendValue(semantic.ToString());
                }
                else
                {
                    row.AppendNullValue();
                }

                if (column.Unit is { } unit)
                {
                    row.AppendValue(unit);
                }
                else
                {
                    row.AppendNullValue();
                }

                row.AppendValue(now);
            });

            var missing = DuckDbRawCatalog.Scalar(connection,
                $"SELECT s.id FROM {stagingTable} s LEFT JOIN worksheet_column w ON w.id = s.id WHERE w.id IS NULL LIMIT 1");
            if (missing is Guid missingId)
            {
                throw new EntityNotFoundException(nameof(WorksheetColumn), missingId);
            }

            DuckDbRawCatalog.Execute(connection,
                "UPDATE worksheet_column SET worksheet_id = s.worksheet_id, column_index = s.column_index, name = s.name, " +
                $"data_type = s.data_type, semantic_type = s.semantic_type, unit = s.unit, updated_at = s.updated_at FROM {stagingTable} s " +
                "WHERE worksheet_column.id = s.id");
        }
        finally
        {
            DuckDbRawCatalog.TryDropTempTable(connection, stagingTable);
        }
    }

    private (string Name, object? Value)[] Parameters(WorksheetColumn column) =>
        Parameters(column, DuckDbMetadata.ToStoredTimestamp(_timeProvider.GetUtcNow()));

    private static (string Name, object? Value)[] Parameters(WorksheetColumn column, DateTimeOffset now) =>
    [
        ("id", column.Id),
        ("worksheet", column.WorksheetId),
        ("index", column.Index),
        ("name", column.Name),
        ("type", column.DataType.ToString()),
        ("semantic", column.SemanticType?.ToString()),
        ("unit", column.Unit),
        ("now", now)
    ];

    private static WorksheetColumn Read(DuckDBDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        WorksheetId = reader.GetGuid(1),
        Index = reader.GetInt32(2),
        Name = reader.GetString(3),
        DataType = DuckDbMetadata.ParseEnum<WorksheetDataType>(reader.GetString(4)),
        SemanticType = reader.IsDBNull(5) ? null : DuckDbMetadata.ParseEnum<ColumnSemanticType>(reader.GetString(5)),
        Unit = DuckDbMetadata.ReadNullableString(reader, 6)
    };
}
