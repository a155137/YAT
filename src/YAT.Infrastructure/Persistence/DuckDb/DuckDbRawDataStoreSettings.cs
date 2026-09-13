namespace YAT.Infrastructure.Persistence.DuckDb;

// Database location and resource limits for the DuckDB raw data store. Limits left unset fall back to
// conservative desktop defaults instead of DuckDB's own (80% of RAM, all logical cores).
public sealed class DuckDbRawDataStoreSettings
{
    internal const long MinimumMemoryLimitBytes = 1024L * 1024;
    internal const long MaxDefaultMemoryLimitBytes = 4L * 1024 * 1024 * 1024;
    internal const int MaxDefaultThreads = 4;

    public DuckDbRawDataStoreSettings(string dataSource, long? memoryLimitBytes = null, int? threads = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataSource);

        if (memoryLimitBytes < MinimumMemoryLimitBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryLimitBytes), memoryLimitBytes, "Memory limit must be at least 1 MiB.");
        }

        if (threads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threads), threads, "Thread count must be at least 1.");
        }

        DataSource = dataSource;
        MemoryLimitBytes = memoryLimitBytes;
        Threads = threads;
    }

    // A database file path, or ":memory:" for a private in-memory database.
    public string DataSource { get; }

    public long? MemoryLimitBytes { get; }

    public int? Threads { get; }

    internal long EffectiveMemoryLimitBytes =>
        MemoryLimitBytes ?? DefaultMemoryLimitBytes(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);

    internal int EffectiveThreads => Threads ?? DefaultThreads(Environment.ProcessorCount);

    // 25% of the memory available to the process, capped at 4 GiB.
    internal static long DefaultMemoryLimitBytes(long availableMemoryBytes) =>
        availableMemoryBytes <= 0
            ? MaxDefaultMemoryLimitBytes
            : Math.Clamp(availableMemoryBytes / 4, MinimumMemoryLimitBytes, MaxDefaultMemoryLimitBytes);

    // Half the logical cores, clamped to 1–4.
    internal static int DefaultThreads(int processorCount) => Math.Clamp(processorCount / 2, 1, MaxDefaultThreads);
}
