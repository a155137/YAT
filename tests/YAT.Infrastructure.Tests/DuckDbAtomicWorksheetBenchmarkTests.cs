using System.Diagnostics;
using System.Globalization;
using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests;

// Streaming performance benchmarks for FT-02A atomic worksheet creation. Explicit - run directly:
//
//     YAT.Infrastructure.Tests.exe -explicit only -class YAT.Infrastructure.Tests.DuckDbAtomicWorksheetBenchmarkTests
//
// Uses default DuckDB storage configuration without global memory/storage overrides and generates streaming rows
// on the fly (never materializing a complete in-memory source dataset).
public sealed class DuckDbAtomicWorksheetBenchmarkTests : IDisposable
{
    private readonly string _tempRoot;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DuckDbAtomicWorksheetBenchmarkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "yat-ft02a-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
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

    private static long FileSizeOrZero(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private async Task RunStreamingScenarioAsync(string label, int rowCount, int numericColumnCount)
    {
        var storageRoot = Path.Combine(_tempRoot, label);
        var projectPath = Path.Combine(_tempRoot, $"{label}.yat");
        var storage = new DuckDbProjectStorage(storageRoot);
        var tp = TimeProvider.System;

        var columns = new List<WorksheetColumnDefinition>(4 + numericColumnCount)
        {
            new("Lot_ID", WorksheetDataType.String),
            new("Bin", WorksheetDataType.String),
            new("Site", WorksheetDataType.String),
            new("Test_Role", WorksheetDataType.String)
        };
        for (var c = 0; c < numericColumnCount; c++)
        {
            columns.Add(new WorksheetColumnDefinition($"T_{c:D2}", WorksheetDataType.Numeric, ColumnSemanticType.TestParameter, "V"));
        }

        using var db = storage.Create(projectPath);
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = label,
            CreatedAt = tp.GetUtcNow(),
            UpdatedAt = tp.GetUtcNow()
        };
        await new DuckDbProjectRepository(db).AddAsync(project, Token);

        var commitWatch = new Stopwatch();
        var creator = new DuckDbAtomicWorksheetCreator(db, tp)
        {
            PhaseHook = phase =>
            {
                if (phase == DuckDbAtomicWorksheetPhase.BeforeCommit)
                {
                    commitWatch.Start();
                }
                else if (phase == DuckDbAtomicWorksheetPhase.AfterCommit)
                {
                    commitWatch.Stop();
                }
            }
        };

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var wsBefore = Process.GetCurrentProcess().WorkingSet64;

        var totalWatch = Stopwatch.StartNew();
        var result = await creator.CreateWorksheetAsync(
            new AtomicWorksheetCreateRequest(project.Id, label, columns),
            writer =>
            {
                for (var r = 0; r < rowCount; r++)
                {
                    writer.BeginRow();
                    writer.WriteString((r & 1) == 0 ? "LOT_A" : "LOT_B");
                    writer.WriteString((r % 20) == 0 ? "002" : "001");
                    if (r % 16 == 0)
                    {
                        writer.WriteNull();
                    }
                    else
                    {
                        writer.WriteString((r & 3) switch { 0 => "S1", 1 => "S2", 2 => "S3", _ => "S4" });
                    }

                    writer.WriteString("Initial");
                    for (var c = 0; c < numericColumnCount; c++)
                    {
                        if (((r + c) & 63) == 0)
                        {
                            writer.WriteNull();
                        }
                        else
                        {
                            writer.WriteDouble((r % 1000) * 0.001 + c * 0.1);
                        }
                    }

                    writer.EndRow();
                }
            },
            Token);
        totalWatch.Stop();

        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);
        var proc = Process.GetCurrentProcess();
        var wsAfter = proc.WorkingSet64;
        var peakWs = proc.PeakWorkingSet64;

        var preCheckpointSize = FileSizeOrZero(projectPath) + FileSizeOrZero(projectPath + ".wal");
        await db.CheckpointAsync(Token);
        var postCheckpointSize = FileSizeOrZero(projectPath) + FileSizeOrZero(projectPath + ".wal");

        Assert.Equal(rowCount, result.Worksheet.RowCount);
        Assert.Equal(columns.Count, result.Worksheet.ColumnCount);

        var rowsPerSec = rowCount / Math.Max(totalWatch.Elapsed.TotalSeconds, 0.0001);
        Report(
            $"[{label}] rows={rowCount:N0}, cols={columns.Count} (4S+{numericColumnCount}N) | " +
            $"elapsed={totalWatch.ElapsedMilliseconds}ms, commit={commitWatch.ElapsedMilliseconds}ms, rate={rowsPerSec:N0} rows/s | " +
            $"managedHeapNetDelta={(heapAfter - heapBefore) / 1024.0 / 1024.0:F2}MB, totalAlloc={(allocatedAfter - allocatedBefore) / 1024.0 / 1024.0:F2}MB, " +
            $"wsDelta={(wsAfter - wsBefore) / 1024.0 / 1024.0:F2}MB, processPeakWS={peakWs / 1024.0 / 1024.0:F2}MB | " +
            $"dbSize(preCheckpoint)={preCheckpointSize / 1024.0 / 1024.0:F2}MB, dbSize(postCheckpoint)={postCheckpointSize / 1024.0 / 1024.0:F2}MB");
    }

    [Fact(Explicit = true)]
    public async Task BenchmarkAllRequiredStreamingScenarios()
    {
        // 1. 100,000 rows x representative mixed columns (4 String + 6 Numeric = 10 columns)
        await RunStreamingScenarioAsync("100k_Mixed10Cols", rowCount: 100_000, numericColumnCount: 6);

        // 2. 1,000,000 rows x representative mixed columns (4 String + 6 Numeric = 10 columns)
        await RunStreamingScenarioAsync("1M_Mixed10Cols", rowCount: 1_000_000, numericColumnCount: 6);

        // 3. Wide Worksheet: 50 Numeric measurement columns + 4 String metadata columns = 54 columns (100,000 rows)
        await RunStreamingScenarioAsync("100k_Wide54Cols", rowCount: 100_000, numericColumnCount: 50);

        // 4. Mid-stream cancellation on a 1,000,000-row input (cancel at 250,000 rows)
        await RunLargeCancellationScenarioAsync();
    }

    private async Task RunLargeCancellationScenarioAsync()
    {
        const int targetRows = 1_000_000;
        const int cancelAtRow = 250_000;
        const int numericColumnCount = 6;

        var storageRoot = Path.Combine(_tempRoot, "cancel_large");
        var projectPath = Path.Combine(_tempRoot, "cancel_large.yat");
        var storage = new DuckDbProjectStorage(storageRoot);
        var tp = TimeProvider.System;

        var columns = new List<WorksheetColumnDefinition>(4 + numericColumnCount)
        {
            new("Lot_ID", WorksheetDataType.String),
            new("Bin", WorksheetDataType.String),
            new("Site", WorksheetDataType.String),
            new("Test_Role", WorksheetDataType.String)
        };
        for (var c = 0; c < numericColumnCount; c++)
        {
            columns.Add(new WorksheetColumnDefinition($"T_{c:D2}", WorksheetDataType.Numeric));
        }

        using var db = storage.Create(projectPath);
        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "CancelBench",
            CreatedAt = tp.GetUtcNow(),
            UpdatedAt = tp.GetUtcNow()
        };
        await new DuckDbProjectRepository(db).AddAsync(project, Token);

        var cancelRequestedAt = new Stopwatch();
        var rollbackWatch = new Stopwatch();
        long cancelDetectedMs = -1;

        var creator = new DuckDbAtomicWorksheetCreator(db, tp)
        {
            PhaseHook = phase =>
            {
                if (phase == DuckDbAtomicWorksheetPhase.BeforeRollback)
                {
                    cancelDetectedMs = cancelRequestedAt.ElapsedMilliseconds;
                    rollbackWatch.Start();
                }
            }
        };

        using var cts = new CancellationTokenSource();
        var rowsSubmitted = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            creator.CreateWorksheetAsync(
                new AtomicWorksheetCreateRequest(project.Id, "CancelledLarge", columns),
                writer =>
                {
                    for (var r = 0; r < targetRows; r++)
                    {
                        if (r == cancelAtRow)
                        {
                            cancelRequestedAt.Start();
                            cts.Cancel();
                        }

                        writer.BeginRow();
                        writer.WriteString("LOT_A");
                        writer.WriteString("001");
                        writer.WriteString("S1");
                        writer.WriteString("Initial");
                        for (var c = 0; c < numericColumnCount; c++)
                        {
                            writer.WriteDouble(r * 0.001 + c);
                        }

                        writer.EndRow();
                        rowsSubmitted++;
                    }
                },
                cts.Token));
        rollbackWatch.Stop();
        cancelRequestedAt.Stop();

        var remaining = db.Execute(conn => (
            Worksheets: Convert.ToInt64(DuckDbRawCatalog.Scalar(conn, "SELECT count(*) FROM worksheet"), CultureInfo.InvariantCulture),
            Columns: Convert.ToInt64(DuckDbRawCatalog.Scalar(conn, "SELECT count(*) FROM worksheet_column"), CultureInfo.InvariantCulture),
            RawBlocks: Convert.ToInt64(DuckDbRawCatalog.Scalar(conn, "SELECT count(*) FROM raw_block"), CultureInfo.InvariantCulture),
            RawCols: Convert.ToInt64(DuckDbRawCatalog.Scalar(conn, "SELECT count(*) FROM raw_column"), CultureInfo.InvariantCulture),
            BlkTables: Convert.ToInt64(DuckDbRawCatalog.Scalar(conn, "SELECT count(*) FROM duckdb_tables() WHERE database_name = current_database() AND schema_name = 'main' AND table_name LIKE 'blk_%'"), CultureInfo.InvariantCulture)
        ));

        Assert.Equal((0L, 0L, 0L, 0L, 0L), remaining);

        Report(
            $"[LargeCancel] target={targetRows:N0}, cancelAt={cancelAtRow:N0}, rowsSubmitted={rowsSubmitted:N0} | " +
            $"cancelLatency={cancelDetectedMs}ms, rollback={rollbackWatch.ElapsedMilliseconds}ms, totalAfterCancel={cancelRequestedAt.ElapsedMilliseconds}ms | " +
            $"remaining(ws={remaining.Worksheets}, cols={remaining.Columns}, blocks={remaining.RawBlocks}, rawCols={remaining.RawCols}, blkTables={remaining.BlkTables})");
    }
}
