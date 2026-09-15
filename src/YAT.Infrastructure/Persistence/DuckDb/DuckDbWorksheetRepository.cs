using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Infrastructure.Persistence.DuckDb;

// Worksheets of a YAT project file. Order is kept by the persistence-only position column: the first add of a worksheet
// places it after the project's last worksheet, and neither a repeated add nor an update moves it. RowCount and
// ColumnCount are stored exactly as the entity carries them; the raw catalog still defines which raw data exists.
public sealed class DuckDbWorksheetRepository : IWorksheetRepository
{
    private const string SelectWorksheets =
        "SELECT id, project_id, name, row_count, column_count, created_at, updated_at FROM worksheet";

    private readonly DuckDbProjectDatabase _database;

    public DuckDbWorksheetRepository(DuckDbProjectDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<Worksheet?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
            DuckDbMetadata.Query(connection, $"{SelectWorksheets} WHERE id = $id", Read, ("id", id)).SingleOrDefault(),
            cancellationToken);

    public Task<IReadOnlyList<Worksheet>> GetByProjectIdAsync(Guid projectId, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync<IReadOnlyList<Worksheet>>(_database, connection =>
            DuckDbMetadata.Query(
                connection, $"{SelectWorksheets} WHERE project_id = $project ORDER BY position, id", Read, ("project", projectId)),
            cancellationToken);

    public Task AddAsync(Worksheet worksheet, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            ArgumentNullException.ThrowIfNull(worksheet);
            DuckDbMetadata.Execute(connection,
                "INSERT INTO worksheet (id, project_id, name, position, row_count, column_count, created_at, updated_at) " +
                "SELECT $id, $project, $name, COALESCE(MAX(position) + 1, 0), $rows, $columns, $created, $updated " +
                "FROM worksheet WHERE project_id = $project " +
                "ON CONFLICT (id) DO UPDATE SET project_id = EXCLUDED.project_id, name = EXCLUDED.name, " +
                "row_count = EXCLUDED.row_count, column_count = EXCLUDED.column_count, " +
                "created_at = EXCLUDED.created_at, updated_at = EXCLUDED.updated_at",
                Parameters(worksheet));
        }, cancellationToken);

    public Task UpdateAsync(Worksheet worksheet, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            ArgumentNullException.ThrowIfNull(worksheet);
            var updated = DuckDbMetadata.Execute(connection,
                "UPDATE worksheet SET project_id = $project, name = $name, row_count = $rows, column_count = $columns, " +
                "created_at = $created, updated_at = $updated WHERE id = $id",
                Parameters(worksheet));
            if (updated == 0)
            {
                throw new EntityNotFoundException(nameof(Worksheet), worksheet.Id);
            }
        }, cancellationToken);

    private static (string Name, object? Value)[] Parameters(Worksheet worksheet) =>
    [
        ("id", worksheet.Id),
        ("project", worksheet.ProjectId),
        ("name", worksheet.Name),
        ("rows", worksheet.RowCount),
        ("columns", worksheet.ColumnCount),
        ("created", DuckDbMetadata.ToStoredTimestamp(worksheet.CreatedAt)),
        ("updated", DuckDbMetadata.ToStoredTimestamp(worksheet.UpdatedAt))
    ];

    private static Worksheet Read(DuckDBDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        ProjectId = reader.GetGuid(1),
        Name = reader.GetString(2),
        RowCount = reader.GetInt64(3),
        ColumnCount = reader.GetInt32(4),
        CreatedAt = DuckDbMetadata.ReadTimestamp(reader, 5),
        UpdatedAt = DuckDbMetadata.ReadTimestamp(reader, 6)
    };
}
