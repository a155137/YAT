using System.Diagnostics;
using YAT.Application.FtImport;

namespace YAT.Application.Tests;

// Performance benchmarks for FT-03.1 file discovery, filename identification, grouping, and import plan creation.
// Explicit - run directly:
//
//     YAT.Application.Tests.exe -explicit only -class YAT.Application.Tests.FtFileDiscoveryBenchmarkTests
public sealed class FtFileDiscoveryBenchmarkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FtFileDiscoveryService _service = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FtFileDiscoveryBenchmarkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft03-1-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        Console.WriteLine(line);
    }

    private void RunScaleScenario(int fileCount)
    {
        var dir = Path.Combine(_tempDir, $"scale_{fileCount}");
        Directory.CreateDirectory(dir);
        var profile = FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile();

        // Generate representative filenames across multiple lots, original + RT1/RT2, multiple parts (0-byte files).
        var lotCount = Math.Max(1, fileCount / 10);
        for (var i = 0; i < fileCount; i++)
        {
            var lotIdx = i % lotCount;
            var slot = i / lotCount; // 0..9 per lot
            var stageSeg = slot switch
            {
                0 or 1 or 2 => "F1",
                3 or 4 or 5 => "F1B45678R1",
                _ => "F1B45678R2"
            };
            var part = slot switch
            {
                0 or 1 or 2 => slot + 1,
                3 or 4 or 5 => slot - 2,
                _ => slot - 5
            };
            var fileName = $"E320BD-262.20261123111219.LOT{lotIdx:D5}_TQB01RXAA1_{stageSeg}_{part}_20261122171516_20261123110911.Reg.zip";
            using var _ = File.Create(Path.Combine(dir, fileName));
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);

        var sw = Stopwatch.StartNew();
        var discovery = _service.DiscoverFolder(profile, dir, Token);
        var discoverMs = sw.ElapsedMilliseconds;

        var selection = new FtImportSelectionModel(discovery);
        var planResult = selection.BuildImportPlan();
        sw.Stop();

        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var allocAfter = GC.GetTotalAllocatedBytes(precise: true);

        Assert.Equal(fileCount, discovery.Files.Count);
        Assert.Empty(discovery.Collisions);
        Assert.True(planResult.IsValid);
        Assert.Equal(fileCount, planResult.Plan!.OrderedFiles.Count);

        Report(
            $"[Scale {fileCount:N0}] lots={discovery.LotGroups.Count:N0}, files={discovery.Files.Count:N0} | " +
            $"discover={discoverMs}ms, total(discover+plan)={sw.ElapsedMilliseconds}ms | " +
            $"managedHeapNetDelta={(heapAfter - heapBefore) / 1024.0 / 1024.0:F2}MB, totalAlloc={(allocAfter - allocBefore) / 1024.0 / 1024.0:F2}MB");
    }

    [Fact(Explicit = true)]
    public void BenchmarkDiscoveryGroupingAndPlanAt100_1000_And10000Files()
    {
        RunScaleScenario(100);
        RunScaleScenario(1_000);
        RunScaleScenario(10_000);

        // Cancellation check on 10,000-file directory.
        var dir10k = Path.Combine(_tempDir, "scale_10000");
        var profile = FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var swCancel = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => _service.DiscoverFolder(profile, dir10k, cts.Token));
        swCancel.Stop();
        Report($"[Cancel 10,000] cancelled cleanly in {swCancel.ElapsedMilliseconds}ms");
    }
}
