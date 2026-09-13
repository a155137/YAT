using DuckDB.NET.Data;

namespace YAT.Infrastructure.Tests.TestDoubles;

// A DuckDB file in a unique folder under the system temp directory, deleted (with .wal/.tmp) on dispose.
// Stores using the file must be disposed first.
internal sealed class TemporaryDatabaseFile : IDisposable
{
    private readonly string _directory;

    public TemporaryDatabaseFile()
    {
        _directory = Path.Combine(Path.GetTempPath(), "yat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        FilePath = Path.Combine(_directory, "data.duckdb");
    }

    public string FilePath { get; }

    // Opens a separate inspection connection. Only valid while no store holds the file open.
    public T Inspect<T>(Func<DuckDBConnection, T> query)
    {
        using var connection = new DuckDBConnection(new DuckDBConnectionStringBuilder { DataSource = FilePath }.ConnectionString);
        connection.Open();
        return query(connection);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
