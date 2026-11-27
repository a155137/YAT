using System.IO.Compression;
using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;

namespace YAT.Application.Tests;

public sealed class DatalogScannerAndPreviewTests : IDisposable
{
    private readonly string _tempDir;
    private readonly DatalogStreamOpener _opener = new();
    private readonly DatalogStructureScanner _scanner = new();
    private readonly DatalogPreviewReader _previewReader = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public DatalogScannerAndPreviewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft01b-tests", Guid.NewGuid().ToString("N"));
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

    private static ImportProfile CreateKeywordProfile(
        ColumnSelector? siteColumn = null,
        IReadOnlyList<CompositeFieldRule>? composites = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "KeywordScanProfile",
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile().FilenameRules,
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = ",",
            CompositeFields = composites ?? [],
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Test_Name", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            UnitRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Unit", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            TargetRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Target", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            LslRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "LSL", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            UslRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "USL", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "DATA_START", ColumnIndex = 0, MatchMode = KeywordMatchMode.StartsWith },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "HARD_BIN" },
            SiteColumn = siteColumn,
            IgnoredColumns = [new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "IGNORE_ME" }]
        },
        BinRules = new BinRuleSet { GoodBins = ["1"], NonRetestFailBins = ["2"] },
        MissingValueRules = new MissingValueRuleSet { Markers = ["", "N/A"], Policy = MissingValuePolicy.UseMissing }
    };

    [Fact]
    public void StreamingOpenerReadsPlainTextBomAndSingleEntryZipIgnoringDirectories()
    {
        var csvPath = Path.Combine(_tempDir, "bom.csv");
        File.WriteAllText(csvPath, "Header1,Header2\n1,2\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using (var opened = _opener.Open(csvPath, Token))
        {
            Assert.Null(opened.ArchiveEntryName);
            Assert.Equal("Header1,Header2", opened.Reader.ReadLine());
        }

        var zipPath = Path.Combine(_tempDir, "sample.Reg.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("nested/");
            var entry = archive.CreateEntry("nested/data.csv");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.WriteLine("Test_Name,HARD_BIN,300");
        }

        using (var openedZip = _opener.Open(zipPath, Token))
        {
            Assert.Equal("nested/data.csv", openedZip.ArchiveEntryName);
            Assert.Equal("Test_Name,HARD_BIN,300", openedZip.Reader.ReadLine());
        }
    }

    [Fact]
    public void StreamingOpenerRejectsAmbiguousEmptyCorruptZipAndInvalidUtf8()
    {
        var emptyZip = Path.Combine(_tempDir, "empty.zip");
        using (ZipFile.Open(emptyZip, ZipArchiveMode.Create)) { }
        Assert.Equal(DatalogOpenErrorCode.EmptyArchive, Assert.Throws<DatalogOpenException>(() => _opener.Open(emptyZip, Token)).ErrorCode);

        var multiZip = Path.Combine(_tempDir, "multi.zip");
        using (var archive = ZipFile.Open(multiZip, ZipArchiveMode.Create))
        {
            using (var w1 = new StreamWriter(archive.CreateEntry("a.csv").Open())) w1.WriteLine("A");
            using (var w2 = new StreamWriter(archive.CreateEntry("b.csv").Open())) w2.WriteLine("B");
        }
        Assert.Equal(DatalogOpenErrorCode.AmbiguousArchive, Assert.Throws<DatalogOpenException>(() => _opener.Open(multiZip, Token)).ErrorCode);

        var corruptZip = Path.Combine(_tempDir, "corrupt.zip");
        File.WriteAllBytes(corruptZip, [0x00, 0x01, 0x02, 0x03]);
        Assert.Equal(DatalogOpenErrorCode.CorruptArchive, Assert.Throws<DatalogOpenException>(() => _opener.Open(corruptZip, Token)).ErrorCode);

        var badUtf8 = Path.Combine(_tempDir, "bad.csv");
        File.WriteAllBytes(badUtf8, [0x80, 0xC0, 0xFE, (byte)'\n']);
        using var opened = _opener.Open(badUtf8, Token);
        var scanResult = _scanner.Scan(CreateKeywordProfile(), opened.Reader, Token);
        Assert.False(scanResult.IsSuccess);
        Assert.Contains(scanResult.Diagnostics, d => d.Code == DatalogScanErrorCode.InvalidEncoding);
    }

    [Fact]
    public void StructureScannerResolvesKeywordPreambleNumericAndUnicodeHeadersAndCompositeFields()
    {
        var content = """
            Lot: L1
            Tester: T2
            Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,300,300_Reg,PARM001,IDD,晶圓測試_Vth,COMPOSITE_COL
            Unit,bin,site,ms,mV,uA,ohm,mA,V,str
            Target,1,0,0,100,200,50,10,1.2,0
            LSL,,0,0,90,180,40,5,1.0,0
            USL,,0,0,110,220,60,15,1.4,0
            DATA_START,1,1,12,101,201,51,11,1.21,A/B
            """;

        var profile = CreateKeywordProfile(
            siteColumn: new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" },
            composites:
            [
                new CompositeFieldRule(
                    new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "COMPOSITE_COL" },
                    "/",
                    ["COMP_1", "COMP_2"])
            ]);

        using var reader = new StringReader(content);
        var result = _scanner.Scan(profile, reader, Token);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.RowPositions!.HeaderRowNumber);
        Assert.Equal(4, result.RowPositions.UnitRowNumber);
        Assert.Equal(5, result.RowPositions.TargetRowNumber);
        Assert.Equal(6, result.RowPositions.LslRowNumber);
        Assert.Equal(7, result.RowPositions.UslRowNumber);
        Assert.Equal(8, result.RowPositions.DataStartRowNumber);
        Assert.Equal(8, result.RowsScanned);

        Assert.Equal("HARD_BIN", result.BinColumn!.Name);
        Assert.True(result.HasConfiguredSiteColumn);
        Assert.Equal("SITE_NUM", result.SiteColumn!.Name);
        Assert.Equal(["IGNORE_ME"], result.IgnoredColumns.Select(c => c.Name));
        Assert.Equal(
            ["Test_Name", "300", "300_Reg", "PARM001", "IDD", "晶圓測試_Vth", "COMP_1", "COMP_2"],
            result.AvailableTestHeaders.Select(h => h.Name));

        // Composite row expansion verification
        var rawFields = new[] { "DATA_START", "1", "1", "12", "101", "201", "51", "11", "1.21", "A/B" };
        Assert.True(DatalogRecordTokenizer.TryExpandCompositeRow(rawFields, result.CompositePlans, 8, out var expanded, out _));
        Assert.Equal("A", expanded[^2]);
        Assert.Equal("B", expanded[^1]);

        // Invalid composite part count fails explicitly
        var badFields = new[] { "DATA_START", "1", "1", "12", "101", "201", "51", "11", "1.21", "ONLY_ONE" };
        Assert.False(DatalogRecordTokenizer.TryExpandCompositeRow(badFields, result.CompositePlans, 9, out _, out var compErr));
        Assert.Equal(DatalogScanErrorCode.InvalidCompositeField, compErr!.Code);
    }

    [Fact]
    public void StructureScannerDetectsMissingSiteDuplicateHeadersAmbiguousAndBadOrderRows()
    {
        // Configured Site missing from header
        var missingSiteProfile = CreateKeywordProfile(
            siteColumn: new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" });
        using (var r1 = new StringReader("Test_Name,HARD_BIN,300\nDATA_START,1,10\n"))
        {
            var res = _scanner.Scan(missingSiteProfile, r1, Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogScanErrorCode.MissingSiteColumn, res.Diagnostics[0].Code);
        }

        // Duplicate headers (never silently renamed)
        using (var r2 = new StringReader("Test_Name,HARD_BIN,300,300\nDATA_START,1,10,20\n"))
        {
            var res = _scanner.Scan(CreateKeywordProfile(), r2, Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogScanErrorCode.DuplicateHeader, res.Diagnostics[0].Code);
        }

        // Ambiguous header row
        using (var r3 = new StringReader("Test_Name,HARD_BIN,300\nTest_Name,HARD_BIN,300\nDATA_START,1,10\n"))
        {
            var res = _scanner.Scan(CreateKeywordProfile(), r3, Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogScanErrorCode.AmbiguousRowMatch, res.Diagnostics[0].Code);
        }

        // Metadata row before HeaderRow -> invalid row ordering
        var badOrderProfile = CreateKeywordProfile();
        using (var r5 = new StringReader("Unit,bin,mV\nTest_Name,HARD_BIN,300\nDATA_START,1,10\n"))
        {
            var res = _scanner.Scan(badOrderProfile, r5, Token);
            Assert.False(res.IsSuccess);
            Assert.Equal(DatalogScanErrorCode.InvalidRowOrdering, res.Diagnostics[0].Code);
        }
    }

    [Fact]
    public void PreviewEnforcesRowSizeAndColumnLimitsAndHandlesOversizedRecords()
    {
        var profile = CreateKeywordProfile();
        var wideHeader = "Test_Name,HARD_BIN,IGNORE_ME," + string.Join(',', Enumerable.Range(0, 800).Select(i => $"T_{i}"));
        var lines = new List<string> { wideHeader };
        for (var i = 0; i < 80; i++)
        {
            lines.Add($"DATA_START_{i},1," + string.Join(',', Enumerable.Repeat("1.0", 800)));
        }

        var text = string.Join('\n', lines);
        var preview = _previewReader.ReadPreview(
            profile,
            () => new StringReader(text),
            maxRows: 50,
            maxPreviewChars: 512 * 1024,
            maxColumnsPerRow: 128,
            cancellationToken: Token);

        Assert.True(preview.WasTruncatedByRowLimit);
        Assert.Equal(50, preview.RawLines.Count);
        Assert.Equal(50, preview.TableRows.Count);
        Assert.True(preview.TableRows[0].IsColumnTruncated);
        Assert.Equal(128, preview.TableRows[0].Cells.Count);
        Assert.True(preview.Structure!.IsSuccess);

        // Oversized single line capped by DatalogRecordTokenizer
        var tinyTokenizer = new DatalogRecordTokenizer(maxRecordChars: 64);
        var tinyPreview = new DatalogPreviewReader(tinyTokenizer, new DatalogStructureScanner(tinyTokenizer));
        var longLine = new string('X', 200);
        var badRes = tinyPreview.ReadPreview(profile, () => new StringReader(longLine), cancellationToken: Token);
        Assert.Contains(badRes.Diagnostics, d => d.Code == DatalogScanErrorCode.RecordTooLarge);

        // Cancellation
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            _previewReader.ReadPreview(profile, () => new StringReader(text), cancellationToken: cts.Token));
    }

    [Fact]
    public void Large100MbDatalogScansHeaderAndPreviewWithoutReadingCompleteFile()
    {
        var largePath = Path.Combine(_tempDir, "large_105mb.csv");
        var header = "Test_Name,HARD_BIN,SITE_NUM,IGNORE_ME,300,IDD\n";
        var unit = "Unit,bin,site,ms,mV,mA\n";
        var dataRow = "DATA_START,1,1,123.45,67.89," + new string('0', 200) + "\n";

        using (var fs = new FileStream(largePath, FileMode.Create, FileAccess.Write, FileShare.None, 65_536))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            sw.Write(header);
            sw.Write(unit);
            // Write ~105 MB
            var targetBytes = 105L * 1024 * 1024;
            while (fs.Length < targetBytes)
            {
                sw.Write(dataRow);
            }
        }

        var fileBytes = new FileInfo(largePath).Length;
        Assert.True(fileBytes >= 100L * 1024 * 1024);

        var profile = CreateKeywordProfile(siteColumn: new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" });
        long scanBytesRead = 0;

        using (var countingStream = new ByteCountingStream(File.OpenRead(largePath)))
        using (var sr = new StreamReader(countingStream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, 4096))
        {
            var scan = _scanner.Scan(profile, sr, Token);
            scanBytesRead = countingStream.BytesRead;
            Assert.True(scan.IsSuccess);
            Assert.Equal(3, scan.RowsScanned);
        }

        // Only a tiny bounded prefix of the 105 MB file was read (<= 16 KB), never the whole file.
        Assert.True(scanBytesRead <= 16_384, $"Scanner read {scanBytesRead} bytes from a {fileBytes}-byte file.");

        long previewBytesRead = 0;
        var preview = _previewReader.ReadPreview(profile, () =>
        {
            var cs = new ByteCountingStream(File.OpenRead(largePath));
            return new TrackingStreamReader(cs, bytes => previewBytesRead += bytes);
        }, cancellationToken: Token);

        Assert.True(preview.WasTruncatedByRowLimit);
        Assert.Equal(50, preview.RawLines.Count);
        Assert.True(previewBytesRead < 128 * 1024, $"Preview + scan read {previewBytesRead} bytes from a {fileBytes}-byte file.");
    }

    [Fact]
    public void StrictEncodingRejectsInvalidUtf8WithBomAndMalformedUtf16()
    {
        // Invalid UTF-8 after UTF-8 BOM (EF BB BF)
        var badUtf8Bom = Path.Combine(_tempDir, "bad_utf8_bom.csv");
        File.WriteAllBytes(badUtf8Bom, [0xEF, 0xBB, 0xBF, 0x80, 0xC0, (byte)'\n']);
        using (var opened = _opener.Open(badUtf8Bom, Token))
        {
            var scan = _scanner.Scan(CreateKeywordProfile(), opened.Reader, Token);
            Assert.False(scan.IsSuccess);
            Assert.Equal(DatalogScanErrorCode.InvalidEncoding, scan.Diagnostics[0].Code);
        }

        // Malformed UTF-16 LE (FF FE + unpaired high surrogate 0xD800)
        var badUtf16 = Path.Combine(_tempDir, "bad_utf16.csv");
        File.WriteAllBytes(badUtf16, [0xFF, 0xFE, 0x00, 0xD8, 0x41, 0x00, (byte)'\n', 0x00]);
        using (var opened16 = _opener.Open(badUtf16, Token))
        {
            var scan16 = _scanner.Scan(CreateKeywordProfile(), opened16.Reader, Token);
            Assert.False(scan16.IsSuccess);
            Assert.Equal(DatalogScanErrorCode.InvalidEncoding, scan16.Diagnostics[0].Code);
        }
    }

    [Fact]
    public void OversizedSingleLineAbortsBeforeFullStreamRead()
    {
        var hugeLinePath = Path.Combine(_tempDir, "huge_single_line.csv");
        using (var fs = new FileStream(hugeLinePath, FileMode.Create, FileAccess.Write, FileShare.None, 65_536))
        {
            var chunk = Enumerable.Repeat((byte)'A', 65_536).ToArray();
            for (var i = 0; i < 160; i++) // 10 MB without newline
            {
                fs.Write(chunk);
            }
        }

        var boundedTokenizer = new DatalogRecordTokenizer(maxRecordChars: 1024);
        var boundedScanner = new DatalogStructureScanner(boundedTokenizer);
        long bytesRead;
        DatalogStructureScanResult scan;

        using (var cs = new ByteCountingStream(File.OpenRead(hugeLinePath)))
        using (var sr = new StreamReader(cs, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, 4096))
        {
            scan = boundedScanner.Scan(CreateKeywordProfile(), sr, Token);
            bytesRead = cs.BytesRead;
        }

        Assert.False(scan.IsSuccess);
        Assert.Equal(DatalogScanErrorCode.RecordTooLarge, scan.Diagnostics[0].Code);
        Assert.True(bytesRead <= 16_384, $"Bounded record reader read {bytesRead} bytes of 10 MB line.");
    }

    [Fact]
    public void SplitQuotedCsvFieldIsRejectedInsteadOfSilentlyShiftingColumns()
    {
        var content = "Test_Name,HARD_BIN,\"A,B\"\nDATA_START,1,2\n";
        using var reader = new StringReader(content);
        var result = _scanner.Scan(CreateKeywordProfile(), reader, Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(DatalogScanErrorCode.UnsupportedQuotedField, result.Diagnostics[0].Code);
    }

    [Fact]
    public void ReopeningAtDataStartRowReadsFirstMeasurementRowWithoutOffByOne()
    {
        var csvPath = Path.Combine(_tempDir, "reopen_datastart.csv");
        File.WriteAllText(csvPath, "Preamble\nTest_Name,HARD_BIN,IGNORE_ME,300\nUnit,bin,ms,mV\nDATA_START,1,0,12.5\nDATA_START,1,0,13.5\n");

        var profile = CreateKeywordProfile();
        int dataStartRow;
        using (var opened = _opener.Open(csvPath, Token))
        {
            var scan = _scanner.Scan(profile, opened.Reader, Token);
            Assert.True(scan.IsSuccess);
            dataStartRow = scan.RowPositions!.DataStartRowNumber;
            Assert.Equal(4, dataStartRow);
        }

        var tokenizer = new DatalogRecordTokenizer();
        using var reopened = _opener.Open(csvPath, Token);
        var currentRow = 0;
        string? firstDataLine = null;
        while (tokenizer.ReadBoundedLine(reopened.Reader, Token) is { } line)
        {
            currentRow++;
            if (currentRow == dataStartRow)
            {
                firstDataLine = line;
                break;
            }
        }

        Assert.Equal("DATA_START,1,0,12.5", firstDataLine);
    }

    private sealed class TrackingStreamReader(ByteCountingStream stream, Action<long> onDispose)
        : StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, 4096)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                onDispose(stream.BytesRead);
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ByteCountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
