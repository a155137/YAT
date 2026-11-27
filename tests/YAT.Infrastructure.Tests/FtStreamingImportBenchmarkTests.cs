using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.Domain.Entities;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests;

// End-to-end performance benchmarks for FT-03.3 Streaming Import Engine:
//   - ~100 MB plain text (multi-Lot, FT/RT filtering, absent-column mapping, 52 measurement columns)
//   - ZIP equivalent
//   - Mid-stream cancellation and rollback latency
//
// Explicit - run directly:
//     YAT.Infrastructure.Tests.exe -explicit only -class YAT.Infrastructure.Tests.FtStreamingImportBenchmarkTests
public sealed class FtStreamingImportBenchmarkTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly DuckDbProjectStorage _storage;
    private readonly FtFileDiscoveryService _discovery = new();
    private readonly FtSchemaPreparationService _schemaPrep = new();
    private readonly FtStreamingImportService _importer = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FtStreamingImportBenchmarkTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "yat-ft03-3-bench", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        _storage = new DuckDbProjectStorage(Path.Combine(_tempRoot, "storage"));
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

    private static ImportProfile CreateBenchProfile() => new()
    {
        Id = Guid.NewGuid(),
        Name = "FT033_Bench",
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = new FilenameRuleSet
        {
            AcceptedExtensions = [".csv", ".Reg.zip"],
            Mode = FilenameRuleMode.Structured,
            SegmentDelimiters = ['.', '_'],
            LotId = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
            TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.FixedRange, SegmentIndex = 4, StartIndex = 0, Length = 2 },
            Retest = new FilenameRetestRule
            {
                RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 4 },
                RetestMarker = "R",
                RetestRoundField = new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 4, Marker = "R" }
            },
            FilePart = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 5 }
        },
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = ",",
            CompositeFields = [],
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 1 },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 2 },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SOFT_BIN" },
            SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" },
            IgnoredColumns = [new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "UNIT_ID" }]
        },
        BinRules = new BinRuleSet
        {
            GoodBins = ["01"],
            NonRetestFailBins = ["99"]
        },
        MissingValueRules = new MissingValueRuleSet
        {
            Markers = ["", "N/A"],
            Policy = MissingValuePolicy.UseMissing
        }
    };

    private static string NameOf(string lot, string stageSeg, int part, string ext) =>
        $"E320BD-262.20261123111219.{lot}_TQB01RXAA1_{stageSeg}_{part}_20261122171516_20261123110911{ext}";

    private (string PlainDir, string ZipDir, long PlainBytes, long ZipBytes) Generate100MbFixtures()
    {
        var plainDir = Path.Combine(_tempRoot, "plain_100mb");
        var zipDir = Path.Combine(_tempRoot, "zip_100mb");
        Directory.CreateDirectory(plainDir);
        Directory.CreateDirectory(zipDir);

        // 50 cols per file across 2 lots with 2-col offset -> 52 union measurement columns (56 total output columns).
        // Each row ~350 bytes; 300,000 total rows (~105 MB plain text).
        var specs = new (string Lot, string Stage, int Part, int Rows, int ColOffset)[]
        {
            ("LOTA", "F1",         1, 100_000, 0), // filtered (80% retained: 01/99, 20% filtered: 05)
            ("LOTA", "F1B45678R1", 1,  50_000, 0), // latest round -> 100% retained
            ("LOTB", "F1",         1, 100_000, 2), // filtered (80% retained) + absent cols 0,1
            ("LOTB", "F1B45678R1", 1,  50_000, 2)  // latest round -> 100% retained + absent cols 0,1
        };

        var utf8 = new UTF8Encoding(false);
        long plainBytes = 0;
        long zipBytes = 0;

        foreach (var (lot, stage, part, rows, offset) in specs)
        {
            var plainPath = Path.Combine(plainDir, NameOf(lot, stage, part, ".csv"));
            var headerSb = new StringBuilder(600);
            headerSb.Append("UNIT_ID,SOFT_BIN,SITE_NUM");
            for (var c = 0; c < 50; c++)
            {
                headerSb.Append(",T_").Append(c + offset);
            }

            var passRowSb = new StringBuilder(400);
            var failRowSb = new StringBuilder(400);
            passRowSb.Append("1,01,0");
            failRowSb.Append("2,05,1");
            for (var c = 0; c < 50; c++)
            {
                passRowSb.Append(",1.2345");
                failRowSb.Append(",9.8765");
            }

            var headerLine = headerSb.ToString();
            var passLine = passRowSb.ToString();
            var failLine = failRowSb.ToString();

            using (var w = new StreamWriter(plainPath, false, utf8, 65_536))
            {
                w.WriteLine(headerLine);
                for (var r = 0; r < rows; r++)
                {
                    w.WriteLine((r % 5) == 0 ? failLine : passLine);
                }
            }

            plainBytes += new FileInfo(plainPath).Length;

            // Create ZIP equivalent
            var zipPath = Path.Combine(zipDir, NameOf(lot, stage, part, ".Reg.zip"));
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry("data.csv", CompressionLevel.Fastest);
                using var es = entry.Open();
                using var src = File.OpenRead(plainPath);
                src.CopyTo(es);
            }

            zipBytes += new FileInfo(zipPath).Length;
        }

        return (plainDir, zipDir, plainBytes, zipBytes);
    }

    private async Task RunEndToEndScenarioAsync(string label, string dataDir, long inputBytes)
    {
        var profile = CreateBenchProfile();
        var swPrep = Stopwatch.StartNew();
        var discovery = _discovery.DiscoverFolder(profile, dataDir, Token);
        var plan = new FtImportSelectionModel(discovery).BuildImportPlan().Plan!;
        var union = _schemaPrep.ScanUnionSchema(plan, Token).UnionSchema!;
        var prepared = _schemaPrep.PrepareImportPlan(union, union.UnionMeasurementHeaders, Token).Plan!;
        swPrep.Stop();

        var dbPath = Path.Combine(_tempRoot, $"{label}.yat");
        using var db = _storage.Create(dbPath);
        var projectRepo = new DuckDbProjectRepository(db);
        var project = new Project { Id = Guid.NewGuid(), Name = label, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await projectRepo.AddAsync(project, Token);
        var creator = new DuckDbAtomicWorksheetCreator(db, TimeProvider.System);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(true);
        var allocBefore = GC.GetTotalAllocatedBytes(true);
        var gen0Before = GC.CollectionCount(0);
        var gen1Before = GC.CollectionCount(1);
        var gen2Before = GC.CollectionCount(2);

        long commitDurationMs = 0;
        long commitStartTick = 0;
        creator.PhaseHook = phase =>
        {
            if (phase == DuckDbAtomicWorksheetPhase.BeforeCommit)
            {
                commitStartTick = Stopwatch.GetTimestamp();
            }
            else if (phase == DuckDbAtomicWorksheetPhase.AfterCommit)
            {
                commitDurationMs = (long)Stopwatch.GetElapsedTime(commitStartTick).TotalMilliseconds;
            }
        };

        var sw = Stopwatch.StartNew();
        var result = await _importer.ImportAsync(creator, project.Id, $"Import_{label}", prepared, cancellationToken: Token);
        sw.Stop();
        await db.CheckpointAsync(Token);

        var heapAfter = GC.GetTotalMemory(true);
        var allocAfter = GC.GetTotalAllocatedBytes(true);
        var workingSetMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        var gen0Delta = GC.CollectionCount(0) - gen0Before;
        var gen1Delta = GC.CollectionCount(1) - gen1Before;
        var gen2Delta = GC.CollectionCount(2) - gen2Before;
        var dbBytes = new FileInfo(dbPath).Length;

        Assert.True(result.IsSuccess);
        var inputMb = inputBytes / (1024.0 * 1024.0);
        var sec = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        Report(
            $"[{label}] input={inputMb:F1}MB, cols={prepared.OutputColumns.Count} | " +
            $"processed={result.TotalProcessedRowCount:N0}, retained={result.TotalRetainedRowCount:N0}, filtered={result.TotalFilteredRowCount:N0} | " +
            $"prep={swPrep.ElapsedMilliseconds}ms, import={sw.ElapsedMilliseconds}ms (commit={commitDurationMs}ms) | " +
            $"throughput={inputMb / sec:F1} MB/s ({result.TotalProcessedRowCount / sec:N0} rows/s) | " +
            $"heapDelta={(heapAfter - heapBefore) / 1024.0 / 1024.0:F2}MB, totalAlloc={(allocAfter - allocBefore) / 1024.0 / 1024.0:F1}MB, " +
            $"workingSet={workingSetMb:F1}MB, gc(0/1/2)={gen0Delta}/{gen1Delta}/{gen2Delta}, dbSize={dbBytes / 1024.0 / 1024.0:F1}MB");
    }

    [Fact(Explicit = true)]
    public async Task Benchmark100MbPlainZipAndMidStreamCancellation()
    {
        var (plainDir, zipDir, plainBytes, zipBytes) = Generate100MbFixtures();

        await RunEndToEndScenarioAsync("plain_100mb", plainDir, plainBytes);
        await RunEndToEndScenarioAsync("zip_100mb", zipDir, zipBytes);

        // Mid-stream cancellation + rollback latency on 100 MB plain dataset.
        var profile = CreateBenchProfile();
        var plan = new FtImportSelectionModel(_discovery.DiscoverFolder(profile, plainDir, Token)).BuildImportPlan().Plan!;
        var union = _schemaPrep.ScanUnionSchema(plan, Token).UnionSchema!;
        var prepared = _schemaPrep.PrepareImportPlan(union, union.UnionMeasurementHeaders, Token).Plan!;

        var cancelDbPath = Path.Combine(_tempRoot, "cancel_bench.yat");
        using var db = _storage.Create(cancelDbPath);
        var project = new Project { Id = Guid.NewGuid(), Name = "CancelBench", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow };
        await new DuckDbProjectRepository(db).AddAsync(project, Token);
        var creator = new DuckDbAtomicWorksheetCreator(db, TimeProvider.System);

        long rollbackStartTick = 0;
        creator.PhaseHook = phase =>
        {
            if (phase == DuckDbAtomicWorksheetPhase.BeforeRollback)
            {
                rollbackStartTick = Stopwatch.GetTimestamp();
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var swCancel = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _importer.ImportAsync(creator, project.Id, "CancelledImport", prepared, cancellationToken: cts.Token));
        swCancel.Stop();
        var rollbackMs = rollbackStartTick > 0 ? (long)Stopwatch.GetElapsedTime(rollbackStartTick).TotalMilliseconds : 0;

        var wsCount = (await new DuckDbWorksheetRepository(db).GetByProjectIdAsync(project.Id, Token)).Count;
        Assert.Equal(0, wsCount);
        Report($"[cancel_mid_stream_100mb] cancelToReturn={swCancel.ElapsedMilliseconds}ms, rollbackPhase={rollbackMs}ms, remainingWorksheets={wsCount}");
    }
}
