using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests;

public class DuckDbRawDataStoreSettingsTests
{
    private const long MiB = 1024L * 1024;
    private const long GiB = 1024L * MiB;

    [Theory]
    [InlineData(64 * GiB, 4 * GiB)]
    [InlineData(16 * GiB, 4 * GiB)]
    [InlineData(8 * GiB, 2 * GiB)]
    [InlineData(2 * GiB, GiB / 2)]
    [InlineData(2 * MiB, MiB)]
    [InlineData(0L, 4 * GiB)]
    public void DefaultMemoryLimitIsAQuarterOfAvailableMemoryCappedAtFourGiB(long availableBytes, long expectedBytes)
    {
        Assert.Equal(expectedBytes, DuckDbRawDataStoreSettings.DefaultMemoryLimitBytes(availableBytes));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(6, 3)]
    [InlineData(8, 4)]
    [InlineData(32, 4)]
    public void DefaultThreadsAreHalfTheLogicalCoresClampedToOneToFour(int processorCount, int expectedThreads)
    {
        Assert.Equal(expectedThreads, DuckDbRawDataStoreSettings.DefaultThreads(processorCount));
    }

    [Fact]
    public void ExplicitLimitsOverrideTheDefaults()
    {
        var settings = new DuckDbRawDataStoreSettings("data.duckdb", memoryLimitBytes: 512 * MiB, threads: 3);

        Assert.Equal(512 * MiB, settings.EffectiveMemoryLimitBytes);
        Assert.Equal(3, settings.EffectiveThreads);
    }

    [Fact]
    public void UnsetLimitsUseTheDefaults()
    {
        var settings = new DuckDbRawDataStoreSettings("data.duckdb");

        Assert.InRange(settings.EffectiveMemoryLimitBytes, MiB, 4 * GiB);
        Assert.Equal(DuckDbRawDataStoreSettings.DefaultThreads(Environment.ProcessorCount), settings.EffectiveThreads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RejectsBlankDataSource(string? dataSource)
    {
        Assert.ThrowsAny<ArgumentException>(() => new DuckDbRawDataStoreSettings(dataSource!));
    }

    [Fact]
    public void RejectsInvalidLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DuckDbRawDataStoreSettings("data.duckdb", memoryLimitBytes: MiB - 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DuckDbRawDataStoreSettings("data.duckdb", threads: 0));
    }
}
