using YAT.Application.Exceptions;

namespace YAT.Infrastructure.Persistence.DuckDb;

// Creates and opens YAT project databases. Temporary projects and the working folders of project files live under
// TemporaryRoot (by default %TEMP%\YAT\projects), one uniquely named folder per database; nothing is ever created in
// the application or repository folders.
public sealed class DuckDbProjectStorage
{
    public const string TemporaryDatabaseFileName = "project.duckdb";

    private readonly long? _memoryLimitBytes;
    private readonly int? _threads;

    public DuckDbProjectStorage(string temporaryRoot, long? memoryLimitBytes = null, int? threads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(temporaryRoot);

        // Validates the limits once, with the same rules as the raw data store.
        _ = new DuckDbRawDataStoreSettings(temporaryRoot, memoryLimitBytes, threads);

        TemporaryRoot = Path.GetFullPath(temporaryRoot);
        _memoryLimitBytes = memoryLimitBytes;
        _threads = threads;
    }

    public static string DefaultTemporaryRoot => Path.Combine(Path.GetTempPath(), "YAT", "projects");

    public string TemporaryRoot { get; }

    // A new temporary project database: <TemporaryRoot>\<id>\project.duckdb.
    public DuckDbProjectDatabase CreateTemporary()
    {
        var workingDirectory = CreateWorkingDirectory();
        try
        {
            return DuckDbProjectDatabase.Create(
                Settings(Path.Combine(workingDirectory, TemporaryDatabaseFileName)), isTemporary: true, workingDirectory);
        }
        catch
        {
            DuckDbProjectDatabase.TryDeleteDirectory(workingDirectory);
            throw;
        }
    }

    // A new project file at filePath, which must not exist.
    public DuckDbProjectDatabase Create(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        var workingDirectory = CreateWorkingDirectory();
        try
        {
            return DuckDbProjectDatabase.Create(Settings(fullPath), isTemporary: false, workingDirectory);
        }
        catch
        {
            DuckDbProjectDatabase.TryDeleteDirectory(workingDirectory);
            throw;
        }
    }

    // An existing YAT project file (ProjectStorageException when it is missing, not a YAT project or of a newer version).
    public DuckDbProjectDatabase Open(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);

        var workingDirectory = CreateWorkingDirectory();
        try
        {
            return DuckDbProjectDatabase.Open(Settings(fullPath), workingDirectory);
        }
        catch
        {
            DuckDbProjectDatabase.TryDeleteDirectory(workingDirectory);
            throw;
        }
    }

    private DuckDbRawDataStoreSettings Settings(string filePath) => new(filePath, _memoryLimitBytes, _threads);

    private string CreateWorkingDirectory()
    {
        try
        {
            return Directory.CreateDirectory(Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"))).FullName;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectStorageException(ProjectStorageError.CannotOpen, "Temporary project storage could not be created.", exception);
        }
    }
}
