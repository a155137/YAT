using System.IO.Compression;
using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.Domain.Enums;

namespace YAT.Application.Tests;

public sealed class FtSchemaPreparationAndFilteringTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FtFileDiscoveryService _discovery = new();
    private readonly FtSchemaPreparationService _service = new();
    private readonly DatalogRowParser _parser = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FtSchemaPreparationAndFilteringTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft03-2-tests", Guid.NewGuid().ToString("N"));
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
        bool includeSite = true,
        IReadOnlyList<CompositeFieldRule>? compositeFields = null,
        IReadOnlyList<ColumnSelector>? ignoredColumns = null,
        IReadOnlyList<string>? goodBins = null,
        IReadOnlyList<string>? nonRetestFailBins = null,
        MissingValuePolicy missingPolicy = MissingValuePolicy.UseMissing,
        bool unnumberedRetest = false)
    {
        var baseProfile = FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile();
        return baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                AcceptedExtensions = [".csv", ".Reg.zip", ".zip"],
                Retest = unnumberedRetest
                    ? baseProfile.FilenameRules.Retest with { RetestRoundField = null }
                    : baseProfile.FilenameRules.Retest
            },
            StructureRules = new DatalogStructureRuleSet
            {
                Delimiter = ",",
                CompositeFields = compositeFields ?? [],
                HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 1 },
                DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 2 },
                BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SOFT_BIN" },
                SiteColumn = includeSite ? new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" } : null,
                IgnoredColumns = ignoredColumns ?? [new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "UNIT_ID" }]
            },
            BinRules = new BinRuleSet
            {
                GoodBins = goodBins ?? ["1", "01"],
                NonRetestFailBins = nonRetestFailBins ?? ["2", "09"]
            },
            MissingValueRules = new MissingValueRuleSet
            {
                Markers = ["", "N/A"],
                Policy = missingPolicy
            }
        };
    }

    private static string RepresentativeName(string lot, string stageSeg, int part, string ext = ".csv") =>
        $"E320BD-262.20261123111219.{lot}_TQB01RXAA1_{stageSeg}_{part}_20261122171516_20261123110911{ext}";

    private string WritePlainFile(string subDir, string fileName, params string[] lines)
    {
        var dir = Path.Combine(_tempDir, subDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private string WriteZipFile(string subDir, string fileName, string entryName, params string[] lines)
    {
        var dir = Path.Combine(_tempDir, subDir);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(string.Join("\n", lines) + "\n");
        return path;
    }

    private FtImportPlan BuildValidPlan(ImportProfile profile, string subDir, Action<FtImportSelectionModel>? configureSelection = null)
    {
        var dir = Path.Combine(_tempDir, subDir);
        var discovery = _discovery.DiscoverFolder(profile, dir, Token);
        var selection = new FtImportSelectionModel(discovery);
        configureSelection?.Invoke(selection);
        var result = selection.BuildImportPlan();
        Assert.True(result.IsValid, string.Join(" | ", result.Errors.Select(e => e.Message)));
        return result.Plan!;
    }

    // -------------------------------------------------------------------------
    // A. Structure Preparation
    // -------------------------------------------------------------------------

    [Fact]
    public void ScanUnionSchema_HandlesMultipleLotsOriginalRetestFilePartsPlainAndZipAndReorderedColumns()
    {
        var profile = CreateProfile();
        WritePlainFile("multi", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS",
            "1,1,0,1.1,2.2");
        WriteZipFile("multi", RepresentativeName("LOT001", "F1B45678R1", 1, ".Reg.zip"), "data.csv",
            "ALS,SITE_NUM,UNIT_ID,SOFT_BIN,PS",
            "2.5,1,2,1,3.3");
        WritePlainFile("multi", RepresentativeName("LOT002", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "3,1,0,1.2");
        WriteZipFile("multi", RepresentativeName("LOT002", "F1", 2, ".Reg.zip"), "part2.csv",
            "SOFT_BIN,UNIT_ID,SITE_NUM,IR,IDD",
            "1,4,2,4.4,1.3");

        var plan = BuildValidPlan(profile, "multi");
        var scanResult = _service.ScanUnionSchema(plan, Token);

        Assert.True(scanResult.IsSuccess);
        Assert.NotNull(scanResult.UnionSchema);
        Assert.Equal(4, scanResult.UnionSchema!.ScannedFiles.Count);
        Assert.Equal(["IDD", "ALS", "PS", "IR"], scanResult.UnionSchema.UnionMeasurementHeaders);
        Assert.Null(scanResult.UnionSchema.ScannedFiles[0].ArchiveEntryName);
        Assert.Equal("data.csv", scanResult.UnionSchema.ScannedFiles[1].ArchiveEntryName);
        Assert.NotNull(scanResult.UnionSchema.ScannedFiles[0].FileStamp);
        Assert.True(scanResult.UnionSchema.ScannedFiles[0].FileStamp!.LengthBytes > 0);
    }

    [Fact]
    public void ScanUnionSchema_SupportsCompositeFieldsAndMissingOptionalSiteColumn()
    {
        var profile = CreateProfile(
            includeSite: false,
            compositeFields:
            [
                new CompositeFieldRule(
                    new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "COMP_DATA" },
                    ";",
                    ["COMP_IDD", "COMP_VTH"])
            ]);

        WritePlainFile("comp", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,COMP_DATA,ALS",
            "1,1,1.25;0.75,10.0");

        var plan = BuildValidPlan(profile, "comp");
        var scanResult = _service.ScanUnionSchema(plan, Token);
        Assert.True(scanResult.IsSuccess);
        Assert.Equal(["COMP_IDD", "COMP_VTH", "ALS"], scanResult.UnionSchema!.UnionMeasurementHeaders);
        Assert.False(scanResult.UnionSchema.ScannedFiles[0].Structure.HasConfiguredSiteColumn);
        Assert.Null(scanResult.UnionSchema.ScannedFiles[0].Structure.SiteColumn);

        var prep = _service.PrepareImportPlan(scanResult.UnionSchema, ["COMP_IDD", "COMP_VTH", "ALS"], Token);
        Assert.True(prep.IsValid);
        // Site metadata column remains present in output schema even when Profile does not define SiteColumn.
        Assert.Equal(["Lot_ID", "Bin", "Site", "Test_Role", "COMP_IDD", "COMP_VTH", "ALS"],
            prep.Plan!.OutputColumns.Select(c => c.Name).ToArray());
        Assert.Equal(WorksheetDataType.String, prep.Plan.OutputColumns[2].DataType);
        Assert.Equal(ColumnSemanticType.Site, prep.Plan.OutputColumns[2].SemanticType);

        // Verify composite-expanded measurement mapping indices and row parsing.
        var mapping = prep.Plan.Sources[0].MeasurementMapping;
        Assert.Equal(2, mapping.Columns[0].SourceColumnIndex); // COMP_IDD
        Assert.Equal(3, mapping.Columns[1].SourceColumnIndex); // COMP_VTH
        Assert.Equal(4, mapping.Columns[2].SourceColumnIndex); // ALS
        Assert.All(mapping.Columns, c => Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, c.Presence));

        double?[]? parsedValues = null;
        var parseRes = _parser.ParseFile(
            prep.Plan.Sources[0].SourceFile.SourcePath,
            prep.Plan.ProfileSnapshot,
            prep.Plan.Sources[0].Structure,
            prep.Plan.SelectedMeasurementHeaders,
            row => parsedValues = row.Measurements.ToArray(),
            includeBin: prep.Plan.Sources[0].BinRetentionPolicy.IncludeBinPredicate,
            cancellationToken: Token);
        Assert.True(parseRes.IsSuccess);
        Assert.Equal([1.25, 0.75, 10.0], parsedValues);
    }

    [Fact]
    public void ScanUnionSchema_RejectsInvalidSelectedSourceStructureAndDoesNotSilentlySkip()
    {
        var profile = CreateProfile();
        WritePlainFile("invalid_src", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,1,0,1.0");
        // Missing SOFT_BIN column in second selected file.
        var badPath = WritePlainFile("invalid_src", RepresentativeName("LOT002", "F1", 1, ".csv"),
            "UNIT_ID,SITE_NUM,IDD",
            "1,0,1.0");

        var plan = BuildValidPlan(profile, "invalid_src");
        var scanResult = _service.ScanUnionSchema(plan, Token);

        Assert.False(scanResult.IsSuccess);
        Assert.Null(scanResult.UnionSchema);
        var err = Assert.Single(scanResult.Errors);
        Assert.Equal(FtImportDiagnosticCode.StructureScanFailed, err.Code);
        Assert.Equal(badPath, err.SourcePath);
        Assert.Equal("LOT002", err.LotId);
    }

    [Fact]
    public void ScanUnionSchema_RejectsCorruptZipArchiveWithSourceDiagnostic()
    {
        var profile = CreateProfile();
        var dir = Path.Combine(_tempDir, "corrupt_zip");
        Directory.CreateDirectory(dir);
        var badZip = Path.Combine(dir, RepresentativeName("LOT001", "F1", 1, ".Reg.zip"));
        File.WriteAllBytes(badZip, [0x01, 0x02, 0x03, 0x04]);

        var plan = BuildValidPlan(profile, "corrupt_zip");
        var scanResult = _service.ScanUnionSchema(plan, Token);

        Assert.False(scanResult.IsSuccess);
        var err = Assert.Single(scanResult.Errors);
        Assert.Equal(FtImportDiagnosticCode.SourceOpenFailed, err.Code);
        Assert.Equal(badZip, err.SourcePath);
    }

    [Fact]
    public void ScanUnionSchema_SupportsCancellationAndDisposesAllOpenedHandles()
    {
        var profile = CreateProfile();
        var f1 = WriteZipFile("cancel", RepresentativeName("LOT001", "F1", 1, ".Reg.zip"), "a.csv",
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,1,0,1.0");
        var f2 = WritePlainFile("cancel", RepresentativeName("LOT002", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,ALS",
            "1,1,0,2.0");

        var plan = BuildValidPlan(profile, "cancel");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _service.ScanUnionSchema(plan, cts.Token));

        // Run a real scan and verify handles are disposed (exclusive open succeeds immediately).
        var ok = _service.ScanUnionSchema(plan, Token);
        Assert.True(ok.IsSuccess);
        using (new FileStream(f1, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (new FileStream(f2, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    // -------------------------------------------------------------------------
    // B. Union Schema & Exact Header Identity / Conflicts
    // -------------------------------------------------------------------------

    [Fact]
    public void ScanUnionSchema_ConstructsDeterministicUnionAcrossLotsOriginalRetestPartsAndExcludesUnselected()
    {
        var profile = CreateProfile();
        WritePlainFile("union", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS,PS",
            "1,1,0,1,2,3");
        WritePlainFile("union", RepresentativeName("LOTB", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS,IR",
            "1,1,0,1,2,4");
        WritePlainFile("union", RepresentativeName("LOTC", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,PS,IR",
            "1,1,0,1,3,4");
        WritePlainFile("union", RepresentativeName("LOTC", "F1", 2, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,PART2_ONLY",
            "1,1,0,5");
        WritePlainFile("union", RepresentativeName("LOTC", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,RETEST_ONLY",
            "1,1,0,6");
        WritePlainFile("union", RepresentativeName("LOTD", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,UNSELECTED_ONLY",
            "1,1,0,7");

        var plan = BuildValidPlan(profile, "union", sel => sel.SetLotSelected("LOTD", false));
        var scanResult = _service.ScanUnionSchema(plan, Token);

        Assert.True(scanResult.IsSuccess);
        Assert.Equal(["IDD", "ALS", "PS", "IR", "PART2_ONLY", "RETEST_ONLY"], scanResult.UnionSchema!.UnionMeasurementHeaders);
        Assert.DoesNotContain("UNSELECTED_ONLY", scanResult.UnionSchema.UnionMeasurementHeaders);
    }

    [Fact]
    public void ScanUnionSchema_PreservesExactOrdinalIdentityCaseLeadingZerosAndFullHeaderText()
    {
        var profile = CreateProfile();
        WritePlainFile("ordinal", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,1001_IDD,1002_IDD,IDD,idd,001_VTH,1_VTH,VTH (mV)",
            "1,1,0,1,2,3,4,5,6,7");

        var plan = BuildValidPlan(profile, "ordinal");
        var scanResult = _service.ScanUnionSchema(plan, Token);

        Assert.True(scanResult.IsSuccess);
        Assert.Equal(["1001_IDD", "1002_IDD", "IDD", "idd", "001_VTH", "1_VTH", "VTH (mV)"],
            scanResult.UnionSchema!.UnionMeasurementHeaders);
    }

    [Fact]
    public void ScanUnionSchema_RejectsDuplicateMeasurementHeaderWithinSingleSource()
    {
        var profile = CreateProfile();
        var dupPath = WritePlainFile("dup", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS,IDD",
            "1,1,0,1,2,3");

        var plan = BuildValidPlan(profile, "dup");
        var scanResult = _service.ScanUnionSchema(plan, Token);

        Assert.False(scanResult.IsSuccess);
        var err = Assert.Single(scanResult.Errors);
        Assert.Equal(FtImportDiagnosticCode.DuplicateMeasurementHeader, err.Code);
        Assert.Equal(dupPath, err.SourcePath);
    }

    [Theory]
    [InlineData("Lot_ID")]
    [InlineData("Bin")]
    [InlineData("Site")]
    [InlineData("Test_Role")]
    public void PrepareImportPlan_RejectsReservedOutputColumnNameConflictWhenSelected(string reservedName)
    {
        var profile = CreateProfile();
        WritePlainFile($"res_{reservedName}", RepresentativeName("LOT001", "F1", 1, ".csv"),
            $"UNIT_ID,SOFT_BIN,SITE_NUM,IDD,{reservedName}",
            "1,1,0,1.0,2.0");

        var plan = BuildValidPlan(profile, $"res_{reservedName}");
        var scanResult = _service.ScanUnionSchema(plan, Token);
        Assert.True(scanResult.IsSuccess);

        // Selecting the reserved name must fail with ReservedColumnNameConflict.
        var badPrep = _service.PrepareImportPlan(scanResult.UnionSchema!, ["IDD", reservedName], Token);
        Assert.False(badPrep.IsValid);
        Assert.Contains(badPrep.Errors, e => e.Code == FtImportDiagnosticCode.ReservedColumnNameConflict);

        // Selecting only non-conflicting headers succeeds.
        var okPrep = _service.PrepareImportPlan(scanResult.UnionSchema!, ["IDD"], Token);
        Assert.True(okPrep.IsValid);
    }

    [Fact]
    public void PrepareImportPlan_DoesNotApplyCaseNormalizationToReservedNamesAndRejectsWhitespaceIncompatibleHeaders()
    {
        var profile = CreateProfile();
        WritePlainFile("naming", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,lot_id, IDD_PAD ,",
            "1,1,0,1.0,2.0,3.0");

        var plan = BuildValidPlan(profile, "naming");
        var scanResult = _service.ScanUnionSchema(plan, Token);
        Assert.True(scanResult.IsSuccess);

        // Ordinal case distinction: "lot_id" is NOT "Lot_ID".
        var lowerCaseOk = _service.PrepareImportPlan(scanResult.UnionSchema!, ["lot_id"], Token);
        Assert.True(lowerCaseOk.IsValid);

        // Leading/trailing whitespace is incompatible with Worksheet column naming (trimming would silently rename).
        var paddedFail = _service.PrepareImportPlan(scanResult.UnionSchema!, [" IDD_PAD "], Token);
        Assert.False(paddedFail.IsValid);
        Assert.Contains(paddedFail.Errors, e => e.Code == FtImportDiagnosticCode.IncompatibleWorksheetColumnName);

        // Blank header is incompatible with Worksheet column naming.
        var blankFail = _service.PrepareImportPlan(scanResult.UnionSchema!, [""], Token);
        Assert.False(blankFail.IsValid);
        Assert.Contains(blankFail.Errors, e => e.Code == FtImportDiagnosticCode.IncompatibleWorksheetColumnName);
    }

    // -------------------------------------------------------------------------
    // C. Measurement Selection and Per-File Mapping
    // -------------------------------------------------------------------------

    [Fact]
    public void TestItemSelectionModel_SupportsAddRemoveSelectAllClearAllAndRejectsUnknownOrDuplicate()
    {
        var profile = CreateProfile();
        WritePlainFile("sel", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS,PS,IR",
            "1,1,0,1,2,3,4");

        var plan = BuildValidPlan(profile, "sel");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var model = new FtTestItemSelectionModel(union);

        Assert.Equal(4, model.AvailableCount);
        Assert.Equal(0, model.SelectedCount);
        Assert.Equal(["IDD", "ALS", "PS", "IR"], model.GetUnselectedHeaders());

        // Adding out of union order still yields deterministic union first-seen order.
        model.Add("IR");
        model.AddRange(["PS", "IDD"]);
        Assert.Equal(["IDD", "PS", "IR"], model.GetSelectedHeaders());
        Assert.Equal(["ALS"], model.GetUnselectedHeaders());

        // Reject unknown and duplicate (including case mismatch).
        Assert.Throws<ArgumentException>(() => model.Add("idd"));
        Assert.Throws<ArgumentException>(() => model.Add("UNKNOWN"));
        Assert.Throws<ArgumentException>(() => model.Add("IDD"));
        Assert.Throws<ArgumentException>(() => model.AddRange(["ALS", "ALS"]));
        // Failed AddRange must not partially mutate state.
        Assert.False(model.IsSelected("ALS"));

        model.Remove("PS");
        Assert.Equal(["IDD", "IR"], model.GetSelectedHeaders());

        model.SelectAll();
        Assert.Equal(4, model.SelectedCount);
        Assert.Empty(model.GetUnselectedHeaders());

        model.ClearAll();
        Assert.Equal(0, model.SelectedCount);

        // Empty selection is rejected when preparing final plan.
        var emptyPrep = _service.PrepareImportPlan(model, Token);
        Assert.False(emptyPrep.IsValid);
        Assert.Contains(emptyPrep.Errors, e => e.Code == FtImportDiagnosticCode.EmptyMeasurementSelection);
    }

    [Fact]
    public void PrepareImportPlan_RejectsUnknownDuplicateAndRoleConflictingSelections()
    {
        var profile = CreateProfile(
            ignoredColumns: [new ColumnSelector { Kind = ColumnSelectorKind.Index, ColumnIndex = 0 }]);
        WritePlainFile("role_conflict", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "EXTRA,SOFT_BIN,SITE_NUM,IDD",
            "0,1,0,1.0");
        WritePlainFile("role_conflict", RepresentativeName("LOT002", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,EXTRA",
            "0,1,0,1.0,2.0");

        var plan = BuildValidPlan(profile, "role_conflict");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;

        var unknownRes = _service.PrepareImportPlan(union, ["IDD", "NO_SUCH_TEST"], Token);
        Assert.False(unknownRes.IsValid);
        Assert.Contains(unknownRes.Errors, e => e.Code == FtImportDiagnosticCode.UnknownSelectedMeasurement);

        var dupRes = _service.PrepareImportPlan(union, ["IDD", "IDD"], Token);
        Assert.False(dupRes.IsValid);
        Assert.Contains(dupRes.Errors, e => e.Code == FtImportDiagnosticCode.DuplicateSelectedMeasurement);

        // EXTRA is an IgnoredColumn in LOT001 but an available test in LOT002 -> rejected cleanly before FT-03.3.
        var roleRes = _service.PrepareImportPlan(union, ["IDD", "EXTRA"], Token);
        Assert.False(roleRes.IsValid);
        Assert.Contains(roleRes.Errors, e => e.Code == FtImportDiagnosticCode.MeasurementColumnRoleConflict);
    }

    [Fact]
    public void PrepareImportPlan_MapsPresentAndAbsentColumnsDeterministicallyAcrossReorderedFiles()
    {
        var profile = CreateProfile();
        WritePlainFile("map", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS",
            "1,1,0,1.0,2.0");
        WritePlainFile("map", RepresentativeName("LOTB", "F1", 1, ".csv"),
            "PS,SITE_NUM,SOFT_BIN,UNIT_ID,IDD",
            "3.0,0,1,1,1.5");

        var plan = BuildValidPlan(profile, "map");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;

        // Pass selection in reverse order; output schema must still follow Union first-seen order (IDD, ALS, PS).
        var prep = _service.PrepareImportPlan(union, ["PS", "ALS", "IDD"], Token);
        Assert.True(prep.IsValid);
        var prepared = prep.Plan!;

        Assert.Equal(["IDD", "ALS", "PS"], prepared.SelectedMeasurementHeaders);
        Assert.Equal(["Lot_ID", "Bin", "Site", "Test_Role", "IDD", "ALS", "PS"],
            prepared.OutputColumns.Select(c => c.Name).ToArray());
        Assert.Equal(
            [WorksheetDataType.String, WorksheetDataType.String, WorksheetDataType.String, WorksheetDataType.String,
             WorksheetDataType.Numeric, WorksheetDataType.Numeric, WorksheetDataType.Numeric],
            prepared.OutputColumns.Select(c => c.DataType).ToArray());

        // LOT_A: IDD(src=3, Present), ALS(src=4, Present), PS(src=null, Absent)
        var mapA = prepared.Sources[0].MeasurementMapping;
        Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, mapA.Columns[0].Presence);
        Assert.Equal(3, mapA.Columns[0].SourceColumnIndex);
        Assert.Equal(4, mapA.Columns[0].OutputWorksheetColumnIndex);
        Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, mapA.Columns[1].Presence);
        Assert.Equal(4, mapA.Columns[1].SourceColumnIndex);
        Assert.Equal(FtSourceMeasurementColumnPresence.AbsentFromSource, mapA.Columns[2].Presence);
        Assert.Null(mapA.Columns[2].SourceColumnIndex);
        Assert.Equal([0, 1], mapA.PresentMeasurementIndices);
        Assert.Equal([2], mapA.AbsentMeasurementIndices);
        Assert.Equal(["PS"], mapA.AbsentHeaders);

        // LOT_B: IDD(src=4, Present), ALS(src=null, Absent), PS(src=0, Present)
        var mapB = prepared.Sources[1].MeasurementMapping;
        Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, mapB.Columns[0].Presence);
        Assert.Equal(4, mapB.Columns[0].SourceColumnIndex);
        Assert.Equal(FtSourceMeasurementColumnPresence.AbsentFromSource, mapB.Columns[1].Presence);
        Assert.Null(mapB.Columns[1].SourceColumnIndex);
        Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, mapB.Columns[2].Presence);
        Assert.Equal(0, mapB.Columns[2].SourceColumnIndex);
        Assert.Equal([0, 2], mapB.PresentMeasurementIndices);
        Assert.Equal([1], mapB.AbsentMeasurementIndices);
        Assert.Equal(["ALS"], mapB.AbsentHeaders);
    }

    // -------------------------------------------------------------------------
    // D. FT/RT Bin Retention Policy
    // -------------------------------------------------------------------------

    [Fact]
    public void BinRetentionPolicy_OriginalOnly_RetainsAllRows()
    {
        var profile = CreateProfile();
        WritePlainFile("pol_orig", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,99,0,1.0");

        var plan = BuildValidPlan(profile, "pol_orig");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        var policy = prep.Sources[0].BinRetentionPolicy;
        Assert.Equal(FtBinRetentionMode.RetainAllRows, policy.Mode);
        Assert.Null(policy.IncludeBinPredicate);
        Assert.True(policy.ShouldRetainBin("99"));
        Assert.True(policy.ShouldRetainBin("001"));
        Assert.False(policy.ShouldRetainBin(""));
        Assert.False(policy.ShouldRetainBin("   "));
    }

    [Fact]
    public void BinRetentionPolicy_NumberedRounds_NumericOrderingRT2VsRT10_AndMultiplePartsInLatestRound()
    {
        var profile = CreateProfile(goodBins: ["1", "01"], nonRetestFailBins: ["2", "09"]);
        WritePlainFile("pol_num", RepresentativeName("LOT001", "F1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_num", RepresentativeName("LOT001", "F1B45678R2", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_num", RepresentativeName("LOT001", "F1B45678R10", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_num", RepresentativeName("LOT001", "F1B45678R10", 2, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        var plan = BuildValidPlan(profile, "pol_num");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        // Order: Original, R2, R10 part 1, R10 part 2
        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, prep.Sources[0].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, prep.Sources[1].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainAllRows, prep.Sources[2].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainAllRows, prep.Sources[3].BinRetentionPolicy.Mode);

        // Exact string & leading-zero distinction on filtered policy.
        var filtered = prep.Sources[0].BinRetentionPolicy;
        Assert.True(filtered.ShouldRetainBin("1"));
        Assert.True(filtered.ShouldRetainBin("01"));
        Assert.False(filtered.ShouldRetainBin("001"));
        Assert.True(filtered.ShouldRetainBin("2"));
        Assert.True(filtered.ShouldRetainBin("09"));
        Assert.False(filtered.ShouldRetainBin("9"));
        Assert.False(filtered.ShouldRetainBin("5"));
        Assert.False(filtered.ShouldRetainBin(""));
        Assert.False(filtered.ShouldRetainBin("BIN_UNUSUAL"));
    }

    [Fact]
    public void BinRetentionPolicy_IndependentPerLot_AndRespectsExcludedHigherRound()
    {
        var profile = CreateProfile();
        // LOTA has Original, R1, and R2 (we will exclude R2 -> R1 becomes latest selected round for LOTA!)
        WritePlainFile("pol_lots", RepresentativeName("LOTA", "F1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_lots", RepresentativeName("LOTA", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_lots", RepresentativeName("LOTA", "F1B45678R2", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        // LOTB has Original, R1, R3 (all selected -> R1 filtered, R3 retains all)
        WritePlainFile("pol_lots", RepresentativeName("LOTB", "F1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_lots", RepresentativeName("LOTB", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_lots", RepresentativeName("LOTB", "F1B45678R3", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        // LOTC has Retest-only (R1 selected, no Original -> R1 retains all)
        WritePlainFile("pol_lots", RepresentativeName("LOTC", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        var plan = BuildValidPlan(profile, "pol_lots", sel =>
        {
            var lotAR2 = sel.Discovery.Files.Single(f => f.Match?.LotId == "LOTA" && f.Match.RetestRound == 2);
            sel.SetFileSelected(lotAR2.Id, false);
        });

        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        FtBinRetentionMode ModeOf(string lot, FtTestRole role, int? round) =>
            prep.Sources.Single(s => s.SourceFile.LotId == lot && s.SourceFile.TestRole == role && s.SourceFile.RetestRound == round)
                .BinRetentionPolicy.Mode;

        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, ModeOf("LOTA", FtTestRole.Original, null));
        Assert.Equal(FtBinRetentionMode.RetainAllRows, ModeOf("LOTA", FtTestRole.Retest, 1));

        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, ModeOf("LOTB", FtTestRole.Original, null));
        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, ModeOf("LOTB", FtTestRole.Retest, 1));
        Assert.Equal(FtBinRetentionMode.RetainAllRows, ModeOf("LOTB", FtTestRole.Retest, 3));

        Assert.Equal(FtBinRetentionMode.RetainAllRows, ModeOf("LOTC", FtTestRole.Retest, 1));
    }

    [Fact]
    public void BinRetentionPolicy_UnnumberedRetestGroup_FiltersOriginalAndRetainsAllUnnumberedRetests()
    {
        var profile = CreateProfile(unnumberedRetest: true);
        WritePlainFile("pol_unnum", RepresentativeName("LOT001", "F1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_unnum", RepresentativeName("LOT001", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_unnum", RepresentativeName("LOT001", "F1B45678R1", 2, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        var plan = BuildValidPlan(profile, "pol_unnum");
        Assert.Equal(FtLatestRetestKind.UnnumberedGroup, plan.Lots[0].LatestRetestKind);

        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, prep.Sources[0].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainAllRows, prep.Sources[1].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainAllRows, prep.Sources[2].BinRetentionPolicy.Mode);
    }

    [Fact]
    public void ScanUnionSchema_DefensivelyRejectsManuallyConstructedMixedNumberedAndUnnumberedPlan()
    {
        var profile = CreateProfile();
        var f1 = WritePlainFile("mixed", RepresentativeName("LOT001", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        var f2 = WritePlainFile("mixed", RepresentativeName("LOT001", "F1B45678R1", 2, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        var numbered = new FtPlannedSourceFile(Guid.NewGuid(), f1, Path.GetFileName(f1), "LOT001", "F1", FtTestRole.Retest, 1, 1);
        var unnumbered = new FtPlannedSourceFile(Guid.NewGuid(), f2, Path.GetFileName(f2), "LOT001", "F1", FtTestRole.Retest, null, 2);
        var badLot = new FtPlannedLot(
            "LOT001", "F1", false, true, FtLatestRetestKind.NumberedRound, [1], 1,
            [], [new FtPlannedRetestRound(1, [numbered], [1]), new FtPlannedRetestRound(null, [unnumbered], [2])],
            [numbered, unnumbered]);
        var badPlan = new FtImportPlan(profile, "F1", [badLot], [numbered, unnumbered], []);

        var scanResult = _service.ScanUnionSchema(badPlan, Token);
        Assert.False(scanResult.IsSuccess);
        Assert.Contains(scanResult.Errors, e => e.Code == FtImportDiagnosticCode.AmbiguousMixedRetestRounds);
    }

    // -------------------------------------------------------------------------
    // E. Parser Integration Contract
    // -------------------------------------------------------------------------

    [Fact]
    public void ParserIntegration_EarlierRoundBinFirstFilteringSkipsInvalidFilteredRowsAndPreservesStrictValidation()
    {
        var profile = CreateProfile(goodBins: ["1"], nonRetestFailBins: ["2"], missingPolicy: MissingValuePolicy.UseMissing);

        // Original (earlier round): Bin=5 (excluded) has INVALID numeric "NOT_A_NUM" -> MUST NOT FAIL!
        // Bin=1 (retained) has Missing cell "" in IDD (present) and absent ALS column!
        WritePlainFile("parse_int", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,5,0,NOT_A_NUM",
            "2,1,0,",
            "3,2,1,12.5");

        // Latest round (RT1): retains ALL valid bins including Bin=5 and unusual Bin=BIN_X; has ALS & IDD.
        WritePlainFile("parse_int", RepresentativeName("LOT001", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS",
            "4,5,0,9.5,100.0",
            "5,BIN_X,1,8.5,200.0");

        var plan = BuildValidPlan(profile, "parse_int");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD", "ALS"], Token).Plan!;

        // Parse Original source with its retention policy predicate.
        var origSource = prep.Sources[0];
        var origRows = new List<(string Bin, double?[] Values)>();
        var origResult = _parser.ParseFile(
            origSource.SourceFile.SourcePath,
            prep.ProfileSnapshot,
            origSource.Structure,
            prep.SelectedMeasurementHeaders,
            row => origRows.Add((row.Bin, row.Measurements.ToArray())),
            includeBin: origSource.BinRetentionPolicy.IncludeBinPredicate,
            cancellationToken: Token);

        Assert.True(origResult.IsSuccess);
        Assert.Equal(3, origResult.ProcessedRowCount);
        Assert.Equal(2, origResult.RetainedRowCount);
        Assert.Equal("1", origRows[0].Bin);
        // Both IDD (Present + Missing cell) and ALS (Absent column) yield null from parser,
        // while MeasurementMapping explicitly distinguishes PresentInSource vs AbsentFromSource!
        Assert.Null(origRows[0].Values[0]);
        Assert.Null(origRows[0].Values[1]);
        Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, origSource.MeasurementMapping.Columns[0].Presence);
        Assert.Equal(FtSourceMeasurementColumnPresence.AbsentFromSource, origSource.MeasurementMapping.Columns[1].Presence);
        Assert.Equal("2", origRows[1].Bin);
        Assert.Equal(12.5, origRows[1].Values[0]);

        // Parse RT1 (latest round) with its retention policy predicate (null -> retain all valid bins).
        var rt1Source = prep.Sources[1];
        var rt1Rows = new List<(string Bin, double?[] Values)>();
        var rt1Result = _parser.ParseFile(
            rt1Source.SourceFile.SourcePath,
            prep.ProfileSnapshot,
            rt1Source.Structure,
            prep.SelectedMeasurementHeaders,
            row => rt1Rows.Add((row.Bin, row.Measurements.ToArray())),
            includeBin: rt1Source.BinRetentionPolicy.IncludeBinPredicate,
            cancellationToken: Token);

        Assert.True(rt1Result.IsSuccess);
        Assert.Equal(2, rt1Result.RetainedRowCount);
        Assert.Equal(["5", "BIN_X"], rt1Rows.Select(r => r.Bin).ToArray());
    }

    [Fact]
    public void ParserIntegration_InvalidNumericInRetainedRowStillFailsAndIsNotClassifiedAsAbsent()
    {
        var profile = CreateProfile(goodBins: ["1"], nonRetestFailBins: ["2"]);
        WritePlainFile("parse_err", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,1,0,BAD_VALUE");
        WritePlainFile("parse_err", RepresentativeName("LOT001", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "2,1,0,1.0");

        var plan = BuildValidPlan(profile, "parse_err");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        var origSource = prep.Sources[0];
        Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, origSource.MeasurementMapping.Columns[0].Presence);

        var parseRes = _parser.ParseFile(
            origSource.SourceFile.SourcePath,
            prep.ProfileSnapshot,
            origSource.Structure,
            prep.SelectedMeasurementHeaders,
            _ => { },
            includeBin: origSource.BinRetentionPolicy.IncludeBinPredicate,
            cancellationToken: Token);

        Assert.False(parseRes.IsSuccess);
        Assert.Equal(DatalogParseErrorCode.InvalidNumericValue, parseRes.Diagnostics[0].Code);
        Assert.Empty(origSource.MeasurementMapping.AbsentHeaders);
    }

    [Fact]
    public void ParserIntegration_EmptyBinStillFailsOnLatestRoundRetainAllPolicy()
    {
        var profile = CreateProfile(goodBins: ["1"], nonRetestFailBins: ["2"]);
        WritePlainFile("empty_bin", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,,0,1.0");

        var plan = BuildValidPlan(profile, "empty_bin");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        var source = prep.Sources[0];
        Assert.Equal(FtBinRetentionMode.RetainAllRows, source.BinRetentionPolicy.Mode);
        var parseRes = _parser.ParseFile(
            source.SourceFile.SourcePath,
            prep.ProfileSnapshot,
            source.Structure,
            prep.SelectedMeasurementHeaders,
            _ => { },
            includeBin: source.BinRetentionPolicy.IncludeBinPredicate,
            cancellationToken: Token);

        Assert.False(parseRes.IsSuccess);
        Assert.Equal(DatalogParseErrorCode.MissingRequiredBin, parseRes.Diagnostics[0].Code);
    }

    [Fact]
    public void ScanUnionSchema_SameHeadersAcrossAllLotsDeduplicatesDeterministically()
    {
        var profile = CreateProfile();
        WritePlainFile("same_hdr", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS,PS",
            "1,1,0,1,2,3");
        WritePlainFile("same_hdr", RepresentativeName("LOTB", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS,PS",
            "1,1,0,1,2,3");

        var plan = BuildValidPlan(profile, "same_hdr");
        var scanResult = _service.ScanUnionSchema(plan, Token);
        Assert.True(scanResult.IsSuccess);
        Assert.Equal(["IDD", "ALS", "PS"], scanResult.UnionSchema!.UnionMeasurementHeaders);
    }

    // -------------------------------------------------------------------------
    // F. Prepared Plan Safety & Immutability
    // -------------------------------------------------------------------------

    [Fact]
    public void PreparedPlan_IsDeeplyImmutableAndIndependentOfLaterSelectionMutations()
    {
        var profile = CreateProfile();
        WritePlainFile("immut", RepresentativeName("LOT001", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS",
            "1,1,0,1.0,2.0");
        WritePlainFile("immut", RepresentativeName("LOT001", "F1B45678R1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,PS",
            "2,1,0,1.5,3.0");

        var discovery = _discovery.DiscoverFolder(profile, Path.Combine(_tempDir, "immut"), Token);
        var fileSelection = new FtImportSelectionModel(discovery);
        var plan = fileSelection.BuildImportPlan().Plan!;
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var itemSelection = new FtTestItemSelectionModel(union);
        itemSelection.AddRange(["IDD", "ALS"]);

        var prepared = _service.PrepareImportPlan(itemSelection, Token).Plan!;

        // Mutate both selection models after preparation.
        itemSelection.ClearAll();
        itemSelection.Add("PS");
        fileSelection.ClearSelection();

        // Prepared plan must remain completely unaffected.
        Assert.Equal(["IDD", "ALS"], prepared.SelectedMeasurementHeaders);
        Assert.Equal(2, prepared.Sources.Count);

        // Nested collections must reject mutation via IList<T>.
        Assert.Throws<NotSupportedException>(() => ((IList<string>)prepared.UnionMeasurementHeaders).Add("X"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)prepared.SelectedMeasurementHeaders).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<FtPreparedSourceFile>)prepared.Sources).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ResolvedHeaderColumn>)prepared.Sources[0].Structure.AvailableTestHeaders).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)prepared.Sources[0].Structure.AllHeaders).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<FtSourceMeasurementColumnMapping>)prepared.Sources[0].MeasurementMapping.Columns).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)prepared.Sources[0].MeasurementMapping.AbsentHeaders).Add("Z"));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)prepared.Sources[0].BinRetentionPolicy.RetainedBins).Add("99"));
        Assert.Throws<NotSupportedException>(() => ((IList<YAT.Application.Abstractions.Persistence.WorksheetColumnDefinition>)prepared.OutputColumns).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<FtPlannedLot>)prepared.ImportPlan.Lots).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<FtPlannedSourceFile>)prepared.ImportPlan.OrderedFiles).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<int>)prepared.ImportPlan.Lots[0].SelectedRetestRounds).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ResolvedMeasurementColumn>)prepared.Sources[0].MeasurementMapping.ParserMapping.Columns).Clear());
    }

    // -------------------------------------------------------------------------
    // G. P0/P1 Closure Verification: Mapping Values, Semantics, Composites, Stamps
    // -------------------------------------------------------------------------

    [Fact]
    public void ParserMapping_VerifiesActualParsedValuesWithInterspersedAbsentColumnsMultipleCompositesAndLeadingZeroCase()
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

        // LOTA: has PRE_COMP (1001_IDD, 001_VTH), EXTRA_A, 1_VTH, POST_COMP (IDD, idd). Absent: 1002_IDD, ALS.
        WritePlainFile("p0_map", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,PRE_COMP,EXTRA_A,1_VTH,POST_COMP",
            "1,1,0,11.1;0.25,7.7,0.55,22.2;33.3");

        // LOTB: reordered physical columns with PRE_COMP and POST_COMP before/after regular columns. Absent: EXTRA_A, 1_VTH.
        WritePlainFile("p0_map", RepresentativeName("LOTB", "F1", 1, ".csv"),
            "ALS,POST_COMP,SITE_NUM,1002_IDD,SOFT_BIN,UNIT_ID,PRE_COMP",
            "99.9,44.4;55.5,1,12.3,1,2,10.5;0.15");

        var plan = BuildValidPlan(profile, "p0_map");
        var scanRes = _service.ScanUnionSchema(plan, Token);
        Assert.True(scanRes.IsSuccess, string.Join(" | ", scanRes.Errors.Select(e => e.Message)));
        var union = scanRes.UnionSchema!;
        // Union order: 1001_IDD, 001_VTH, EXTRA_A, 1_VTH, IDD, idd, ALS, 1002_IDD
        // Select subset (exclude 1_VTH) with interspersed present/absent columns in both files.
        var prep = _service.PrepareImportPlan(union, ["1001_IDD", "ALS", "EXTRA_A", "001_VTH", "1002_IDD", "IDD", "idd"], Token).Plan!;

        Assert.Equal(["1001_IDD", "001_VTH", "EXTRA_A", "IDD", "idd", "ALS", "1002_IDD"], prep.SelectedMeasurementHeaders);

        // Verify LOTA actual parsed values indexed by ParserMeasurementIndex against OutputWorksheetColumnIndex.
        var srcA = prep.Sources[0];
        double?[]? rowA = null;
        var resA = _parser.ParseFile(
            srcA.SourceFile.SourcePath,
            prep.ProfileSnapshot,
            srcA.Structure,
            prep.SelectedMeasurementHeaders,
            r => rowA = r.Measurements.ToArray(),
            includeBin: srcA.BinRetentionPolicy.IncludeBinPredicate,
            cancellationToken: Token);
        Assert.True(resA.IsSuccess);
        Assert.NotNull(rowA);

        var outA = new double?[prep.OutputColumns.Count];
        foreach (var col in srcA.MeasurementMapping.Columns)
        {
            Assert.Equal(col.SelectedMeasurementIndex, col.ParserMeasurementIndex);
            outA[col.OutputWorksheetColumnIndex] = col.Presence == FtSourceMeasurementColumnPresence.AbsentFromSource
                ? 0.0
                : rowA![col.ParserMeasurementIndex];
        }

        Assert.Equal([11.1, 0.25, 7.7, 22.2, 33.3, 0.0, 0.0], outA[4..]);

        // Verify LOTB actual parsed values (reordered physical columns + absent EXTRA_A).
        var srcB = prep.Sources[1];
        double?[]? rowB = null;
        var resB = _parser.ParseFile(
            srcB.SourceFile.SourcePath,
            prep.ProfileSnapshot,
            srcB.Structure,
            prep.SelectedMeasurementHeaders,
            r => rowB = r.Measurements.ToArray(),
            includeBin: srcB.BinRetentionPolicy.IncludeBinPredicate,
            cancellationToken: Token);
        Assert.True(resB.IsSuccess);
        Assert.NotNull(rowB);

        var outB = new double?[prep.OutputColumns.Count];
        foreach (var col in srcB.MeasurementMapping.Columns)
        {
            Assert.Equal(col.SelectedMeasurementIndex, col.ParserMeasurementIndex);
            outB[col.OutputWorksheetColumnIndex] = col.Presence == FtSourceMeasurementColumnPresence.AbsentFromSource
                ? 0.0
                : rowB![col.ParserMeasurementIndex];
        }

        Assert.Equal([10.5, 0.15, 0.0, 44.4, 55.5, 99.9, 12.3], outB[4..]);
    }

    [Theory]
    [InlineData(MissingValuePolicy.UseMissing, null)]
    [InlineData(MissingValuePolicy.ReplaceWithZero, 0.0)]
    public void PresentAbsentMissingAndErrorSemantics_HandlesZeroNonZeroMissingAndAbsentIndependently(
        MissingValuePolicy policy,
        double? expectedMissingCellValue)
    {
        var profile = CreateProfile(missingPolicy: policy);
        WritePlainFile($"sem_{policy}", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,ZERO_COL,VAL_COL,MISS_COL",
            "1,1,0,0.0,42.5,N/A");
        WritePlainFile($"sem_{policy}", RepresentativeName("LOTB", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,ABSENT_IN_A",
            "2,1,0,7.5");

        var plan = BuildValidPlan(profile, $"sem_{policy}");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["ZERO_COL", "VAL_COL", "MISS_COL", "ABSENT_IN_A"], Token).Plan!;

        var srcA = prep.Sources[0];
        double?[]? valuesA = null;
        var parseA = _parser.ParseFile(
            srcA.SourceFile.SourcePath,
            prep.ProfileSnapshot,
            srcA.Structure,
            prep.SelectedMeasurementHeaders,
            r => valuesA = r.Measurements.ToArray(),
            cancellationToken: Token);

        Assert.True(parseA.IsSuccess);
        Assert.Equal(0.0, valuesA![0]);
        Assert.Equal(42.5, valuesA[1]);
        Assert.Equal(expectedMissingCellValue, valuesA[2]);
        // Even when MissingValuePolicy.UseMissing yields null for MISS_COL, schema mapping distinguishes Present vs Absent.
        Assert.Equal(FtSourceMeasurementColumnPresence.PresentInSource, srcA.MeasurementMapping.Columns[2].Presence);
        Assert.Equal(FtSourceMeasurementColumnPresence.AbsentFromSource, srcA.MeasurementMapping.Columns[3].Presence);
        Assert.Null(srcA.MeasurementMapping.Columns[3].SourceColumnIndex);
    }

    [Fact]
    public void PresentMissingCellWithStopImportAndNumericOverflow_FailStrictlyWithoutClassifyingAsAbsent()
    {
        var stopProfile = CreateProfile(missingPolicy: MissingValuePolicy.StopImport);
        WritePlainFile("sem_stop", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,1,0,N/A");

        var stopPlan = BuildValidPlan(stopProfile, "sem_stop");
        var stopPrep = _service.PrepareImportPlan(_service.ScanUnionSchema(stopPlan, Token).UnionSchema!, ["IDD"], Token).Plan!;
        var stopParse = _parser.ParseFile(
            stopPrep.Sources[0].SourceFile.SourcePath,
            stopPrep.ProfileSnapshot,
            stopPrep.Sources[0].Structure,
            stopPrep.SelectedMeasurementHeaders,
            _ => { },
            cancellationToken: Token);

        Assert.False(stopParse.IsSuccess);
        Assert.Equal(DatalogParseErrorCode.MissingValueRejected, stopParse.Diagnostics[0].Code);
        Assert.Empty(stopPrep.Sources[0].MeasurementMapping.AbsentHeaders);

        var overflowProfile = CreateProfile();
        WritePlainFile("sem_overflow", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,1,0,1e500");

        var overflowPlan = BuildValidPlan(overflowProfile, "sem_overflow");
        var overflowPrep = _service.PrepareImportPlan(_service.ScanUnionSchema(overflowPlan, Token).UnionSchema!, ["IDD"], Token).Plan!;
        var overflowParse = _parser.ParseFile(
            overflowPrep.Sources[0].SourceFile.SourcePath,
            overflowPrep.ProfileSnapshot,
            overflowPrep.Sources[0].Structure,
            overflowPrep.SelectedMeasurementHeaders,
            _ => { },
            cancellationToken: Token);

        Assert.False(overflowParse.IsSuccess);
        Assert.Equal(DatalogParseErrorCode.NumericOverflow, overflowParse.Diagnostics[0].Code);
        Assert.Empty(overflowPrep.Sources[0].MeasurementMapping.AbsentHeaders);
    }

    [Fact]
    public void BinRetentionPolicy_OriginalPlusRT1AndOriginalPlusRT1PlusRT2_ApplyCorrectLotPolicies()
    {
        var profile = CreateProfile();
        WritePlainFile("pol_r1", RepresentativeName("LOTA", "F1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_r1", RepresentativeName("LOTA", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        var prepR1 = _service.PrepareImportPlan(
            _service.ScanUnionSchema(BuildValidPlan(profile, "pol_r1"), Token).UnionSchema!, ["IDD"], Token).Plan!;
        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, prepR1.Sources[0].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainAllRows, prepR1.Sources[1].BinRetentionPolicy.Mode);

        WritePlainFile("pol_r1_r2", RepresentativeName("LOTB", "F1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_r1_r2", RepresentativeName("LOTB", "F1B45678R1", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");
        WritePlainFile("pol_r1_r2", RepresentativeName("LOTB", "F1B45678R2", 1, ".csv"), "UNIT_ID,SOFT_BIN,SITE_NUM,IDD", "1,1,0,1.0");

        var prepR2 = _service.PrepareImportPlan(
            _service.ScanUnionSchema(BuildValidPlan(profile, "pol_r1_r2"), Token).UnionSchema!, ["IDD"], Token).Plan!;
        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, prepR2.Sources[0].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainGoodAndNonRetestFailBinsOnly, prepR2.Sources[1].BinRetentionPolicy.Mode);
        Assert.Equal(FtBinRetentionMode.RetainAllRows, prepR2.Sources[2].BinRetentionPolicy.Mode);
    }

    [Fact]
    public void SourceFileStamp_MatchesActualFileMetadataAndDetectsMissingSource()
    {
        var profile = CreateProfile();
        var path = WritePlainFile("stamp", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD",
            "1,1,0,1.0");
        var info = new FileInfo(path);

        var plan = BuildValidPlan(profile, "stamp");
        var union = _service.ScanUnionSchema(plan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        Assert.NotNull(union.ScannedFiles[0].FileStamp);
        Assert.Equal(info.Length, union.ScannedFiles[0].FileStamp!.LengthBytes);
        Assert.Equal(info.LastWriteTimeUtc, union.ScannedFiles[0].FileStamp!.LastWriteTimeUtc);
        Assert.Equal(union.ScannedFiles[0].FileStamp, prep.Sources[0].FileStamp);

        // Delete file after planning and verify ScanUnionSchema reports SourceOpenFailed with SourcePath.
        File.Delete(path);
        var missingScan = _service.ScanUnionSchema(plan, Token);
        Assert.False(missingScan.IsSuccess);
        var err = Assert.Single(missingScan.Errors);
        Assert.Equal(FtImportDiagnosticCode.SourceOpenFailed, err.Code);
        Assert.Equal(path, err.SourcePath);
    }

    [Fact]
    public void ScanUnionSchema_IsIsolatedFromSubsequentMutationsToManuallyConstructedMutablePlanLists()
    {
        var profile = CreateProfile();
        var path = WritePlainFile("mut_plan", RepresentativeName("LOTA", "F1", 1, ".csv"),
            "UNIT_ID,SOFT_BIN,SITE_NUM,IDD,ALS",
            "1,1,0,1.0,2.0");

        var mutableFile = new FtPlannedSourceFile(Guid.NewGuid(), path, Path.GetFileName(path), "LOTA", "F1", FtTestRole.Original, null, 1);
        var mutableOriginals = new List<FtPlannedSourceFile> { mutableFile };
        var mutableRounds = new List<FtPlannedRetestRound>();
        var mutableSelectedRounds = new List<int>();
        var mutableLotOrdered = new List<FtPlannedSourceFile> { mutableFile };
        var mutableLots = new List<FtPlannedLot>
        {
            new("LOTA", "F1", true, false, FtLatestRetestKind.None, mutableSelectedRounds, null, mutableOriginals, mutableRounds, mutableLotOrdered)
        };
        var mutablePlanOrdered = new List<FtPlannedSourceFile> { mutableFile };
        var mutablePlanDiagnostics = new List<FtImportDiagnostic>();
        var manualPlan = new FtImportPlan(profile, "F1", mutableLots, mutablePlanOrdered, mutablePlanDiagnostics);

        var union = _service.ScanUnionSchema(manualPlan, Token).UnionSchema!;
        var prep = _service.PrepareImportPlan(union, ["IDD"], Token).Plan!;

        // Mutate all caller lists after preparation.
        mutableOriginals.Clear();
        mutableLotOrdered.Clear();
        mutableLots.Clear();
        mutablePlanOrdered.Clear();
        mutableSelectedRounds.Add(99);

        Assert.Single(union.ImportPlan.Lots);
        Assert.Single(union.ImportPlan.OrderedFiles);
        Assert.Single(prep.ImportPlan.Lots);
        Assert.Single(prep.ImportPlan.OrderedFiles);
        Assert.Empty(prep.ImportPlan.Lots[0].SelectedRetestRounds);
    }
}
