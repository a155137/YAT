using System.Globalization;
using System.IO.Compression;
using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;

namespace YAT.Application.Tests;

public sealed class DatalogRowParserTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatalogStreamOpener _opener = new();
    private readonly DatalogStructureScanner _scanner = new();
    private readonly DatalogRowParser _parser = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DatalogRowParserTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft01c-tests", Guid.NewGuid().ToString("N"));
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

    private static ImportProfile CreateProfile(
        MissingValuePolicy policy = MissingValuePolicy.UseMissing,
        ColumnSelector? siteColumn = null,
        IReadOnlyList<CompositeFieldRule>? composites = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "FT01C_Profile",
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile().FilenameRules,
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = ",",
            CompositeFields = composites ?? [],
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Test_Name", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            UnitRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Unit", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "DATA_START", ColumnIndex = 0, MatchMode = KeywordMatchMode.StartsWith },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "HARD_BIN" },
            SiteColumn = siteColumn,
            IgnoredColumns = [new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "IGNORE_ME" }]
        },
        BinRules = new BinRuleSet { GoodBins = ["1", "01", "001"], NonRetestFailBins = ["2"] },
        MissingValueRules = new MissingValueRuleSet
        {
            Markers = ["", "N/A", "NULL", "*****"],
            Policy = policy
        }
    };

    private DatalogStructureScanResult ScanText(ImportProfile profile, string content)
    {
        using var sr = new StringReader(content);
        var scan = _scanner.Scan(profile, sr, Token);
        Assert.True(scan.IsSuccess, string.Join("; ", scan.Diagnostics.Select(d => d.Message)));
        return scan;
    }

    [Fact]
    public void StreamingCorrectnessPreservesFirstLastRowOrderZipAndSelectedColumnOrder()
    {
        var content = """
            Preamble
            Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,300,IDD,COMPOSITE
            Unit,bin,site,ms,mV,mA,tag
            DATA_START,01,00,9,10.5,-2.5e-3,A/B
            DATA_START,001,1,9,0,100,C/D
            """;

        var profile = CreateProfile(
            siteColumn: new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" },
            composites:
            [
                new CompositeFieldRule(
                    new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "COMPOSITE" },
                    "/",
                    ["PART_A", "PART_B"])
            ]);

        var zipPath = Path.Combine(_tempDir, "lot.Reg.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var writer = new StreamWriter(archive.CreateEntry("lot.csv").Open(), new UTF8Encoding(false));
            writer.Write(content);
        }

        DatalogStructureScanResult scan;
        using (var opened = _opener.Open(zipPath, Token))
        {
            scan = _scanner.Scan(profile, opened.Reader, Token);
            Assert.True(scan.IsSuccess);
        }

        var rows = new List<(int Row, string Bin, string? Site, double?[] Meas)>();
        var result = _parser.ParseFile(
            zipPath,
            profile,
            scan,
            ["IDD", "300"],
            r => rows.Add((r.SourceRowNumber, r.Bin, r.Site, r.Measurements.ToArray())),
            cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.ProcessedRowCount);
        Assert.Equal(2, result.RetainedRowCount);
        Assert.Equal(2, rows.Count);
        Assert.Equal((4, "01", "00"), (rows[0].Row, rows[0].Bin, rows[0].Site));
        Assert.Equal([-2.5e-3, 10.5], rows[0].Meas);
        Assert.Equal((5, "001", "1"), (rows[1].Row, rows[1].Bin, rows[1].Site));
        Assert.Equal([100.0, 0.0], rows[1].Meas);
    }

    [Theory]
    [InlineData(MissingValuePolicy.UseMissing, null)]
    [InlineData(MissingValuePolicy.ReplaceWithZero, 0.0)]
    public void MissingMarkersAndNonFiniteMarkersFollowPolicy(MissingValuePolicy policy, double? expected)
    {
        var content = """
            Test_Name,HARD_BIN,IGNORE_ME,T1
            DATA_START,1,0,
            DATA_START,1,0,N/A
            DATA_START,1,0,NULL
            DATA_START,1,0,*****
            DATA_START,1,0,NaN
            DATA_START,1,0,Infinity
            DATA_START,1,0,-Infinity
            """;

        var profile = CreateProfile(policy: policy);
        var scan = ScanText(profile, content);
        var values = new List<double?>();

        using var sr = new StringReader(content);
        var result = _parser.ParseReader(sr, "mem.csv", profile, scan, ["T1"], r => values.Add(r.Measurements.Span[0]), cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, values.Count);
        Assert.All(values, v => Assert.Equal(expected, v));
    }

    [Fact]
    public void StopImportPolicyRejectsMissingValueWithActionableDiagnostic()
    {
        var content = "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0,N/A\n";
        var profile = CreateProfile(policy: MissingValuePolicy.StopImport);
        var scan = ScanText(profile, content);

        using var sr = new StringReader(content);
        var result = _parser.ParseReader(sr, "stop.csv", profile, scan, ["T1"], _ => { }, cancellationToken: Token);

        Assert.False(result.IsSuccess);
        var diag = Assert.Single(result.Diagnostics);
        Assert.Equal(DatalogParseErrorCode.MissingValueRejected, diag.Code);
        Assert.Equal("stop.csv", diag.SourceName);
        Assert.Equal(2, diag.SourceRowNumber);
        Assert.Equal("T1", diag.ColumnHeader);
        Assert.Equal("N/A", diag.ValueExcerpt);
    }

    [Theory]
    [InlineData("abc", DatalogParseErrorCode.InvalidNumericValue)]
    [InlineData("1,234.5", DatalogParseErrorCode.InvalidNumericValue)]
    [InlineData("1e400", DatalogParseErrorCode.NumericOverflow)]
    [InlineData("-1e400", DatalogParseErrorCode.NumericOverflow)]
    public void InvalidNumericAndOverflowAreRejectedAndDistinguishedRegardlessOfThreadCulture(string badValue, DatalogParseErrorCode expectedCode)
    {
        var content = $"Test_Name\tHARD_BIN\tIGNORE_ME\tT1\nDATA_START\t1\t0\t{badValue}\n";
        var profile = CreateProfile() with
        {
            StructureRules = CreateProfile().StructureRules with { Delimiter = "\t" }
        };
        var scan = ScanText(profile, content);

        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            using var sr = new StringReader(content);
            var result = _parser.ParseReader(sr, "num.tsv", profile, scan, ["T1"], _ => { }, cancellationToken: Token);

            Assert.False(result.IsSuccess);
            Assert.Equal(expectedCode, result.Diagnostics[0].Code);
            Assert.Equal("T1", result.Diagnostics[0].ColumnHeader);
            Assert.Equal(2, result.Diagnostics[0].SourceRowNumber);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public void AbsentMeasurementColumnIsDistinguishedFromMissingCellAndReservedColumnsAreRejected()
    {
        var content = "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0,\nDATA_START,1,0,5.5\n";
        var profile = CreateProfile();
        var scan = ScanText(profile, content);

        var rows = new List<double?[]>();
        using (var sr = new StringReader(content))
        {
            var result = _parser.ParseReader(sr, "absent.csv", profile, scan, ["T1", "ABSENT_TEST"], r => rows.Add(r.Measurements.ToArray()), cancellationToken: Token);
            Assert.True(result.IsSuccess);
            Assert.Equal(["ABSENT_TEST"], result.Mapping!.AbsentHeaders);
            Assert.True(result.Mapping.Columns[0].IsPresentInSource);
            Assert.False(result.Mapping.Columns[1].IsPresentInSource);
            Assert.Null(rows[0][0]); // Missing cell in T1
            Assert.Null(rows[0][1]); // Absent column
            Assert.Equal(5.5, rows[1][0]);
            Assert.Null(rows[1][1]);
        }

        // Selecting Bin/Ignored column is rejected
        using var sr2 = new StringReader(content);
        var badSel = _parser.ParseReader(sr2, "absent.csv", profile, scan, ["HARD_BIN"], _ => { }, cancellationToken: Token);
        Assert.False(badSel.IsSuccess);
        Assert.Equal(DatalogParseErrorCode.InvalidColumnMapping, badSel.Diagnostics[0].Code);
    }

    [Fact]
    public void BinFirstPredicateSkipsUnselectedRowNumericParsingAndUnselectedColumnBadValuesDoNotFail()
    {
        var content = """
            Test_Name,HARD_BIN,IGNORE_ME,T_SEL,T_UNSEL
            DATA_START,1,0,12.5,NOT_A_NUMBER
            DATA_START,2,0,BAD_NUM_ON_EXCLUDED,ALSO_BAD
            DATA_START,01,0,3.25,999
            """;

        var profile = CreateProfile();
        var scan = ScanText(profile, content);
        var retainedBins = new List<string>();

        using var sr = new StringReader(content);
        var result = _parser.ParseReader(
            sr,
            "filter.csv",
            profile,
            scan,
            ["T_SEL"],
            r => retainedBins.Add(r.Bin),
            includeBin: bin => bin is "1" or "01",
            cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.ProcessedRowCount);
        Assert.Equal(2, result.RetainedRowCount);
        Assert.Equal(["1", "01"], retainedBins);
    }

    [Fact]
    public void StructuralErrorsShortLongMissingBinQuotedCsvAndCancellationAreHandledSafely()
    {
        var profile = CreateProfile();

        // Short record
        var shortContent = "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0\n";
        var scanShort = ScanText(profile, shortContent);
        using (var sr = new StringReader(shortContent))
        {
            var res = _parser.ParseReader(sr, "short.csv", profile, scanShort, ["T1"], _ => { }, cancellationToken: Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogParseErrorCode.InvalidRecordWidth, res.Diagnostics[0].Code);
        }

        // Missing required Bin on excluded/retained row
        var missingBinContent = "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,,0,1.0\n";
        var scanBin = ScanText(profile, missingBinContent);
        using (var sr = new StringReader(missingBinContent))
        {
            var res = _parser.ParseReader(sr, "nobin.csv", profile, scanBin, ["T1"], _ => { }, cancellationToken: Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogParseErrorCode.MissingRequiredBin, res.Diagnostics[0].Code);
        }

        // Unsupported quoted CSV field
        var quotedContent = "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0,1.0\nDATA_START,1,0,\"1,2\"\n";
        var scanQuoted = ScanText(profile, quotedContent);
        using (var sr = new StringReader(quotedContent))
        {
            var res = _parser.ParseReader(sr, "quoted.csv", profile, scanQuoted, ["T1"], _ => { }, cancellationToken: Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogParseErrorCode.UnsupportedQuotedField, res.Diagnostics[0].Code);
        }

        // Cancellation disposes opened file stream
        var csvPath = Path.Combine(_tempDir, "cancel.csv");
        File.WriteAllText(csvPath, "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0,1.0\nDATA_START,1,0,2.0\n");
        DatalogStructureScanResult scanFile;
        using (var opened = _opener.Open(csvPath, Token))
        {
            scanFile = _scanner.Scan(profile, opened.Reader, Token);
        }

        using var cts = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            _parser.ParseFile(csvPath, profile, scanFile, ["T1"], _ => cts.Cancel(), cancellationToken: cts.Token));

        // File must not remain locked after cancellation
        File.Delete(csvPath);
    }

    [Fact]
    public void ReusedMeasurementBufferHasNoStaleValuesAcrossNumericMissingExcludedAndAbsentRows()
    {
        var content = """
            Test_Name,HARD_BIN,IGNORE_ME,T1,T2
            DATA_START,1,0,10.5,20.25
            DATA_START,1,0,,N/A
            DATA_START,2,0,999.0,888.0
            DATA_START,1,0,0,-0.0
            DATA_START,1,0,NaN,42.5
            """;

        var profile = CreateProfile(policy: MissingValuePolicy.UseMissing);
        var scan = ScanText(profile, content);
        var copiedRows = new List<double?[]>();
        ReadOnlyMemory<double?>? firstBorrowedSlice = null;

        using var sr = new StringReader(content);
        var result = _parser.ParseReader(
            sr,
            "buffer.csv",
            profile,
            scan,
            ["T1", "ABSENT", "T2"],
            row =>
            {
                firstBorrowedSlice ??= row.Measurements;
                copiedRows.Add(row.Measurements.ToArray());
            },
            includeBin: bin => bin == "1",
            cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(5, result.ProcessedRowCount);
        Assert.Equal(4, result.RetainedRowCount);

        // Every retained row has exact values with no stale leakage from previous or excluded rows.
        Assert.Equal([10.5, null, 20.25], copiedRows[0]);
        Assert.Equal([null, null, null], copiedRows[1]);
        Assert.Equal([0.0, null, 0.0], copiedRows[2]);
        Assert.Equal([null, null, 42.5], copiedRows[3]);

        // Backing buffer is reused across rows (borrowed memory reflects the last retained row).
        Assert.NotNull(firstBorrowedSlice);
        Assert.Equal([null, null, 42.5], firstBorrowedSlice.Value.ToArray());
    }

    [Fact]
    public void MeasurementMappingEnforcesOrdinalMatchingRejectsReservedColumnsAndNeverZeroFillsAbsentColumns()
    {
        var content = "Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,T1\nDATA_START,1,0,9,0\nDATA_START,1,0,9,N/A\n";
        var siteSelector = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" };

        // 1. Exact ordinal matching: "t1" is distinct from "T1" and treated as absent.
        // 2. ReplaceWithZero replaces missing cell in T1 with 0.0, but NEVER zero-fills absent column "t1".
        var replaceProfile = CreateProfile(policy: MissingValuePolicy.ReplaceWithZero, siteColumn: siteSelector);
        var replaceScan = ScanText(replaceProfile, content);
        var replaceRows = new List<double?[]>();
        using (var sr = new StringReader(content))
        {
            var res = _parser.ParseReader(sr, "ord.csv", replaceProfile, replaceScan, ["T1", "t1"], r => replaceRows.Add(r.Measurements.ToArray()), cancellationToken: Token);
            Assert.True(res.IsSuccess);
            Assert.Equal(["t1"], res.Mapping!.AbsentHeaders);
            Assert.Equal([0.0, null], replaceRows[0]);
            Assert.Equal([0.0, null], replaceRows[1]);
        }

        // 3. StopImport allows numeric zero and absent column, while rejecting missing markers and NaN/Infinity/-Infinity.
        var zeroOnlyContent = "Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,T1\nDATA_START,1,0,9,0\nDATA_START,1,0,9,-0.0\n";
        var stopProfile = CreateProfile(policy: MissingValuePolicy.StopImport, siteColumn: siteSelector);
        var stopScan = ScanText(stopProfile, zeroOnlyContent);
        var stopRows = new List<double?[]>();
        using (var sr = new StringReader(zeroOnlyContent))
        {
            var res = _parser.ParseReader(sr, "zero.csv", stopProfile, stopScan, ["T1", "ABSENT"], r => stopRows.Add(r.Measurements.ToArray()), cancellationToken: Token);
            Assert.True(res.IsSuccess);
            Assert.Equal([0.0, null], stopRows[0]);
            Assert.Equal([0.0, null], stopRows[1]);
        }

        foreach (var nonFinite in new[] { "NaN", "Infinity", "-Infinity" })
        {
            var nfContent = $"Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,T1\nDATA_START,1,0,9,{nonFinite}\n";
            var nfScan = ScanText(stopProfile, nfContent);
            using var sr = new StringReader(nfContent);
            var res = _parser.ParseReader(sr, "nf.csv", stopProfile, nfScan, ["T1"], _ => { }, cancellationToken: Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogParseErrorCode.MissingValueRejected, res.Diagnostics[0].Code);
        }

        // 4. Unconfigured non-finite variants ("+Infinity", "infinity") are InvalidNumericValue, not NumericOverflow.
        var useMissingProfile = CreateProfile(policy: MissingValuePolicy.UseMissing, siteColumn: siteSelector);
        foreach (var badVariant in new[] { "+Infinity", "infinity", "-infinity" })
        {
            var badContent = $"Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,T1\nDATA_START,1,0,9,{badVariant}\n";
            var badScan = ScanText(useMissingProfile, badContent);
            using var sr = new StringReader(badContent);
            var res = _parser.ParseReader(sr, "badnf.csv", useMissingProfile, badScan, ["T1"], _ => { }, cancellationToken: Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogParseErrorCode.InvalidNumericValue, res.Diagnostics[0].Code);
        }

        // 5. Bin, Site, and Ignored columns are all rejected when selected as measurements.
        foreach (var reserved in new[] { "HARD_BIN", "SITE_NUM", "IGNORE_ME" })
        {
            using var sr = new StringReader(content);
            var res = _parser.ParseReader(sr, "res.csv", useMissingProfile, replaceScan, [reserved], _ => { }, cancellationToken: Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogParseErrorCode.InvalidColumnMapping, res.Diagnostics[0].Code);
        }
    }

    [Fact]
    public void BinPreservesLeadingZerosAndWhitespaceWhileEmptySiteCellBecomesNull()
    {
        var content = """
            Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,T1
            DATA_START, 01 ,,9,1.25
            DATA_START,001,00,9,2.50
            """;

        var profile = CreateProfile(
            siteColumn: new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" });
        var scan = ScanText(profile, content);
        var rows = new List<(string Bin, string? Site)>();

        using var sr = new StringReader(content);
        var result = _parser.ParseReader(sr, "binsite.csv", profile, scan, ["T1"], r => rows.Add((r.Bin, r.Site)), cancellationToken: Token);

        Assert.True(result.IsSuccess);
        Assert.Equal((" 01 ", null), rows[0]);
        Assert.Equal(("001", "00"), rows[1]);
    }

    [Fact]
    public void CancellationIsHonoredBeforeDataStartDuringStreamReadAndWideMeasurementParsing()
    {
        // 1. Cancellation while skipping preamble rows before DataStart.
        var preambleLines = string.Join('\n', Enumerable.Repeat("PREAMBLE", 50));
        var beforeDataContent = $"{preambleLines}\nTest_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0,1.0\n";
        var profile = CreateProfile();
        var scanBefore = ScanText(profile, beforeDataContent);

        using (var cts = new CancellationTokenSource())
        using (var reader = new CancellingOnReadCountReader(beforeDataContent, cancelAtReadNumber: 5, cts))
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                _parser.ParseReader(reader, "pre.csv", profile, scanBefore, ["T1"], _ => { }, cancellationToken: cts.Token));
        }

        // 2. Cancellation during streaming data reads.
        var dataRows = string.Join('\n', Enumerable.Repeat("DATA_START,1,0,1.0", 50));
        var streamContent = $"Test_Name,HARD_BIN,IGNORE_ME,T1\n{dataRows}\n";
        var scanStream = ScanText(profile, streamContent);

        using (var cts = new CancellationTokenSource())
        using (var reader = new CancellingOnReadCountReader(streamContent, cancelAtReadNumber: 10, cts))
        {
            Assert.ThrowsAny<OperationCanceledException>(() =>
                _parser.ParseReader(reader, "stream.csv", profile, scanStream, ["T1"], _ => { }, cancellationToken: cts.Token));
        }

        // 3. Cancellation during wide-record selected measurement parsing (> 1,024 selected columns).
        const int wideCols = 2_200;
        var headers = Enumerable.Range(0, wideCols).Select(i => $"W_{i:D4}").ToArray();
        var wideHeaderLine = $"Test_Name,HARD_BIN,IGNORE_ME,{string.Join(',', headers)}";
        var wideRowLine = $"DATA_START,1,0,{string.Join(',', Enumerable.Repeat("1.5", wideCols))}";
        var wideContent = $"{wideHeaderLine}\n{wideRowLine}\n";
        var wideScan = ScanText(profile, wideContent);

        using (var cts = new CancellationTokenSource())
        using (var sr = new StringReader(wideContent))
        {
            var invoked = false;
            // Trigger cancellation immediately after Bin check (via includeBin) so it fires at column 1,024 inside measurement loop.
            Assert.ThrowsAny<OperationCanceledException>(() =>
                _parser.ParseReader(
                    sr,
                    "wide.csv",
                    profile,
                    wideScan,
                    headers,
                    _ => invoked = true,
                    includeBin: _ =>
                    {
                        cts.Cancel();
                        return true;
                    },
                    cancellationToken: cts.Token));
            Assert.False(invoked);
        }
    }

    [Fact]
    public void PlainAndZipStreamsAreDisposedAfterSuccessFailureCancellationAndCallbackException()
    {
        var profile = CreateProfile();
        var goodContent = "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0,1.0\nDATA_START,1,0,2.0\n";
        var badContent = "Test_Name,HARD_BIN,IGNORE_ME,T1\nDATA_START,1,0,NOT_A_NUM\n";

        foreach (var isZip in new[] { false, true })
        {
            // 1. Success
            var okPath = CreateInputFile(isZip, "ok", goodContent);
            var okScan = ScanFile(profile, okPath);
            var okRes = _parser.ParseFile(okPath, profile, okScan, ["T1"], _ => { }, cancellationToken: Token);
            Assert.True(okRes.IsSuccess);
            File.Delete(okPath);

            // 2. Parsing failure
            var failPath = CreateInputFile(isZip, "fail", badContent);
            var failScan = ScanFile(profile, failPath);
            var failRes = _parser.ParseFile(failPath, profile, failScan, ["T1"], _ => { }, cancellationToken: Token);
            Assert.False(failRes.IsSuccess);
            File.Delete(failPath);

            // 3. Cancellation
            var cancelPath = CreateInputFile(isZip, "cancel", goodContent);
            var cancelScan = ScanFile(profile, cancelPath);
            using (var cts = new CancellationTokenSource())
            {
                Assert.ThrowsAny<OperationCanceledException>(() =>
                    _parser.ParseFile(cancelPath, profile, cancelScan, ["T1"], _ => cts.Cancel(), cancellationToken: cts.Token));
            }
            File.Delete(cancelPath);

            // 4. Caller callback failure
            var cbPath = CreateInputFile(isZip, "cb", goodContent);
            var cbScan = ScanFile(profile, cbPath);
            Assert.Throws<InvalidOperationException>(() =>
                _parser.ParseFile(cbPath, profile, cbScan, ["T1"], _ => throw new InvalidOperationException("boom"), cancellationToken: Token));
            File.Delete(cbPath);
        }
    }

    [Fact]
    public void ParseReaderAndParseFileWithMaxDataRowsPreserveBehaviorAndStopAtBoundary()
    {
        var validContent = """
            Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,T1
            DATA_START,1,0,9,1.5
            DATA_START,1,0,9,2.5
            DATA_START,1,0,9,3.5
            DATA_START,1,0,9,4.5
            """;
        var profile = CreateProfile(siteColumn: new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" });
        var validScan = ScanText(profile, validContent);

        var unlimitedRows = new List<double?>();
        using (var sr = new StringReader(validContent))
        {
            var res = _parser.ParseReader(sr, "valid.csv", profile, validScan, ["T1"], r => unlimitedRows.Add(r.Measurements.Span[0]), cancellationToken: Token);
            Assert.True(res.IsSuccess);
            Assert.Equal(4, res.ProcessedRowCount);
            Assert.Equal([1.5, 2.5, 3.5, 4.5], unlimitedRows);
        }

        // Invalid row after maxDataRows (row 3) must not be parsed when maxDataRows = 2.
        var invalidAfterLimitContent = """
            Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,T1
            DATA_START,1,0,9,1.5
            DATA_START,1,0,9,2.5
            DATA_START,1,0,9,NOT_A_NUMBER
            DATA_START,1,0,9,4.5
            """;
        var invalidScan = ScanText(profile, invalidAfterLimitContent);

        var boundedRows = new List<double?>();
        using (var sr = new StringReader(invalidAfterLimitContent))
        {
            var res = _parser.ParseReader(sr, "bounded.csv", profile, invalidScan, ["T1"], r => boundedRows.Add(r.Measurements.Span[0]), cancellationToken: Token, maxDataRows: 2);
            Assert.True(res.IsSuccess);
            Assert.Equal(2, res.ProcessedRowCount);
            Assert.Equal([1.5, 2.5], boundedRows);
        }

        // Unlimited parsing on the same content fails at the invalid row.
        using (var sr = new StringReader(invalidAfterLimitContent))
        {
            var res = _parser.ParseReader(sr, "bounded.csv", profile, invalidScan, ["T1"], _ => { }, cancellationToken: Token, maxDataRows: null);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogParseErrorCode.InvalidNumericValue, res.Diagnostics[0].Code);
        }

        // Zero and negative maxDataRows are rejected on both ParseReader and ParseFile.
        using (var sr = new StringReader(validContent))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                _parser.ParseReader(sr, "valid.csv", profile, validScan, ["T1"], _ => { }, cancellationToken: Token, maxDataRows: 0));
        }

        var zipPath = CreateInputFile(isZip: true, "bounded_zip", invalidAfterLimitContent);
        var zipScan = ScanFile(profile, zipPath);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _parser.ParseFile(zipPath, profile, zipScan, ["T1"], _ => { }, cancellationToken: Token, maxDataRows: -1));

        var zipRows = new List<double?>();
        var zipRes = _parser.ParseFile(zipPath, profile, zipScan, ["T1"], r => zipRows.Add(r.Measurements.Span[0]), cancellationToken: Token, maxDataRows: 1);
        Assert.True(zipRes.IsSuccess);
        Assert.Equal([1.5], zipRows);
        File.Delete(zipPath);
    }

    private string CreateInputFile(bool isZip, string stem, string content)
    {
        if (!isZip)
        {
            var path = Path.Combine(_tempDir, $"{stem}_{Guid.NewGuid():N}.csv");
            File.WriteAllText(path, content, new UTF8Encoding(false));
            return path;
        }

        var zipPath = Path.Combine(_tempDir, $"{stem}_{Guid.NewGuid():N}.Reg.zip");
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry($"{stem}.csv").Open(), new UTF8Encoding(false));
        writer.Write(content);
        return zipPath;
    }

    private DatalogStructureScanResult ScanFile(ImportProfile profile, string path)
    {
        using var opened = _opener.Open(path, Token);
        var scan = _scanner.Scan(profile, opened.Reader, Token);
        Assert.True(scan.IsSuccess);
        return scan;
    }

    private sealed class CancellingOnReadCountReader(string text, int cancelAtReadNumber, CancellationTokenSource cts) : TextReader
    {
        private readonly StringReader _inner = new(text);
        private int _reads;

        public override int Peek() => _inner.Peek();

        public override int Read()
        {
            if (++_reads == cancelAtReadNumber)
            {
                cts.Cancel();
            }

            return _inner.Read();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
