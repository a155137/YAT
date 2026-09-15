using System.Globalization;
using DuckDB.NET.Data;

namespace YAT.Infrastructure.Persistence.DuckDb;

internal static class DuckDbConnections
{
    public static DuckDBConnection Open(string dataSource, bool readOnly = false)
    {
        var builder = new DuckDBConnectionStringBuilder { DataSource = dataSource };
        if (readOnly)
        {
            builder["ACCESS_MODE"] = "READ_ONLY";
        }

        var connection = new DuckDBConnection(builder.ConnectionString);
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static void ApplyResourceLimits(DuckDBConnection connection, DuckDbRawDataStoreSettings settings)
    {
        var memoryLimitKib = (settings.EffectiveMemoryLimitBytes / 1024).ToString(CultureInfo.InvariantCulture);
        var threads = settings.EffectiveThreads.ToString(CultureInfo.InvariantCulture);
        DuckDbRawCatalog.Execute(connection, $"SET memory_limit = '{memoryLimitKib}KiB'");
        DuckDbRawCatalog.Execute(connection, $"SET threads = {threads}");
    }

    // A string literal for SQL statements that take no parameters (e.g. ATTACH, SET temp_directory).
    public static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    public static string QuoteIdentifier(string name) => $"\"{name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
