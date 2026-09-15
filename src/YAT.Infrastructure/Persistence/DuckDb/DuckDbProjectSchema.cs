using System.Globalization;
using DuckDB.NET.Data;
using YAT.Application.Exceptions;

namespace YAT.Infrastructure.Persistence.DuckDb;

// YAT project file (.yat) schema, version 1. One DuckDB database holds:
//   yat_schema_info(key, value)       format = 'yat.project', schema_version = '1'
//   project(...)                      exactly one row: the file's project
//   worksheet(...)                    position orders a project's worksheets (persistence-only, assigned on first add)
//   worksheet_column(...)             column_index is the worksheet position; created_at/updated_at are storage-managed
//   raw_schema_info, raw_block, raw_column, blk_*   the raw data catalog and blocks (DuckDbRawCatalog)
// As in the raw catalog there are no foreign keys, and name rules (e.g. unique worksheet names) stay in Application.
// Timestamps are TIMESTAMPTZ, stored in UTC with microsecond precision.
internal static class DuckDbProjectSchema
{
    public const int SchemaVersion = 1;

    public const string Format = "yat.project";

    private const string FormatKey = "format";
    private const string SchemaVersionKey = "schema_version";

    private static readonly string[] MetadataTables = ["project", "worksheet", "worksheet_column"];

    // Creates the version 1 metadata schema in a new, empty database.
    public static void Initialize(DuckDBConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            DuckDbRawCatalog.Execute(connection, "CREATE TABLE yat_schema_info (key VARCHAR PRIMARY KEY, value VARCHAR NOT NULL)");
            DuckDbRawCatalog.Execute(connection, "INSERT INTO yat_schema_info VALUES ($key, $value)", ("key", FormatKey), ("value", Format));
            DuckDbRawCatalog.Execute(connection, "INSERT INTO yat_schema_info VALUES ($key, $value)",
                ("key", SchemaVersionKey), ("value", SchemaVersion.ToString(CultureInfo.InvariantCulture)));

            DuckDbRawCatalog.Execute(connection,
                "CREATE TABLE project (id UUID PRIMARY KEY, name VARCHAR NOT NULL, description VARCHAR, " +
                "created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL)");
            DuckDbRawCatalog.Execute(connection,
                "CREATE TABLE worksheet (id UUID PRIMARY KEY, project_id UUID NOT NULL, name VARCHAR NOT NULL, position INTEGER NOT NULL, " +
                "row_count BIGINT NOT NULL, column_count INTEGER NOT NULL, created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL)");
            DuckDbRawCatalog.Execute(connection,
                "CREATE TABLE worksheet_column (id UUID PRIMARY KEY, worksheet_id UUID NOT NULL, column_index INTEGER NOT NULL, " +
                "name VARCHAR NOT NULL, data_type VARCHAR NOT NULL, semantic_type VARCHAR, unit VARCHAR, " +
                "created_at TIMESTAMPTZ NOT NULL, updated_at TIMESTAMPTZ NOT NULL)");

            transaction.Commit();
        }
        catch
        {
            DuckDbTransactions.RollBack(transaction);
            throw;
        }
    }

    // Accepts only a database carrying YAT project metadata of a supported version. Never modifies the database.
    public static void Validate(DuckDBConnection connection)
    {
        if (!TableExists(connection, "yat_schema_info"))
        {
            throw NotAYatProject("The file does not contain YAT project metadata.");
        }

        var format = ReadInfo(connection, FormatKey);
        if (!string.Equals(format, Format, StringComparison.Ordinal))
        {
            throw NotAYatProject("The file does not contain YAT project metadata.");
        }

        var versionText = ReadInfo(connection, SchemaVersionKey);
        if (!int.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version) || version < 1)
        {
            throw NotAYatProject("The YAT project file has a missing or invalid schema version.");
        }

        if (version > SchemaVersion)
        {
            throw new ProjectStorageException(
                ProjectStorageError.UnsupportedSchemaVersion,
                $"The project was saved by a newer version of YAT (project format version {version}); this version supports version {SchemaVersion}.");
        }

        if (MetadataTables.Any(table => !TableExists(connection, table)))
        {
            throw NotAYatProject("The YAT project file is incomplete.");
        }
    }

    private static bool TableExists(DuckDBConnection connection, string table) =>
        Convert.ToInt64(DuckDbRawCatalog.Scalar(connection,
            "SELECT count(*) FROM duckdb_tables() WHERE database_name = current_database() AND schema_name = 'main' AND table_name = $table",
            ("table", table)), CultureInfo.InvariantCulture) > 0;

    private static string? ReadInfo(DuckDBConnection connection, string key) =>
        Convert.ToString(DuckDbRawCatalog.Scalar(connection, "SELECT value FROM yat_schema_info WHERE key = $key", ("key", key)),
            CultureInfo.InvariantCulture);

    private static ProjectStorageException NotAYatProject(string message) => new(ProjectStorageError.NotAYatProject, message);
}
