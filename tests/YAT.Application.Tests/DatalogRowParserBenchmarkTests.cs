using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;

namespace YAT.Application.Tests;

public sealed class DatalogRowParserBenchmarkTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatalogStreamOpener _opener = new();
    private readonly DatalogStructureScanner _scanner = new();
    private readonly DatalogRowParser _parser = new();

    public DatalogRowParserBenchmarkTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft01c-bench", Guid.NewGuid().ToString("N"));
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

    [Fact(Explicit = true)]
    public void Benchmark105MbFullStreamParsingScenarios()
    {
        const int sourceTestCols = 60;
        var selectedSmall = Enumerable.Range(0, 5).Select(i => $"T_{i:D3}").ToArray();
        var selectedLarge = Enumerable.Range(0, 40).Select(i => $"T_{i:D3}").ToArray();

        var profile = new ImportProfile
        {
            Id = Guid.NewGuid(),
            Name = "BenchProfile",
            SchemaVersion = ImportProfile.CurrentSchemaVersion,
            FilenameRules = FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile().FilenameRules,
            StructureRules = new DatalogStructureRuleSet
            {
                Delimiter = ",",
                HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 1 },
                DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 2 },
                BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "BIN" },
                SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE" }
            },
            BinRules = new BinRuleSet { GoodBins = ["1"], NonRetestFailBins = ["2"] },
            MissingValueRules = new MissingValueRuleSet { Markers = ["", "N/A"], Policy = MissingValuePolicy.UseMissing }
        };

        var plainPath = Path.Combine(_tempDir, "bench_105mb.csv");
        var testHeaders = string.Join(',', Enumerable.Range(0, sourceTestCols).Select(i => $"T_{i:D3}"));
        var headerLine = $"BIN,SITE,{testHeaders}\n";
        var values = string.Join(',', Enumerable.Range(0, sourceTestCols).Select(i => $"{i}.25"));
        var rowBin1 = $"1,1,{values}\n";
        var rowBin2 = $"2,2,{values}\n";

        long totalRows = 0;
        using (var fs = new FileStream(plainPath, FileMode.Create, FileAccess.Write, FileShare.None, 65_536))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            sw.Write(headerLine);
            var target = 105L * 1024 * 1024;
            while (fs.Length < target)
            {
                sw.Write(totalRows % 5 == 0 ? rowBin2 : rowBin1);
                totalRows++;
            }
        }

        var plainBytes = new FileInfo(plainPath).Length;
        Assert.True(plainBytes >= 100L * 1024 * 1024);

        var zipPath = Path.Combine(_tempDir, "bench_105mb.Reg.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(plainPath, "bench_105mb.csv", CompressionLevel.Fastest);
        }

        var ct = TestContext.Current.CancellationToken;

        DatalogStructureScanResult scan;
        using (var opened = _opener.Open(plainPath, cancellationToken: ct))
        {
            scan = _scanner.Scan(profile, opened.Reader, ct);
            Assert.True(scan.IsSuccess);
        }

        var output = new List<string>();
        output.Add($"Dataset: {plainBytes / (1024.0 * 1024.0):F2} MB, Rows={totalRows}, SourceCols={sourceTestCols + 2}");

        RunBench("Plain-Small(5)", plainPath, selectedSmall, includeBin: null, output);
        RunBench("Plain-Large(40)", plainPath, selectedLarge, includeBin: null, output);
        RunBench("Zip-Large(40)", zipPath, selectedLarge, includeBin: null, output);
        RunBench("Plain-BinFilter(40)", plainPath, selectedLarge, includeBin: b => b == "1", output);

        // Cancellation latency measurement: measure actual latency from cancellation request to parser termination.
        using var cts = new CancellationTokenSource();
        var swTotalToCancel = Stopwatch.StartNew();
        long cancelRequestedTs = 0;
        long cancelAt = 50_000;
        long seen = 0;
        Assert.ThrowsAny<OperationCanceledException>(() =>
            _parser.ParseFile(
                plainPath,
                profile,
                scan,
                selectedLarge,
                _ =>
                {
                    if (++seen == cancelAt)
                    {
                        cancelRequestedTs = Stopwatch.GetTimestamp();
                        cts.Cancel();
                    }
                },
                cancellationToken: cts.Token));
        var actualCancelLatency = Stopwatch.GetElapsedTime(cancelRequestedTs);
        swTotalToCancel.Stop();
        Assert.True(cancelRequestedTs > 0);
        output.Add($"Cancel after {cancelAt} rows: TotalToCancel={swTotalToCancel.ElapsedMilliseconds}ms, ActualCancelLatency={actualCancelLatency.TotalMilliseconds:F3}ms ({actualCancelLatency.TotalMicroseconds:F1}us)");

        File.WriteAllLines(Path.Combine(_tempDir, "bench_summary.txt"), output);
        foreach (var line in output)
        {
            Console.WriteLine(line);
        }

        void RunBench(string label, string path, IReadOnlyList<string> selected, Func<string, bool>? includeBin, List<string> report)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var heapBefore = GC.GetTotalMemory(true);
            var allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();

            double checksum = 0;
            var res = _parser.ParseFile(
                path,
                profile,
                scan,
                selected,
                row => checksum += row.Measurements.Span[0] ?? 0,
                includeBin: includeBin,
                cancellationToken: ct);

            sw.Stop();
            var allocAfter = GC.GetAllocatedBytesForCurrentThread();
            var heapAfter = GC.GetTotalMemory(false);
            var peakWsMb = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0);
            Assert.True(res.IsSuccess);

            var rps = res.ProcessedRowCount / sw.Elapsed.TotalSeconds;
            var allocMb = (allocAfter - allocBefore) / (1024.0 * 1024.0);
            var heapDeltaMb = (heapAfter - heapBefore) / (1024.0 * 1024.0);
            report.Add($"{label}: Selected={selected.Count}, Processed={res.ProcessedRowCount}, Retained={res.RetainedRowCount}, Elapsed={sw.ElapsedMilliseconds}ms, Rows/s={rps:F0}, CumAlloc={allocMb:F1}MB, HeapDelta={heapDeltaMb:F1}MB, PeakWS={peakWsMb:F1}MB, Sum={checksum:F0}");
        }
    }
}
