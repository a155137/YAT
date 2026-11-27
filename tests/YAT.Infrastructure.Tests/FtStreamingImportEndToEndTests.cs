using System.Globalization;
using System.IO.Compression;
using System.Text;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Analyses;
using YAT.Application.FtImport;
using YAT.Application.Graphs;
using YAT.Application.ImportProfiles;
using YAT.Application.Queries;
using YAT.Domain.Entities;
using YAT.Domain.Enums;
using YAT.Infrastructure.Persistence.DuckDb;

namespace YAT.Infrastructure.Tests;

public sealed class FtStreamingImportEndToEndTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 28, 1, 0, 0, TimeSpan.Zero);

    private readonly string _tempRoot;
    private readonly DuckDbProjectStorage _storage;
    private readonly FtFileDiscoveryService _discovery = new();
    private readonly FtSchemaPreparationService _schemaPrep = new();
    private readonly FtStreamingImportService _importer = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FtStreamingImportEndToEndTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "yat-ft03-3-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed record DatabaseSnapshot(
        long ProjectCount,
        long WorksheetCount,
        long ColumnCount,
        long RawBlockCount,
        long RawColumnCount,
        long BlockTableCount);

    private static DatabaseSnapshot CaptureSnapshot(DuckDbProjectDatabase db) =>
        db.Execute(connection => new DatabaseSnapshot(
            Count(connection, "SELECT count(*) FROM project"),
            Count(connection, "SELECT count(*) FROM worksheet"),
            Count(connection, "SELECT count(*) FROM worksheet_column"),
            Count(connection, "SELECT count(*) FROM raw_block"),
            Count(connection, "SELECT count(*) FROM raw_column"),
            Count(connection, "SELECT count(*) FROM duckdb_tables() WHERE database_name = current_database() AND schema_name = 'main' AND table_name LIKE 'blk_%'")));

    private static long Count(DuckDB.NET.Data.DuckDBConnection connection, string sql) =>
        Convert.ToInt64(DuckDbRawCatalog.Scalar(connection, sql), CultureInfo.InvariantCulture);

    private async Task<(DuckDbProjectDatabase Db, Project Project, Worksheet SeedSheet)> CreateProjectAsync(string? filePath = null)
    {
        var tp = new FixedTimeProvider(BaseTime);
        var db = filePath is null ? _storage.CreateTemporary() : _storage.Create(filePath);
        var projectRepo = new DuckDbProjectRepository(db);
        var worksheetRepo = new DuckDbWorksheetRepository(db);
        var columnRepo = new DuckDbWorksheetColumnRepository(db, tp);
        var rawStore = new DuckDbWorksheetRawDataStore(db);

        var project = new Project
        {
            Id = Guid.NewGuid(),
            Name = "FT033_Project",
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };
        await projectRepo.AddAsync(project, Token);

        var seed = new Worksheet
        {
            Id = Guid.NewGuid(),
            ProjectId = project.Id,
            Name = "ExistingSheet",
            RowCount = 1,
            ColumnCount = 1,
            CreatedAt = BaseTime,
            UpdatedAt = BaseTime
        };
        await worksheetRepo.AddAsync(seed, Token);
        var seedCol = new WorksheetColumn
        {
            Id = Guid.NewGuid(),
            WorksheetId = seed.Id,
            Index = 0,
            Name = "Seed",
            DataType = WorksheetDataType.Numeric
        };
        await columnRepo.AddAsync(seedCol, Token);
        await rawStore.WriteColumnsAsync(seed.Id, new RawDataBlock([new NumericRawDataColumn(seedCol.Id, [99.0])]), Token);
        return (db, project, seed);
    }

    private static ImportProfile CreateProfile(
        bool includeSite = true,
        IReadOnlyList<CompositeFieldRule>? compositeFields = null,
        IReadOnlyList<string>? goodBins = null,
        IReadOnlyList<string>? nonRetestFailBins = null,
        MissingValuePolicy missingPolicy = MissingValuePolicy.UseMissing,
        bool unnumberedRetest = false)
    {
        return new ImportProfile
        {
            Id = Guid.NewGuid(),
            Name = "FT033_Profile",
            SchemaVersion = ImportProfile.CurrentSchemaVersion,
            FilenameRules = new FilenameRuleSet
            {
                AcceptedExtensions = [".csv", ".Reg.zip", ".zip"],
                Mode = FilenameRuleMode.Structured,
                SegmentDelimiters = ['.', '_'],
                LotId = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.FixedRange, SegmentIndex = 4, StartIndex = 0, Length = 2 },
                Retest = new FilenameRetestRule
                {
                    RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 4 },
                    RetestMarker = "R",
                    RetestRoundField = unnumberedRetest
                        ? null
                        : new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 4, Marker = "R" }
                },
                FilePart = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 5 }
            },
            StructureRules = new DatalogStructureRuleSet
            {
                Delimiter = ",",
                CompositeFields = compositeFields ?? [],
                HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 1 },
                DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 2 },
                BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SOFT_BIN" },
                SiteColumn = includeSite ? new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" } : null,
                IgnoredColumns = [new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "UNIT_ID" }]
            },
            BinRules = new BinRuleSet
            {
                GoodBins = goodBins ?? ["01"],
                NonRetestFailBins = nonRetestFailBins ?? ["99"]
            },
            MissingValueRules = new MissingValueRuleSet
            {
                Markers = ["", "N/A"],
                Policy = missingPolicy
            }
        };
    }

    private static string NameOf(string lot, string stageSeg, int part, string ext = ".csv") =>
        $"E320BD-262.20261123111219.{lot}_TQB01RXAA1_{stageSeg}_{part}_20261122171516_20261123110911{ext}";

    private string WritePlain(string subDir, string fileName, params string[] lines)
    {
        var dir = Path.Combine(_tempRoot, subDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteZip(string subDir, string fileName, string entryName, params string[] lines)
    {
        var dir = Path.Combine(_tempRoot, subDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(string.Join("\n", lines) + "\n");
        return path;
    }

    private FtPreparedImportPlan PreparePlan(
        ImportProfile profile,
        string subDir,
        Action<FtImportSelectionModel>? configureFiles = null,
        IReadOnlyList<string>? selectedMeasurements = null)
    {
        var dir = Path.Combine(_tempRoot, subDir);
        var discovery = _discovery.DiscoverFolder(profile, dir, Token);
        var fileSel = new FtImportSelectionModel(discovery);
        configureFiles?.Invoke(fileSel);
        var planRes = fileSel.BuildImportPlan();
        Assert.True(planRes.IsValid, string.Join(" | ", planRes.Errors.Select(e => e.Message)));

        var scanRes = _schemaPrep.ScanUnionSchema(planRes.Plan!, Token);
        Assert.True(scanRes.IsSuccess, string.Join(" | ", scanRes.Errors.Select(e => e.Message)));

        var headers = selectedMeasurements ?? scanRes.UnionSchema!.UnionMeasurementHeaders;
        var prepRes = _schemaPrep.PrepareImportPlan(scanRes.UnionSchema!, headers, Token);
        Assert.True(prepRes.IsValid, string.Join(" | ", prepRes.Errors.Select(e => e.Message)));
        return prepRes.Plan!;
    }

    // -------------------------------------------------------------------------
    // A. Golden Dataset — Mandatory Row-by-Row & Column-by-Column Verification
    // -------------------------------------------------------------------------

    private sealed record ExpectedRow(
        string LotId,
        string Bin,
        string? Site,
        string TestRole,
        double? Idd,
        double? Als,
        double? Ps,
        double? Ir);

    [Fact]
    public async Task GoldenDataset_VerifiesExactRowsColumnsPagingGraphsStatisticsSaveAsAndReopenWithoutSources()
    {
        var profile = CreateProfile(goodBins: ["01"], nonRetestFailBins: ["99"], missingPolicy: MissingValuePolicy.UseMissing);
        var dataDir = Path.Combine(_tempRoot, "golden_src");

        // Lot A: Original + RT1 + RT2 (Part 1 plain + Part 2 ZIP)
        // Original (filtered: keep only "01" and "99"; "1", "001", "05" filtered out even with BAD_NUM!)
        // Schema: IDD, ALS (PS, IR absent -> 0.0)
        WritePlain("golden_src", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS",
            "1,01,0,0.0,10.5",          // retained: present zero + nonzero
            "2,1,0,BAD_NUM,20.0",       // filtered out ("1" != "01"), invalid numeric ignored
            "3,001,1,BAD_NUM,30.0",     // filtered out ("001" != "01")
            "4,99,,N/A,12.0",           // retained: empty site -> null, Missing IDD -> null
            "5,05,2,99.9,99.9");        // filtered out

        // Lot A RT1 (earlier round -> filtered: keep only "01" and "99")
        // Reordered schema: ALS, IDD, PS (IR absent -> 0.0)
        WritePlain("golden_src", NameOf("LOTA", "F1B45678R1", 1, ".csv"),
            "ALS,SITE_NUM,SOFT_BIN,UNIT_ID,IDD,PS",
            "15.0,1,01,6,1.25,2.5",     // retained
            "16.0,1,05,7,BAD,3.5",      // filtered out
            "17.0,2,99,8,0.0,N/A");     // retained: present zero IDD, Missing PS -> null

        // Lot A RT2 Part 1 (latest round -> retain ALL rows including "1", "001", "05")
        // Schema: PS, IDD (ALS, IR absent -> 0.0)
        WritePlain("golden_src", NameOf("LOTA", "F1B45678R2", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,PS,IDD",
            "9,01,0,4.0,2.0",           // retained
            "10,001,1,5.0,3.0");        // retained

        // Lot A RT2 Part 2 (ZIP, latest round -> retain ALL rows)
        // Schema: IR, IDD (ALS, PS absent -> 0.0)
        WriteZip("golden_src", NameOf("LOTA", "F1B45678R2", 2, ".Reg.zip"), "lota_r2_p2.csv",
            "SOFT_BIN,UNIT_ID,SITE_NUM,IR,IDD",
            "05,11,2,8.5,4.0");         // retained

        // Lot B: Original + RT1
        // Original (filtered: keep "01", "99")
        // Schema: IDD, IR (ALS, PS absent -> 0.0)
        WritePlain("golden_src", NameOf("LOTB", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,IR",
            "12,01,0,5.0,6.0",          // retained
            "13,1,0,NOT_NUM,7.0");      // filtered out

        // Lot B RT1 (latest round for LOTB -> retain ALL rows)
        // Schema: ALS, IR (IDD, PS absent -> 0.0)
        WritePlain("golden_src", NameOf("LOTB", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,ALS,IR",
            "14,1,1,20.0,9.0",          // retained ("1" retained in latest round!)
            "15,01,2,,10.0");           // retained (Missing ALS -> null)

        // Lot C: Original only (LatestRetestKind = None -> retain ALL rows)
        // Schema: IDD, ALS, PS, IR
        WritePlain("golden_src", NameOf("LOTC", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS,PS,IR",
            "16,05,0,6.0,7.0,8.0,9.0",  // retained
            "17,001,1,0.0,N/A,0.0,1.0"); // retained

        // Independent expected rows in deterministic FtImportPlan.OrderedFiles order:
        ExpectedRow[] expected =
        [
            // LOTA Original
            new("LOTA", "01",  "0",  "Original", 0.0,  10.5, 0.0,  0.0),
            new("LOTA", "99",  null, "Original", null, 12.0, 0.0,  0.0),
            // LOTA RT1
            new("LOTA", "01",  "1",  "Retest",   1.25, 15.0, 2.5,  0.0),
            new("LOTA", "99",  "2",  "Retest",   0.0,  17.0, null, 0.0),
            // LOTA RT2 Part 1
            new("LOTA", "01",  "0",  "Retest",   2.0,  0.0,  4.0,  0.0),
            new("LOTA", "001", "1",  "Retest",   3.0,  0.0,  5.0,  0.0),
            // LOTA RT2 Part 2 (ZIP)
            new("LOTA", "05",  "2",  "Retest",   4.0,  0.0,  0.0,  8.5),
            // LOTB Original
            new("LOTB", "01",  "0",  "Original", 5.0,  0.0,  0.0,  6.0),
            // LOTB RT1
            new("LOTB", "1",   "1",  "Retest",   0.0,  20.0, 0.0,  9.0),
            new("LOTB", "01",  "2",  "Retest",   0.0,  null, 0.0,  10.0),
            // LOTC Original-only
            new("LOTC", "05",  "0",  "Original", 6.0,  7.0,  8.0,  9.0),
            new("LOTC", "001", "1",  "Original", 0.0,  null, 0.0,  1.0)
        ];

        var preparedPlan = PreparePlan(profile, "golden_src");
        Assert.Equal(["IDD", "ALS", "PS", "IR"], preparedPlan.SelectedMeasurementHeaders);

        var projectPath = Path.Combine(_tempRoot, "golden.yat");
        var saveAsPath = Path.Combine(_tempRoot, "golden_copy.yat");
        var tp = new FixedTimeProvider(BaseTime);

        Guid worksheetId;
        IReadOnlyList<WorksheetColumn> createdColumns;
        var (db, project, _) = await CreateProjectAsync(projectPath);
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, tp);
            var progressEvents = new List<FtImportProgress>();
            var progress = new SyncProgress<FtImportProgress>(progressEvents.Add);

            var result = await _importer.ImportAsync(creator, project.Id, "Golden_FT", preparedPlan, progress, Token);
            Assert.True(result.IsSuccess, string.Join(" | ", result.Errors.Select(e => e.Message)));
            Assert.NotNull(result.Worksheet);
            Assert.Equal(17, result.TotalProcessedRowCount);
            Assert.Equal(expected.Length, result.TotalRetainedRowCount);
            Assert.Equal(5, result.TotalFilteredRowCount);
            Assert.Equal(expected.Length, result.Worksheet!.RowCount);
            Assert.Equal(8, result.Worksheet.ColumnCount);
            Assert.Equal(7, result.SourceSummaries.Count);

            // Progress phases: Validating -> StreamingSource -> Committing -> Completed.
            Assert.Equal(FtImportProgressPhase.Validating, progressEvents[0].Phase);
            Assert.Contains(progressEvents, p => p.Phase == FtImportProgressPhase.StreamingSource);
            Assert.Contains(progressEvents, p => p.Phase == FtImportProgressPhase.Committing);
            Assert.Equal(FtImportProgressPhase.Completed, progressEvents[^1].Phase);

            worksheetId = result.Worksheet.Id;
            createdColumns = result.Columns;

            // Save As (checkpoint + SaveCopyAsync)
            await db.CheckpointAsync(Token);
            await db.SaveCopyAsync(saveAsPath, Token);
        }

        // Delete all source datalog files to prove the .yat project is completely self-contained.
        Directory.Delete(dataDir, recursive: true);
        Assert.False(Directory.Exists(dataDir));

        // Reopen Save As copy and verify row-by-row, column-by-column, Graph queries, and Analysis statistics!
        using var reopened = _storage.Open(saveAsPath);
        var wsRepo = new DuckDbWorksheetRepository(reopened);
        var colRepo = new DuckDbWorksheetColumnRepository(reopened, tp);
        var rawStore = new DuckDbWorksheetRawDataStore(reopened);

        var pageService = new WorksheetDataQueryService(wsRepo, colRepo, rawStore);
        var page = await pageService.GetPageAsync(worksheetId, 0, 100, Token);
        Assert.Equal(expected.Length, page.RowCount);
        Assert.Equal(["Lot_ID", "Bin", "Site", "Test_Role", "IDD", "ALS", "PS", "IR"],
            page.Columns.Select(c => c.Column.Name).ToArray());

        var lotVals = Assert.IsType<StringRawDataColumn>(page.Columns[0].Values).Values;
        var binVals = Assert.IsType<StringRawDataColumn>(page.Columns[1].Values).Values;
        var siteVals = Assert.IsType<StringRawDataColumn>(page.Columns[2].Values).Values;
        var roleVals = Assert.IsType<StringRawDataColumn>(page.Columns[3].Values).Values;
        var iddVals = Assert.IsType<NumericRawDataColumn>(page.Columns[4].Values).Values;
        var alsVals = Assert.IsType<NumericRawDataColumn>(page.Columns[5].Values).Values;
        var psVals = Assert.IsType<NumericRawDataColumn>(page.Columns[6].Values).Values;
        var irVals = Assert.IsType<NumericRawDataColumn>(page.Columns[7].Values).Values;

        for (var r = 0; r < expected.Length; r++)
        {
            Assert.Equal(expected[r].LotId, lotVals[r]);
            Assert.Equal(expected[r].Bin, binVals[r]);
            Assert.Equal(expected[r].Site, siteVals[r]);
            Assert.Equal(expected[r].TestRole, roleVals[r]);
            Assert.Equal(expected[r].Idd, iddVals[r]);
            Assert.Equal(expected[r].Als, alsVals[r]);
            Assert.Equal(expected[r].Ps, psVals[r]);
            Assert.Equal(expected[r].Ir, irVals[r]);
        }

        // GraphDataQueryService (Histogram over IDD grouped by Lot_ID)
        var graphService = new GraphDataQueryService(wsRepo, colRepo, rawStore);
        var graphData = await graphService.LoadAsync(
            new GraphConfiguration(
                GraphType.Histogram,
                worksheetId,
                [
                    new GraphColumnAssignment(GraphVariableRole.Variable, createdColumns[4].Id), // IDD
                    new GraphColumnAssignment(GraphVariableRole.Group, createdColumns[0].Id)     // Lot_ID
                ]),
            Token);
        // 12 rows, 1 Missing IDD -> 11 non-missing observations (including absent-column 0.0s in LOTB RT1!).
        Assert.Equal(11, graphData.Count);

        // AnalysisDataQueryService: verify absent-column zeros contribute to statistics while Missing cells stay null!
        var analysisService = new AnalysisDataQueryService(wsRepo, colRepo, rawStore);
        var analysisData = await analysisService.LoadAsync(
            new AnalysisConfiguration(
                worksheetId,
                [createdColumns[4].Id], // IDD
                GroupColumnId: null),
            Token);
        Assert.Equal(12, analysisData.RowCount);
        var iddSpan = analysisData.Variables[0].Values.Span;
        var nonNullIdd = new List<double>();
        for (var i = 0; i < iddSpan.Length; i++)
        {
            if (iddSpan[i] is { } v)
            {
                nonNullIdd.Add(v);
            }
        }

        Assert.Equal(11, nonNullIdd.Count);
        // Sum of expected IDD = 0 + 1.25 + 0 + 2 + 3 + 4 + 5 + 0 + 0 + 6 + 0 = 21.25; Mean = 21.25 / 11
        Assert.Equal(21.25 / 11.0, nonNullIdd.Average(), precision: 10);
    }

    // -------------------------------------------------------------------------
    // B. Additional Coverage: Wide >50 Cols, Composites, Optional Site, Unnumbered/Retest-Only/Excluded Round
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Import_HandlesWideSchemaOver50ColumnsCaseSensitiveLeadingZeroCompositesAndNoSite()
    {
        var profile = CreateProfile(
            includeSite: false,
            compositeFields:
            [
                new CompositeFieldRule(
                    new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "COMP_A" },
                    ";",
                    ["1001_IDD", "001_VTH"])
            ]);

        var extraCols = Enumerable.Range(1, 56).Select(i => $"T_{i:D3}").ToArray();
        var header = "UNIT_ID,SOFT_BIN,COMP_A,IDD,idd," + string.Join(",", extraCols);
        var row1 = "1,01,1.1;0.2,2.2,3.3," + string.Join(",", Enumerable.Range(1, 56).Select(i => i.ToString(CultureInfo.InvariantCulture)));
        WritePlain("wide", NameOf("LOTA", "F1", 1, ".csv"), header, row1);

        var plan = PreparePlan(profile, "wide");
        Assert.Equal(60, plan.SelectedMeasurementHeaders.Count); // 2 composite + IDD + idd + 56 = 60 (> 50!)
        Assert.Equal(64, plan.OutputColumns.Count);

        var (db, project, _) = await CreateProjectAsync();
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));
            var res = await _importer.ImportAsync(creator, project.Id, "Wide_60", plan, cancellationToken: Token);

            Assert.True(res.IsSuccess, string.Join(" | ", res.Errors.Select(e => e.Message)));
            Assert.Equal(1, res.TotalRetainedRowCount);
            Assert.Equal(64, res.Columns.Count);

            var rawStore = new DuckDbWorksheetRawDataStore(db);
            var block = await rawStore.ReadColumnsAsync(res.Worksheet!.Id, res.Columns.Select(c => c.Id).ToArray(), 0, 10, Token);
            // Site is null (Missing) when SiteColumn is not configured.
            Assert.Null(Assert.IsType<StringRawDataColumn>(block.Columns[2]).Values[0]);
            Assert.Equal(1.1, Assert.IsType<NumericRawDataColumn>(block.Columns[4]).Values[0]);  // 1001_IDD
            Assert.Equal(0.2, Assert.IsType<NumericRawDataColumn>(block.Columns[5]).Values[0]);  // 001_VTH
            Assert.Equal(2.2, Assert.IsType<NumericRawDataColumn>(block.Columns[6]).Values[0]);  // IDD
            Assert.Equal(3.3, Assert.IsType<NumericRawDataColumn>(block.Columns[7]).Values[0]);  // idd
            Assert.Equal(56.0, Assert.IsType<NumericRawDataColumn>(block.Columns[63]).Values[0]); // T_056
        }
    }

    [Fact]
    public async Task Import_HandlesUnnumberedRetestRetestOnlyAndExcludedHighestRound()
    {
        // 1. Unnumbered Retest: Original filtered, all unnumbered Retest retained.
        var unnumProfile = CreateProfile(unnumberedRetest: true);
        WritePlain("unnum", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,1.0",
            "2,05,0,2.0"); // filtered
        WritePlain("unnum", NameOf("LOTA", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "3,05,0,3.0"); // retained (unnumbered retest)

        var unnumPlan = PreparePlan(unnumProfile, "unnum");

        // 2. Excluded highest round (R2 excluded -> R1 becomes latest selected round -> retains all rows) + Retest-only LOTB.
        var numProfile = CreateProfile();
        WritePlain("excl", NameOf("LOTA", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,05,0,10.0"); // retained because R2 is excluded
        WritePlain("excl", NameOf("LOTA", "F1B45678R2", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "2,01,0,20.0"); // excluded

        var exclPlan = PreparePlan(numProfile, "excl", sel =>
        {
            var r2 = sel.Discovery.Files.Single(f => f.Match?.RetestRound == 2);
            sel.SetFileSelected(r2.Id, false);
        });

        var (db, project, _) = await CreateProjectAsync();
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));

            var r1 = await _importer.ImportAsync(creator, project.Id, "Unnum_FT", unnumPlan, cancellationToken: Token);
            Assert.True(r1.IsSuccess);
            Assert.Equal(2, r1.TotalRetainedRowCount);

            var r2 = await _importer.ImportAsync(creator, project.Id, "Excl_FT", exclPlan, cancellationToken: Token);
            Assert.True(r2.IsSuccess);
            Assert.Equal(1, r2.TotalRetainedRowCount);
        }
    }

    // -------------------------------------------------------------------------
    // C. Safety, Rollback, MissingValuePolicies, Zero Rows, Stale Sources, Cancellation, Progress Faults
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Import_MissingValuePolicies_ReplaceWithZeroSucceedsAndStopImportRollsBack()
    {
        WritePlain("mv_zero", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,N/A");
        var zeroPlan = PreparePlan(CreateProfile(missingPolicy: MissingValuePolicy.ReplaceWithZero), "mv_zero");

        WritePlain("mv_stop", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,N/A");
        var stopPlan = PreparePlan(CreateProfile(missingPolicy: MissingValuePolicy.StopImport), "mv_stop");

        var (db, project, _) = await CreateProjectAsync();
        using (db)
        {
            var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));
            var ok = await _importer.ImportAsync(creator, project.Id, "MV_Zero", zeroPlan, cancellationToken: Token);
            Assert.True(ok.IsSuccess);
            var rawStore = new DuckDbWorksheetRawDataStore(db);
            var block = await rawStore.ReadColumnsAsync(ok.Worksheet!.Id, [ok.Columns[4].Id], 0, 10, Token);
            Assert.Equal(0.0, Assert.IsType<NumericRawDataColumn>(block.Columns[0]).Values[0]);

            var before = CaptureSnapshot(db);
            var fail = await _importer.ImportAsync(creator, project.Id, "MV_Stop", stopPlan, cancellationToken: Token);
            Assert.False(fail.IsSuccess);
            Assert.Contains(fail.Errors, e => e.Code == FtImportDiagnosticCode.RowParseFailed);
            Assert.Equal(before, CaptureSnapshot(db));
        }
    }

    [Fact]
    public async Task Import_FailureInFinalFileRollsBackCompletelyAndAllowsRetryWithSameName()
    {
        var profile = CreateProfile();
        // File 1 writes valid rows; File 2 has invalid numeric value -> entire transaction must roll back!
        WritePlain("fail_last", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,1.0",
            "2,01,0,2.0");
        var badFile = WritePlain("fail_last", NameOf("LOTB", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "3,01,0,NOT_A_NUMBER");

        var plan = PreparePlan(profile, "fail_last");

        var (db, project, _) = await CreateProjectAsync();
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));

            var failRes = await _importer.ImportAsync(creator, project.Id, "AtomicRetrySheet", plan, cancellationToken: Token);
            Assert.False(failRes.IsSuccess);
            Assert.Contains(failRes.Errors, e => e.Code == FtImportDiagnosticCode.RowParseFailed && e.SourcePath == badFile);
            Assert.Equal(before, CaptureSnapshot(db));

            // Fix file, rescan, and retry with the exact same Worksheet name -> must succeed!
            File.WriteAllText(badFile, "UNIT_ID,SOFT_BIN,SITE_NUM,IDD\n3,01,0,3.0\n", new UTF8Encoding(false));
            var fixedPlan = PreparePlan(profile, "fail_last");
            var retryRes = await _importer.ImportAsync(creator, project.Id, "AtomicRetrySheet", fixedPlan, cancellationToken: Token);
            Assert.True(retryRes.IsSuccess);
            Assert.Equal(3, retryRes.TotalRetainedRowCount);
        }
    }

    [Fact]
    public async Task Import_NumericOverflowZeroRetainedRowsAndChangedOrMissingSourceRollBackWithoutOrphans()
    {
        var profile = CreateProfile();

        // 1. Numeric overflow
        WritePlain("overflow", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,1e500");
        var overflowPlan = PreparePlan(profile, "overflow");

        // 2. Zero retained rows (filtered Original file where every row's Bin is excluded -> 0 total retained)
        WritePlain("zero_rows", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,05,0,1.0");
        WritePlain("zero_rows", NameOf("LOTA", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "2,05,0,2.0");
        var zeroBasePlan = PreparePlan(profile, "zero_rows");
        // Keep only the filtered Original source so 1 row is processed and 0 rows are retained.
        var zeroPlan = zeroBasePlan with { Sources = Array.AsReadOnly([zeroBasePlan.Sources[0]]) };

        // 3. Source changed on disk after preparation (stale stamp)
        var staleFile = WritePlain("stale", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,1.0");
        var stalePlan = PreparePlan(profile, "stale");
        File.AppendAllText(staleFile, "2,01,0,2.0\n");

        var (db, project, _) = await CreateProjectAsync();
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));

            var ovRes = await _importer.ImportAsync(creator, project.Id, "OverflowSheet", overflowPlan, cancellationToken: Token);
            Assert.False(ovRes.IsSuccess);
            Assert.Contains(ovRes.Errors, e => e.Code == FtImportDiagnosticCode.RowParseFailed);
            Assert.Equal(before, CaptureSnapshot(db));

            var zeroRes = await _importer.ImportAsync(creator, project.Id, "ZeroSheet", zeroPlan, cancellationToken: Token);
            Assert.False(zeroRes.IsSuccess);
            Assert.Contains(zeroRes.Errors, e => e.Code == FtImportDiagnosticCode.ZeroRetainedRows);
            Assert.Equal(before, CaptureSnapshot(db));

            var staleRes = await _importer.ImportAsync(creator, project.Id, "StaleSheet", stalePlan, cancellationToken: Token);
            Assert.False(staleRes.IsSuccess);
            Assert.Contains(staleRes.Errors, e => e.Code == FtImportDiagnosticCode.SourceFileStaleOrMissing);
            Assert.Equal(before, CaptureSnapshot(db));

            // Missing source file
            File.Delete(staleFile);
            var missingRes = await _importer.ImportAsync(creator, project.Id, "MissingSheet", stalePlan, cancellationToken: Token);
            Assert.False(missingRes.IsSuccess);
            Assert.Contains(missingRes.Errors, e => e.Code == FtImportDiagnosticCode.SourceFileStaleOrMissing);
            Assert.Equal(before, CaptureSnapshot(db));
        }
    }

    [Fact]
    public async Task Import_CancellationBeforeDuringAndAfterCommit_AndFaultyProgressObserverBehavior()
    {
        var profile = CreateProfile();
        WritePlain("cancel_prog", NameOf("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,1.0",
            "2,01,0,2.0");
        var plan = PreparePlan(profile, "cancel_prog");

        var (db, project, _) = await CreateProjectAsync();
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));

            // 1. Pre-cancelled token -> throws OperationCanceledException, DB untouched.
            using var preCts = new CancellationTokenSource();
            preCts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _importer.ImportAsync(creator, project.Id, "PreCancel", plan, cancellationToken: preCts.Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 2. Cancellation during transaction before commit -> rolls back completely.
            using var midCts = new CancellationTokenSource();
            creator.PhaseHook = phase =>
            {
                if (phase == DuckDbAtomicWorksheetPhase.BeforeCommit)
                {
                    midCts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _importer.ImportAsync(creator, project.Id, "MidCancel", plan, cancellationToken: midCts.Token));
            Assert.Equal(before, CaptureSnapshot(db));

            // 3. Cancellation AFTER commit + throwing progress observer -> MUST STILL return committed result!
            using var postCts = new CancellationTokenSource();
            creator.PhaseHook = phase =>
            {
                if (phase == DuckDbAtomicWorksheetPhase.AfterCommit)
                {
                    postCts.Cancel();
                }
            };
            var throwingProgress = new SyncProgress<FtImportProgress>(_ => throw new InvalidOperationException("UI observer crashed"));
            var committed = await _importer.ImportAsync(creator, project.Id, "PostCancelAndObserverFault", plan, throwingProgress, postCts.Token);

            Assert.True(committed.IsSuccess);
            Assert.NotNull(committed.Worksheet);
            Assert.Equal(2, committed.TotalRetainedRowCount);
            Assert.Contains(committed.Warnings, w => w.Code == FtImportDiagnosticCode.ProgressObserverFailed);
        }
    }

    [Fact]
    public async Task Import_CorruptZipDuringStreamingFailsAndRollsBackCleanly()
    {
        var profile = CreateProfile();
        var zipPath = WriteZip("corrupt_stream", NameOf("LOTA", "F1", 1, ".Reg.zip"), "data.csv",
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,01,0,1.0");
        var plan = PreparePlan(profile, "corrupt_stream");

        // Overwrite ZIP with same-length corrupt bytes and restore original LastWriteTimeUtc so stamp matches
        // and DatalogRowParser's corrupt-archive handling is exercised directly during streaming.
        var originalStamp = plan.Sources[0].FileStamp!;
        var corruptBytes = new byte[originalStamp.LengthBytes];
        Array.Fill(corruptBytes, (byte)0x55);
        File.WriteAllBytes(zipPath, corruptBytes);
        File.SetLastWriteTimeUtc(zipPath, originalStamp.LastWriteTimeUtc);

        var (db, project, _) = await CreateProjectAsync();
        using (db)
        {
            var before = CaptureSnapshot(db);
            var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));
            var res = await _importer.ImportAsync(creator, project.Id, "CorruptZipSheet", plan, cancellationToken: Token);

        Assert.False(res.IsSuccess);
        Assert.Contains(res.Errors, e => e.Code == FtImportDiagnosticCode.RowParseFailed && e.SourcePath == zipPath);
        Assert.Equal(before, CaptureSnapshot(db));
    }
}

[Fact]
public async Task Import_PersistsExactCompositeAndInterspersedAbsentValues()
{
    var profile = CreateProfile(
        compositeFields:
        [
            new CompositeFieldRule(
                new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "PRE_COMP" },
                ";",
                ["1001_IDD", "001_VTH"]),
            new CompositeFieldRule(
                new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "POST_COMP" },
                ";",
                ["IDD", "idd"])
        ]);

    // LOTA: PRE_COMP (1001_IDD, 001_VTH), EXTRA_A, POST_COMP (IDD, idd). Absent: ALS, 1002_IDD.
    WritePlain("p0_comp_persist", NameOf("LOTA", "F1", 1, ".csv"),
        "UNIT_ID,SOFT_BIN,SITE_NUM,PRE_COMP,EXTRA_A,POST_COMP",
        "1,01,0,11.1;0.25,7.7,22.2;33.3");

    // LOTB: reordered columns with ALS, POST_COMP, 1002_IDD, PRE_COMP. Absent: EXTRA_A.
    WritePlain("p0_comp_persist", NameOf("LOTB", "F1", 1, ".csv"),
        "ALS,POST_COMP,SITE_NUM,1002_IDD,SOFT_BIN,UNIT_ID,PRE_COMP",
        "99.9,44.4;55.5,1,12.3,01,2,10.5;0.15");

    var plan = PreparePlan(profile, "p0_comp_persist");
    Assert.Equal(["1001_IDD", "001_VTH", "EXTRA_A", "IDD", "idd", "ALS", "1002_IDD"], plan.SelectedMeasurementHeaders);

    var (db, project, _) = await CreateProjectAsync();
    using (db)
    {
        var tp = new FixedTimeProvider(BaseTime);
        var creator = new DuckDbAtomicWorksheetCreator(db, tp);
        var res = await _importer.ImportAsync(creator, project.Id, "CompPersist", plan, cancellationToken: Token);
        Assert.True(res.IsSuccess, string.Join(" | ", res.Errors.Select(e => e.Message)));

        var page = await new WorksheetDataQueryService(
            new DuckDbWorksheetRepository(db),
            new DuckDbWorksheetColumnRepository(db, tp),
            new DuckDbWorksheetRawDataStore(db)).GetPageAsync(res.Worksheet!.Id, 0, 10, Token);

        Assert.Equal(2, page.RowCount);
        double?[] expectedRow0 = [11.1, 0.25, 7.7, 22.2, 33.3, 0.0, 0.0];
        double?[] expectedRow1 = [10.5, 0.15, 0.0, 44.4, 55.5, 99.9, 12.3];
        for (var i = 0; i < 7; i++)
        {
            var colVals = Assert.IsType<NumericRawDataColumn>(page.Columns[4 + i].Values).Values;
            Assert.Equal(expectedRow0[i], colVals[0]);
            Assert.Equal(expectedRow1[i], colVals[1]);
        }
    }
}

[Fact]
public async Task Import_RejectsChangedHeaderAfterPreparationAndDetectsSameStampWidthMismatch()
{
    var profile = CreateProfile();
    var file1 = WritePlain("hdr_change", NameOf("LOTA", "F1", 1, ".csv"),
        "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS",
        "1,01,0,1.0,2.0");
    var plan = PreparePlan(profile, "hdr_change");

    // 1. Header changed (different length) -> caught by stamp check before reusing prepared mapping.
    File.WriteAllText(file1, "UNIT_ID,SOFT_BIN,SITE_NUM,ALS,IDD,EXTRA\n1,01,0,2.0,1.0,3.0\n", new UTF8Encoding(false));
    var (db, project, _) = await CreateProjectAsync();
    using (db)
    {
        var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));
        var staleRes = await _importer.ImportAsync(creator, project.Id, "ChangedHeader", plan, cancellationToken: Token);
        Assert.False(staleRes.IsSuccess);
        Assert.Contains(staleRes.Errors, e => e.Code == FtImportDiagnosticCode.SourceFileStaleOrMissing);

        // 2. Simulated same-length + same-timestamp structural incompatibility (extra delimiter in data row).
        var stamp = plan.Sources[0].FileStamp!;
        var badData = "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS\n1,01,0,1,2,3\n".PadRight((int)stamp.LengthBytes, ' ');
        File.WriteAllText(file1, badData[..(int)stamp.LengthBytes], new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(file1, stamp.LastWriteTimeUtc);

        var widthRes = await _importer.ImportAsync(creator, project.Id, "WidthMismatch", plan, cancellationToken: Token);
        Assert.False(widthRes.IsSuccess);
        Assert.Contains(widthRes.Errors, e => e.Code == FtImportDiagnosticCode.RowParseFailed);
    }
}

[Fact]
public async Task Import_CancellationBetweenFilesRollsBackAndReleasesPlainAndZipHandlesAcrossOutcomes()
{
    var profile = CreateProfile();
    var p1 = WritePlain("handles", NameOf("LOTA", "F1", 1, ".csv"),
        "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
        "1,01,0,1.0");
    var p2 = WriteZip("handles", NameOf("LOTB", "F1", 1, ".Reg.zip"), "b.csv",
        "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
        "2,01,0,2.0");
    var plan = PreparePlan(profile, "handles");

    var (db, project, _) = await CreateProjectAsync();
    using (db)
    {
        var before = CaptureSnapshot(db);
        var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));

        // 1. Cancel between file 1 and file 2 -> rolls back file 1 and releases handles.
        using var cts = new CancellationTokenSource();
        var betweenProgress = new SyncProgress<FtImportProgress>(p =>
        {
            if (p.Phase == FtImportProgressPhase.StreamingSource && p.CurrentFileIndex == 2)
            {
                cts.Cancel();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _importer.ImportAsync(creator, project.Id, "CancelBetween", plan, betweenProgress, cts.Token));
        Assert.Equal(before, CaptureSnapshot(db));
        using (new FileStream(p1, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (new FileStream(p2, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

        // 2. Success -> releases both plain and ZIP handles.
        var okRes = await _importer.ImportAsync(creator, project.Id, "HandlesOk", plan, cancellationToken: Token);
        Assert.True(okRes.IsSuccess);
        using (new FileStream(p1, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (new FileStream(p2, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    // 3. Failure in ZIP -> releases handles.
    var badZipDir = "handles_fail";
    var badPlain = WritePlain(badZipDir, NameOf("LOTA", "F1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,01,0,1.0");
    var badZip = WriteZip(badZipDir, NameOf("LOTB", "F1", 1, ".Reg.zip"), "b.csv", "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "2,01,0,BAD");
    var failPlan = PreparePlan(profile, badZipDir);
    var (db2, proj2, _) = await CreateProjectAsync();
    using (db2)
    {
        var failRes = await _importer.ImportAsync(new DuckDbAtomicWorksheetCreator(db2, new FixedTimeProvider(BaseTime)), proj2.Id, "FailHandles", failPlan, cancellationToken: Token);
        Assert.False(failRes.IsSuccess);
        using (new FileStream(badPlain, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (new FileStream(badZip, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }
}

[Fact]
public async Task Import_ProgressCountersAreMonotonicThrottledAccurateAndCompletedOnlyAfterCommit()
{
    var profile = CreateProfile();
    var lines = new List<string>(20_001) { "UNIT_ID,SOFT_BIN,SITE_NUM,IDD" };
    for (var i = 0; i < 20_000; i++)
    {
        lines.Add($"{i},{(i % 5 == 0 ? "05" : "01")},0,1.5");
    }

    WritePlain("prog_20k", NameOf("LOTA", "F1", 1, ".csv"), lines.ToArray());
    WritePlain("prog_20k", NameOf("LOTA", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,05,0,2.5");
    var plan = PreparePlan(profile, "prog_20k");

    var (db, project, _) = await CreateProjectAsync();
    using (db)
    {
        var creator = new DuckDbAtomicWorksheetCreator(db, new FixedTimeProvider(BaseTime));
        var committedObserved = false;
        var completedBeforeCommit = false;
        var events = new List<FtImportProgress>();

        creator.PhaseHook = phase =>
        {
            if (phase == DuckDbAtomicWorksheetPhase.AfterCommit)
            {
                committedObserved = true;
            }
        };

        var progress = new SyncProgress<FtImportProgress>(p =>
        {
            if (p.Phase == FtImportProgressPhase.Completed && !committedObserved)
            {
                completedBeforeCommit = true;
            }

            events.Add(p);
        });

        var res = await _importer.ImportAsync(creator, project.Id, "Progress20K", plan, progress, Token);
        Assert.True(res.IsSuccess);
        Assert.False(completedBeforeCommit);
        Assert.True(committedObserved);

        // Monotonic counters and throttled notifications (< 20 callbacks for 20,001 rows).
        Assert.InRange(events.Count, 4, 20);
        for (var i = 1; i < events.Count; i++)
        {
            Assert.True(events[i].ProcessedRowCount >= events[i - 1].ProcessedRowCount);
            Assert.True(events[i].RetainedRowCount >= events[i - 1].RetainedRowCount);
            Assert.True(events[i].FilteredRowCount >= events[i - 1].FilteredRowCount);
        }

        var final = events[^1];
        Assert.Equal(FtImportProgressPhase.Completed, final.Phase);
        Assert.Equal(20_001, final.ProcessedRowCount);
        Assert.Equal(16_001, final.RetainedRowCount);
        Assert.Equal(4_000, final.FilteredRowCount);
    }
}

private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
