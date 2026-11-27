using System.Diagnostics;
using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;

namespace YAT.Application.Tests;

// Performance benchmarks for FT-03.2 multi-file structure scanning, Union Schema construction,
// per-file measurement mapping, and FT/RT Bin retention policy resolution.
// Explicit - run directly:
//
//     YAT.Application.Tests.exe -explicit only -class YAT.Application.Tests.FtSchemaPreparationBenchmarkTests
public sealed class FtSchemaPreparationBenchmarkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FtFileDiscoveryService _discovery = new();
    private readonly FtSchemaPreparationService _service = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FtSchemaPreparationBenchmarkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft03-2-bench", Guid.NewGuid().ToString("N"));
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

    private static ImportProfile CreateBenchProfile()
    {
        var baseProfile = FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile();
        return baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                AcceptedExtensions = [".csv", ".Reg.zip", ".zip"]
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
                GoodBins = ["1", "01"],
                NonRetestFailBins = ["2", "09"]
            },
            MissingValueRules = new MissingValueRuleSet
            {
                Markers = ["", "N/A"],
                Policy = MissingValuePolicy.UseMissing
            }
        };
    }

    private string GenerateDataset(string folderName, int fileCount, int columnsPerFile, int dataRowsPerFile)
    {
        var dir = Path.Combine(_tempDir, folderName);
        Directory.CreateDirectory(dir);
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        var lotCount = Math.Max(1, fileCount / 5);
        for (var i = 0; i < fileCount; i++)
        {
            var lotIdx = i % lotCount;
            var slot = i / lotCount;
            var stageSeg = slot switch
            {
                0 or 1 => "F1",
                2 or 3 => "F1B45678R1",
                _ => "F1B45678R2"
            };
            var part = (slot % 2) + 1;
            var fileName = $"E320BD-262.20261123111219.LOT{lotIdx:D4}_TQB01RXAA1_{stageSeg}_{part}_20261122171516_20261123110911.csv";
            var path = Path.Combine(dir, fileName);

            using var writer = new StreamWriter(path, append: false, utf8);
            var headerSb = new StringBuilder(columnsPerFile * 12);
            headerSb.Append("UNIT_ID,SOFT_BIN,SITE_NUM");
            // Slight header variation per lot to exercise Union Schema + absent mapping.
            var offset = (lotIdx % 3) * 2;
            for (var c = 0; c < columnsPerFile; c++)
            {
                headerSb.Append(',');
                headerSb.Append("T_");
                headerSb.Append(c + offset);
            }

            writer.WriteLine(headerSb.ToString());

            var rowSb = new StringBuilder(columnsPerFile * 6);
            rowSb.Append("1,1,0");
            for (var c = 0; c < columnsPerFile; c++)
            {
                rowSb.Append(",1.23");
            }

            var rowLine = rowSb.ToString();
            for (var r = 0; r < dataRowsPerFile; r++)
            {
                writer.WriteLine(rowLine);
            }
        }

        return dir;
    }

    private void RunPreparationScenario(string label, int fileCount, int columnsPerFile, int dataRowsPerFile)
    {
        var dir = GenerateDataset(label, fileCount, columnsPerFile, dataRowsPerFile);
        var profile = CreateBenchProfile();
        var discovery = _discovery.DiscoverFolder(profile, dir, Token);
        var plan = new FtImportSelectionModel(discovery).BuildImportPlan().Plan!;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var heapBefore = GC.GetTotalMemory(forceFullCollection: true);
        var allocBefore = GC.GetTotalAllocatedBytes(precise: true);

        var swScan = Stopwatch.StartNew();
        var scanRes = _service.ScanUnionSchema(plan, Token);
        swScan.Stop();
        Assert.True(scanRes.IsSuccess);

        var selection = new FtTestItemSelectionModel(scanRes.UnionSchema!, selectAllByDefault: true);
        var swPrep = Stopwatch.StartNew();
        var prepRes = _service.PrepareImportPlan(selection, Token);
        swPrep.Stop();
        Assert.True(prepRes.IsValid);

        var heapAfter = GC.GetTotalMemory(forceFullCollection: true);
        var allocAfter = GC.GetTotalAllocatedBytes(precise: true);

        // Verify resource disposal: first and last source files can be opened exclusively.
        using (new FileStream(plan.OrderedFiles[0].SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (new FileStream(plan.OrderedFiles[^1].SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

        Report(
            $"[{label}] files={plan.OrderedFiles.Count:N0}, unionHeaders={scanRes.UnionSchema!.UnionMeasurementHeaders.Count:N0} | " +
            $"scan={swScan.ElapsedMilliseconds}ms, prepare={swPrep.ElapsedMilliseconds}ms | " +
            $"managedHeapNetDelta={(heapAfter - heapBefore) / 1024.0 / 1024.0:F2}MB, totalAlloc={(allocAfter - allocBefore) / 1024.0 / 1024.0:F2}MB");
    }

    [Fact(Explicit = true)]
    public void BenchmarkStructureScanningAndSchemaPreparation()
    {
        // 1. 100 selected source files (30 cols)
        RunPreparationScenario("files_100", fileCount: 100, columnsPerFile: 30, dataRowsPerFile: 5);

        // 2. 1,000 selected source files (30 cols)
        RunPreparationScenario("files_1000", fileCount: 1_000, columnsPerFile: 30, dataRowsPerFile: 2);

        // 3. Wide headers (>= 500 measurement columns across 20 files)
        RunPreparationScenario("wide_500cols", fileCount: 20, columnsPerFile: 520, dataRowsPerFile: 5);

        // 4. Representative large datalog (200,000 data rows) verifying bounded structure scanning stops at DataStartRow.
        RunPreparationScenario("large_200k_rows", fileCount: 1, columnsPerFile: 50, dataRowsPerFile: 200_000);

        // 5. Cancellation during multi-file scanning (cancel after 5ms on 1,000-file dataset).
        var dir1000 = Path.Combine(_tempDir, "files_1000");
        var profile = CreateBenchProfile();
        var plan1000 = new FtImportSelectionModel(_discovery.DiscoverFolder(profile, dir1000, Token)).BuildImportPlan().Plan!;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
        var swCancel = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => _service.ScanUnionSchema(plan1000, cts.Token));
        swCancel.Stop();

        // Verify no leaked open file handles after mid-scan cancellation.
        foreach (var file in plan1000.OrderedFiles.Take(25))
        {
            using var _ = new FileStream(file.SourcePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }

        Report($"[cancel_mid_scan_1000] cancelled cleanly in {swCancel.ElapsedMilliseconds}ms, file handles verified disposed.");
    }
}
