using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Infrastructure.Persistence.DuckDb;

// The project of a YAT project file. A file holds exactly one project: adding a project with a different Id is
// rejected. Reads return new instances built from the stored row.
public sealed class DuckDbProjectRepository : IProjectRepository
{
    private const string SelectProjects = "SELECT id, name, description, created_at, updated_at FROM project";

    private readonly DuckDbProjectDatabase _database;

    public DuckDbProjectRepository(DuckDbProjectDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public Task<Project?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
            DuckDbMetadata.Query(connection, $"{SelectProjects} WHERE id = $id", Read, ("id", id)).SingleOrDefault(),
            cancellationToken);

    // The file's project, or null while a new file has none yet.
    public Task<Project?> GetFileProjectAsync(CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            var projects = DuckDbMetadata.Query(connection, SelectProjects, Read);
            return projects.Count <= 1
                ? projects.SingleOrDefault()
                : throw new ProjectStorageException(ProjectStorageError.NotAYatProject, "The project file contains more than one project.");
        }, cancellationToken);

    // Adding the project that is already stored (same Id) replaces it, as the other repositories do.
    public Task AddAsync(Project project, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            ArgumentNullException.ThrowIfNull(project);
            DuckDbMetadata.InTransaction(connection, () =>
            {
                var otherProjects = DuckDbMetadata.Query(
                    connection, "SELECT id FROM project WHERE id <> $id LIMIT 1", reader => reader.GetGuid(0), ("id", project.Id));
                if (otherProjects.Count > 0)
                {
                    throw new ProjectStorageException(ProjectStorageError.ProjectAlreadyExists, "A project file can contain only one project.");
                }

                return DuckDbMetadata.Execute(connection,
                    "INSERT INTO project VALUES ($id, $name, $description, $created, $updated) " +
                    "ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, description = EXCLUDED.description, " +
                    "created_at = EXCLUDED.created_at, updated_at = EXCLUDED.updated_at",
                    Parameters(project));
            });
        }, cancellationToken);

    public Task UpdateAsync(Project project, CancellationToken cancellationToken) =>
        DuckDbMetadata.RunAsync(_database, connection =>
        {
            ArgumentNullException.ThrowIfNull(project);
            var updated = DuckDbMetadata.Execute(connection,
                "UPDATE project SET name = $name, description = $description, created_at = $created, updated_at = $updated WHERE id = $id",
                Parameters(project));
            if (updated == 0)
            {
                throw new EntityNotFoundException(nameof(Project), project.Id);
            }
        }, cancellationToken);

    private static (string Name, object? Value)[] Parameters(Project project) =>
    [
        ("id", project.Id),
        ("name", project.Name),
        ("description", project.Description),
        ("created", DuckDbMetadata.ToStoredTimestamp(project.CreatedAt)),
        ("updated", DuckDbMetadata.ToStoredTimestamp(project.UpdatedAt))
    ];

    private static Project Read(DuckDBDataReader reader) => new()
    {
        Id = reader.GetGuid(0),
        Name = reader.GetString(1),
        Description = DuckDbMetadata.ReadNullableString(reader, 2),
        CreatedAt = DuckDbMetadata.ReadTimestamp(reader, 3),
        UpdatedAt = DuckDbMetadata.ReadTimestamp(reader, 4)
    };
}
