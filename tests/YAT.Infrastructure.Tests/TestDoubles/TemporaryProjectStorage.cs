using DuckDB.NET.Data;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests.TestDoubles;

// A unique test folder with a DuckDbProjectStorage whose temporary root lies inside it, so tests can inspect and clean
// up everything they create. Project databases must be disposed before the folder is.
internal sealed class TemporaryProjectStorage : IDisposable
{
    public TemporaryProjectStorage()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "yat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        TemporaryRoot = Path.Combine(DirectoryPath, "temp");
        Storage = new DuckDbProjectStorage(TemporaryRoot, memoryLimitBytes: 256L * 1024 * 1024, threads: 1);
    }

    public string DirectoryPath { get; }

    public string TemporaryRoot { get; }

    public DuckDbProjectStorage Storage { get; }

    public string File(string name) => Path.Combine(DirectoryPath, name);

    // Folders currently under the temporary root (one per database created or opened and not yet cleaned up).
    public string[] TemporaryFolders() =>
        Directory.Exists(TemporaryRoot) ? Directory.GetDirectories(TemporaryRoot) : [];

    // Runs a query through a separate connection. Only valid while no project database holds the file open.
    public static T Inspect<T>(string filePath, Func<DuckDBConnection, T> query)
    {
        using var connection = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = filePath }.ConnectionString);
        connection.Open();
        return query(connection);
    }

    public static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static object? Scalar(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(DirectoryPath, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
