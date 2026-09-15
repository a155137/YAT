using System.Globalization;
using System.Runtime.ExceptionServices;
using DuckDB.NET.Data;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;

namespace YAT.Infrastructure.Persistence.DuckDb;

// One open YAT project database (.yat file, or a temporary project). It owns the single DuckDB connection that the
// project's metadata repositories and raw data store share, and serializes their use of it: every operation runs
// alone on the connection. Each repository or raw store operation still commits its own transaction.
//
// Every database has a private working folder under the storage's temporary root: for a temporary project it holds
// the database file itself, for a project file it only receives DuckDB spill files, so no sidecar folder appears
// next to a user's .yat file (a .wal file does exist there while changes are not yet checkpointed).
public sealed class DuckDbProjectDatabase : IProjectDatabase
{
    private const string CopyAlias = "yat_copy";

    // DuckDB database files start with an 8-byte checksum followed by the magic bytes "DUCK".
    private static readonly byte[] DuckDbMagic = "DUCK"u8.ToArray();

    private static readonly string[] CopyCheckedTables = ["project", "worksheet", "worksheet_column", "raw_block", "raw_column"];

    private readonly DuckDbRawDataStoreSettings _settings;
    private readonly string _workingDirectory;
    private readonly Lock _gate = new();
    private DuckDBConnection? _connection;
    private bool _disposed;

    private DuckDbProjectDatabase(DuckDbRawDataStoreSettings settings, bool isTemporary, string workingDirectory)
    {
        _settings = settings;
        IsTemporary = isTemporary;
        _workingDirectory = workingDirectory;
    }

    public string FilePath => _settings.DataSource;

    public bool IsTemporary { get; }

    // Test-only fault injection after a copy has been validated and before it is moved into place (receives the staging
    // file path). Always null in production.
    internal Action<string>? BeforeCopyMoveHook { get; set; }

    // Creates a new database file with the version 1 YAT schema (metadata and raw catalog) and keeps it open.
    // The file must not exist; a partially created file is removed again when creation fails.
    internal static DuckDbProjectDatabase Create(DuckDbRawDataStoreSettings settings, bool isTemporary, string workingDirectory)
    {
        var filePath = settings.DataSource;
        if (File.Exists(filePath) || Directory.Exists(filePath))
        {
            throw new ProjectStorageException(ProjectStorageError.DestinationExists, "A file with this name already exists.");
        }

        if (!Directory.Exists(Path.GetDirectoryName(filePath)))
        {
            throw new ProjectStorageException(ProjectStorageError.CannotOpen, "The folder for the project file does not exist.");
        }

        var database = new DuckDbProjectDatabase(settings, isTemporary, workingDirectory);
        try
        {
            database.OpenConnection(initialize: true);
            return database;
        }
        catch (Exception exception)
        {
            database.Dispose();
            DeleteDatabaseFiles(filePath);
            throw Translate(exception, ProjectStorageError.CannotOpen, "The project file could not be created.");
        }
    }

    // Opens an existing YAT project file. The file is first validated through a read-only connection, so a file that
    // is not a YAT project of a supported version is never modified.
    internal static DuckDbProjectDatabase Open(DuckDbRawDataStoreSettings settings, string workingDirectory)
    {
        var filePath = settings.DataSource;
        if (!File.Exists(filePath))
        {
            throw new ProjectStorageException(ProjectStorageError.CannotOpen, "The project file does not exist.");
        }

        if (!HasDuckDbHeader(filePath))
        {
            throw new ProjectStorageException(ProjectStorageError.NotAYatProject, "The file is not a YAT project.");
        }

        try
        {
            using var validation = DuckDbConnections.Open(filePath, readOnly: true);
            DuckDbProjectSchema.Validate(validation);
        }
        catch (DuckDBException exception)
        {
            throw new ProjectStorageException(ProjectStorageError.CannotOpen, "The project file could not be opened.", exception);
        }

        var database = new DuckDbProjectDatabase(settings, isTemporary: false, workingDirectory);
        try
        {
            database.OpenConnection(initialize: false);
            return database;
        }
        catch (Exception exception)
        {
            database.Dispose();
            throw Translate(exception, ProjectStorageError.CannotOpen, "The project file could not be opened.");
        }
    }

    public Task CheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Execute(connection =>
            {
                RunStorageOperation(() => DuckDbRawCatalog.Execute(connection, "CHECKPOINT"));
                return true;
            });
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

    // Copy sequence: checkpoint → ATTACH a staging file next to the destination → COPY FROM DATABASE → DETACH →
    // validate the staging file through its own read-only connection (schema, and table row counts against this
    // database) → move it to the destination without overwriting. Copying the open file itself is not possible on
    // Windows, so the copy is made by DuckDB from the live database. On failure the staging file is deleted and this
    // database is left open and unchanged.
    public Task SaveCopyAsync(string destinationPath, CancellationToken cancellationToken)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            cancellationToken.ThrowIfCancellationRequested();
            SaveCopy(Path.GetFullPath(destinationPath));
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

    public void DeleteTemporaryStorage()
    {
        if (!IsTemporary)
        {
            throw new InvalidOperationException("Only the storage of a temporary project can be deleted.");
        }

        lock (_gate)
        {
            if (!_disposed)
            {
                throw new InvalidOperationException("A temporary project must be disposed before its storage is deleted.");
            }
        }

        if (Directory.Exists(_workingDirectory))
        {
            Directory.Delete(_workingDirectory, recursive: true);
        }
    }

    // Closes the database file. A project file's working folder (spill files only) is removed; a temporary project's
    // folder, which holds its database, stays until DeleteTemporaryStorage.
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

        if (!IsTemporary)
        {
            TryDeleteDirectory(_workingDirectory);
        }
    }

    // Runs an operation alone on the shared connection. DuckDB exceptions are left to the caller to translate.
    internal T Execute<T>(Func<DuckDBConnection, T> operation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return operation(_connection!);
        }
    }

    internal static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OpenConnection(bool initialize)
    {
        var connection = DuckDbConnections.Open(FilePath);
        try
        {
            DuckDbConnections.ApplyResourceLimits(connection, _settings);
            DuckDbRawCatalog.Execute(connection, $"SET temp_directory = {DuckDbConnections.Literal(Path.Combine(_workingDirectory, "spill"))}");

            if (initialize)
            {
                DuckDbProjectSchema.Initialize(connection);
            }
            else
            {
                DuckDbProjectSchema.Validate(connection);
            }

            DuckDbRawCatalog.Initialize(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }

        _connection = connection;
    }

    private void SaveCopy(string destination)
    {
        if (string.Equals(destination, FilePath, StringComparison.OrdinalIgnoreCase) || File.Exists(destination) || Directory.Exists(destination))
        {
            throw new ProjectStorageException(ProjectStorageError.DestinationExists, "A file with this name already exists.");
        }

        var directory = Path.GetDirectoryName(destination);
        if (directory is null || !Directory.Exists(directory))
        {
            throw new ProjectStorageException(ProjectStorageError.CopyFailed, "The destination folder does not exist.");
        }

        var staging = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            var sourceCounts = Execute(connection => CopyInto(connection, staging));
            ValidateCopy(staging, sourceCounts);
            BeforeCopyMoveHook?.Invoke(staging);
            File.Move(staging, destination, overwrite: false);
        }
        catch (Exception exception)
        {
            DeleteDatabaseFiles(staging);
            if (exception is IOException && File.Exists(destination))
            {
                throw new ProjectStorageException(ProjectStorageError.DestinationExists, "A file with this name already exists.", exception);
            }

            // Any other failure, including a copy that does not validate, is a failed copy.
            throw exception is ProjectStorageException { Error: ProjectStorageError.DestinationExists } or ObjectDisposedException
                ? exception
                : new ProjectStorageException(ProjectStorageError.CopyFailed, "The project could not be copied.", exception);
        }
    }

    // Returns the row counts of the checked tables at the time of the copy.
    private static Dictionary<string, long> CopyInto(DuckDBConnection connection, string staging)
    {
        DuckDbRawCatalog.Execute(connection, "CHECKPOINT");
        var sourceName = Convert.ToString(DuckDbRawCatalog.Scalar(connection, "SELECT current_database()"), CultureInfo.InvariantCulture)!;
        var sourceCounts = CountRows(connection);

        DuckDbRawCatalog.Execute(connection, $"ATTACH {DuckDbConnections.Literal(staging)} AS {CopyAlias}");
        Exception? failure = null;
        try
        {
            DuckDbRawCatalog.Execute(connection, $"COPY FROM DATABASE {DuckDbConnections.QuoteIdentifier(sourceName)} TO {CopyAlias}");
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            DuckDbRawCatalog.Execute(connection, $"DETACH {CopyAlias}");
        }
        catch (Exception) when (failure is not null)
        {
            // The copy failure is the one reported.
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Throw(failure);
        }

        return sourceCounts;
    }

    private static void ValidateCopy(string staging, Dictionary<string, long> sourceCounts)
    {
        using var connection = DuckDbConnections.Open(staging, readOnly: true);
        DuckDbProjectSchema.Validate(connection);

        var copyCounts = CountRows(connection);
        if (CopyCheckedTables.Any(table => copyCounts[table] != sourceCounts[table]))
        {
            throw new ProjectStorageException(ProjectStorageError.CopyFailed, "The project copy is incomplete.");
        }
    }

    private static Dictionary<string, long> CountRows(DuckDBConnection connection) =>
        CopyCheckedTables.ToDictionary(
            table => table,
            table => Convert.ToInt64(DuckDbRawCatalog.Scalar(connection, $"SELECT count(*) FROM {table}"), CultureInfo.InvariantCulture));

    private static T RunStorageOperation<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (DuckDBException exception)
        {
            throw new ProjectStorageException(ProjectStorageError.StorageFailure, "The project storage operation failed.", exception);
        }
    }

    private static void RunStorageOperation(Action operation) => RunStorageOperation(() =>
    {
        operation();
        return true;
    });

    private static bool HasDuckDbHeader(string filePath)
    {
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var header = new byte[12];
            return stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length
                && header.AsSpan(8, 4).SequenceEqual(DuckDbMagic);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new ProjectStorageException(ProjectStorageError.CannotOpen, "The project file could not be read.", exception);
        }
    }

    private static Exception Translate(Exception exception, ProjectStorageError error, string message) => exception switch
    {
        ProjectStorageException => exception,
        DuckDBException or IOException or UnauthorizedAccessException or RawDataStorageException =>
            new ProjectStorageException(error, message, exception),
        _ => exception
    };

    private static void DeleteDatabaseFiles(string filePath)
    {
        foreach (var path in new[] { filePath, filePath + ".wal" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
