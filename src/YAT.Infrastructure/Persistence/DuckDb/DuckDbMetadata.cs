using System.Globalization;
using DuckDB.NET.Data;
using YAT.Application.Exceptions;

namespace YAT.Infrastructure.Persistence.DuckDb;

// Shared plumbing of the DuckDB metadata repositories: operations run alone on the project's connection, DuckDB
// exceptions become ProjectStorageException, and results are returned as completed tasks.
internal static class DuckDbMetadata
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    public static Task<T> RunAsync<T>(DuckDbProjectDatabase database, Func<DuckDBConnection, T> operation, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(database.Execute(connection =>
            {
                try
                {
                    return operation(connection);
                }
                catch (DuckDBException exception)
                {
                    throw new ProjectStorageException(ProjectStorageError.StorageFailure, "The project storage operation failed.", exception);
                }
            }));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }
        catch (Exception exception)
        {
            return Task.FromException<T>(exception);
        }
    }

    public static Task RunAsync(DuckDbProjectDatabase database, Action<DuckDBConnection> operation, CancellationToken cancellationToken) =>
        RunAsync(database, connection =>
        {
            operation(connection);
            return true;
        }, cancellationToken);

    // Runs the operation in one transaction on the connection.
    public static T InTransaction<T>(DuckDBConnection connection, Func<T> operation)
    {
        using var transaction = connection.BeginTransaction();
        try
        {
            var result = operation();
            transaction.Commit();
            return result;
        }
        catch
        {
            DuckDbTransactions.RollBack(transaction);
            throw;
        }
    }

    public static List<T> Query<T>(
        DuckDBConnection connection,
        string sql,
        Func<DuckDBDataReader, T> map,
        params (string Name, object? Value)[] parameters)
    {
        using var command = DuckDbRawCatalog.CreateCommand(connection, sql, parameters);
        using var reader = command.ExecuteReader();

        var rows = new List<T>();
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    // Returns the number of rows changed by the statement.
    public static int Execute(DuckDBConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = DuckDbRawCatalog.CreateCommand(connection, sql, parameters);
        return command.ExecuteNonQuery();
    }

    // Timestamps are stored in UTC with DuckDB's microsecond precision. Values are normalized before they are written,
    // so a value read back in the same session equals the value read after reopening the project.
    public static DateTimeOffset ToStoredTimestamp(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % TicksPerMicrosecond, TimeSpan.Zero);

    public static DateTimeOffset ReadTimestamp(DuckDBDataReader reader, int ordinal) =>
        new(reader.GetFieldValue<DateTimeOffset>(ordinal).UtcTicks, TimeSpan.Zero);

    public static string? ReadNullableString(DuckDBDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    // Enum values are stored by name; numbers and unknown names are rejected.
    public static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum =>
        !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
        && Enum.TryParse<TEnum>(value, ignoreCase: false, out var parsed)
        && Enum.IsDefined(parsed)
            ? parsed
            : throw new ProjectStorageException(
                ProjectStorageError.StorageFailure, $"The project file contains an unsupported {typeof(TEnum).Name} value '{value}'.");
}
