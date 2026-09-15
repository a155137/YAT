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

    private (string Name, object? Value)[] Parameters(WorksheetColumn column) =>
    [
        ("id", column.Id),
        ("worksheet", column.WorksheetId),
        ("index", column.Index),
        ("name", column.Name),
        ("type", column.DataType.ToString()),
        ("semantic", column.SemanticType?.ToString()),
        ("unit", column.Unit),
        ("now", DuckDbMetadata.ToStoredTimestamp(_timeProvider.GetUtcNow()))
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
