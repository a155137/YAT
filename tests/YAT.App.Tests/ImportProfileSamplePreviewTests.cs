using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;
using YAT.app.ViewModels;
using YAT.Infrastructure.ImportProfiles;

namespace YAT.App.Tests;

public sealed class ImportProfileSamplePreviewTests : IDisposable
{
    private readonly string _tempDir;

    public ImportProfileSamplePreviewTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft02-3-preview-tests", Guid.NewGuid().ToString("N"));
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

    private string WriteSampleCsv(string fileName, string content)
    {
        var path = Path.Combine(_tempDir, fileName);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    private string WriteSampleZip(string zipFileName, string innerName, string content)
    {
        var zipPath = Path.Combine(_tempDir, zipFileName);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        using var writer = new StreamWriter(archive.CreateEntry(innerName).Open(), new UTF8Encoding(false));
        writer.Write(content);
        return zipPath;
    }

    private static ImportProfileBuilderViewModel CreateNewBuilder(TimeSpan? debounce = null) =>
        new(
            new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null),
            previewUiDispatch: action => action(),
            previewDebounce: debounce ?? TimeSpan.Zero);

    [Fact]
    public async Task IncompleteProfileBootstrapsTableAndRawPreviewAndNeverPersistsSamplePath()
    {
        var samplePath = WriteSampleCsv("unmatched_sample.csv", """
            PreambleLine
            Test_Name,HARD_BIN,SITE_NUM,IDD,VTH
            Unit,bin,site,mA,V
            DATA_START,01,1,12.5,0.65
            DATA_START,02,2,14.1,0.72
            """);

        using var vm = CreateNewBuilder();
        vm.ProfileName = ""; // Incomplete / invalid profile
        vm.Preview.SetSampleFile(samplePath);
        await vm.Preview.CurrentRefreshTask;

        // Data preview is populated even though Profile is incomplete and filename does not match
        Assert.False(vm.Preview.IsFilenameMatched);
        Assert.True(vm.Preview.HasFilenameDiagnostic);
        Assert.Equal(5, vm.Preview.PreviewTableRows.Count);
        Assert.Contains("PreambleLine", vm.Preview.RawPreviewText, StringComparison.Ordinal);

        // Switching Table <-> Raw does not rescan or clear state
        var inspectionBefore = vm.Preview.LastInspection;
        vm.Preview.ViewMode = PreviewViewMode.Raw;
        Assert.True(vm.Preview.IsRawView);
        vm.Preview.ViewMode = PreviewViewMode.Table;
        Assert.Same(inspectionBefore, vm.Preview.LastInspection);

        // Saving a valid profile never persists sample path in JSON
        vm.ProfileName = "Saved_Without_SamplePath";
        Assert.True(vm.TrySave());
        var json = Encoding.UTF8.GetString(ImportProfileJsonSerializer.Serialize(vm.SavedProfile!));
        Assert.DoesNotContain("unmatched_sample.csv", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidDelimiterStillProvidesRawPreview()
    {
        var samplePath = WriteSampleCsv("raw_only.csv", "A,B,C\n1,2,3\n");
        using var vm = CreateNewBuilder();
        vm.DelimiterPreset = DelimiterPreset.Custom;
        vm.CustomDelimiter = "";
        vm.Preview.SetSampleFile(samplePath);
        await vm.Preview.CurrentRefreshTask;

        Assert.Contains("A,B,C", vm.Preview.RawPreviewText, StringComparison.Ordinal);
        Assert.False(vm.Preview.IsStructureResolved);
    }

    [Fact]
    public async Task InteractiveRowAndColumnAssignmentSynchronizesDraftBeforeAndAfterHeaderResolution()
    {
        var samplePath = WriteSampleCsv("interactive.csv", """
            Preamble
            Test_Name,HARD_BIN,SITE_NUM,IGNORE_COL,IDD,VTH
            Unit,bin,site,ms,mA,V
            DATA_START,01,1,99,10.5,0.65
            DATA_START,1,2,88,11.2,0.70
            """);

        using var vm = CreateNewBuilder();
        vm.ProfileName = "Interactive_FT";
        vm.HeaderRow.Kind = RowMatchKind.KeywordMatch;
        vm.HeaderRow.Keyword = "NON_EXISTENT";
        vm.Preview.SetSampleFile(samplePath);
        await vm.Preview.CurrentRefreshTask;

        // Before Header resolution: column assignment uses 1-based Index
        Assert.False(vm.Preview.IsStructureResolved);
        Assert.True(vm.Preview.WillReplaceKeywordRule(PreviewRowRole.Header));
        var col1Before = vm.Preview.PreviewColumns[1];
        Assert.Null(col1Before.ResolvedHeaderName);
        vm.Preview.AssignColumnRole(col1Before, PreviewColumnAction.SetAsBin);
        await vm.Preview.CurrentRefreshTask;
        Assert.Equal(ColumnSelectorKind.Index, vm.BinColumn.Kind);
        Assert.Equal(2, vm.BinColumn.ColumnPosition);

        // Assign Header=2, Unit=3, DataStart=4 via preview row actions
        vm.Preview.AssignRowRole(2, PreviewRowRole.Header);
        await vm.Preview.CurrentRefreshTask;
        vm.Preview.AssignRowRole(3, PreviewRowRole.Unit);
        await vm.Preview.CurrentRefreshTask;
        vm.Preview.AssignRowRole(4, PreviewRowRole.DataStart);
        await vm.Preview.CurrentRefreshTask;

        Assert.Equal(2, vm.HeaderRow.RowNumber);
        Assert.True(vm.UnitRow.IsEnabled);
        Assert.Equal(3, vm.UnitRow.RowNumber);
        Assert.Equal(4, vm.DataStartRow.RowNumber);
        Assert.True(vm.Preview.IsStructureResolved);

        // After Header resolution: column assignment uses exact HeaderName
        var siteCol = vm.Preview.PreviewColumns.First(c => c.ResolvedHeaderName == "SITE_NUM");
        var ignoreCol = vm.Preview.PreviewColumns.First(c => c.ResolvedHeaderName == "IGNORE_COL");
        vm.Preview.AssignColumnRole(siteCol, PreviewColumnAction.SetAsSite);
        await vm.Preview.CurrentRefreshTask;
        vm.Preview.AssignColumnRole(ignoreCol, PreviewColumnAction.AddToIgnored);
        await vm.Preview.CurrentRefreshTask;

        Assert.True(vm.SiteColumn.IsEnabled);
        Assert.Equal(ColumnSelectorKind.HeaderName, vm.SiteColumn.Kind);
        Assert.Equal("SITE_NUM", vm.SiteColumn.HeaderName);
        Assert.Single(vm.IgnoredColumns);
        Assert.Equal("IGNORE_COL", vm.IgnoredColumns[0].HeaderName);

        // Clear optional Unit row & remove ignored column
        Assert.True(vm.Preview.CanClearRowRole(3));
        vm.Preview.ClearRowRole(3);
        await vm.Preview.CurrentRefreshTask;
        Assert.False(vm.UnitRow.IsEnabled);

        vm.Preview.AssignColumnRole(ignoreCol, PreviewColumnAction.RemoveFromIgnored);
        await vm.Preview.CurrentRefreshTask;
        Assert.Empty(vm.IgnoredColumns);
    }

    [Fact]
    public async Task CompositeExpandedColumnsAreMarkedAndAssignedByResolvedHeaderName()
    {
        var samplePath = WriteSampleCsv("composite.csv", """
            TAG,IDD
            01/S1,12.5
            02/S2,14.0
            """);

        using var vm = CreateNewBuilder();
        vm.ProfileName = "Composite_FT";
        vm.AddCompositeFieldCommand.Execute(null);
        vm.CompositeFields[0].SourceColumn.HeaderName = "TAG";
        vm.CompositeFields[0].Separator = "/";
        vm.CompositeFields[0].NewOutputName = "EXP_BIN";
        vm.CompositeFields[0].AddOutputNameCommand.Execute(null);
        vm.CompositeFields[0].NewOutputName = "EXP_SITE";
        vm.CompositeFields[0].AddOutputNameCommand.Execute(null);
        vm.BinColumn.HeaderName = "EXP_BIN";

        vm.Preview.SetSampleFile(samplePath);
        await vm.Preview.CurrentRefreshTask;

        Assert.True(vm.Preview.IsStructureResolved);
        var expSite = vm.Preview.PreviewColumns.First(c => c.ResolvedHeaderName == "EXP_SITE");
        Assert.True(expSite.IsCompositeExpanded);
        Assert.Contains("[EXP]", expSite.RoleBadgeText, StringComparison.Ordinal);

        vm.Preview.AssignColumnRole(expSite, PreviewColumnAction.SetAsSite);
        await vm.Preview.CurrentRefreshTask;
        Assert.Equal(ColumnSelectorKind.HeaderName, vm.SiteColumn.Kind);
        Assert.Equal("EXP_SITE", vm.SiteColumn.HeaderName);
    }

    [Fact]
    public async Task BoundedSampleValidationReportsRepresentativeSuccessAndMeasurementFailureAccurately()
    {
        var zipPath = WriteSampleZip(
            "E320BD-262.20261123111219.XXX123.1-6846_TQB01RXAA1_F1_1_20261122171516_20261123110911.Reg.zip",
            "lot.csv",
            """
            Test_Name,HARD_BIN,SITE_NUM,IDD
            DATA_START,01,1,12.5
            DATA_START,01,2,N/A
            """);

        using var vm = CreateNewBuilder();
        vm.ProfileName = "Rep_FT";
        vm.AcceptedExtensions.Clear();
        vm.AcceptedExtensions.Add(".Reg.zip");
        vm.SegmentDelimitersText = "_";
        vm.LotIdField.Kind = FilenameFieldKind.MarkerAfter;
        vm.LotIdField.SegmentPosition = 1;
        vm.LotIdField.Marker = ".";
        vm.LotIdField.MarkerOccurrence = 2;
        vm.TestStageField.Kind = FilenameFieldKind.FixedRange;
        vm.TestStageField.SegmentPosition = 3;
        vm.TestStageField.StartPosition = 1;
        vm.TestStageField.Length = 2;
        vm.RetestRoleField.IsEnabled = true;
        vm.RetestRoleField.SegmentPosition = 3;
        vm.RetestMarker = "R";
        vm.FilePartField.IsEnabled = true;
        vm.FilePartField.SegmentPosition = 4;
        vm.BinColumn.HeaderName = "HARD_BIN";
        vm.SiteColumn.IsEnabled = true;
        vm.SiteColumn.HeaderName = "SITE_NUM";
        vm.AddIgnoredColumnCommand.Execute(null);
        vm.IgnoredColumns[0].HeaderName = "Test_Name";
        vm.AddPresetMissingMarkerCommand.Execute("N/A");
        vm.MissingPolicy = MissingValuePolicy.UseMissing;

        vm.Preview.SetSampleFile(zipPath);
        await vm.Preview.CurrentRefreshTask;

        Assert.True(vm.Preview.IsFilenameMatched);
        Assert.Contains("XXX123.1-6846", vm.Preview.FilenameSummaryText, StringComparison.Ordinal);
        Assert.True(vm.Preview.IsStructureResolved);
        Assert.True(vm.Preview.IsMeasurementValidationSuccess);
        Assert.Contains("2 data row(s)", vm.Preview.MeasurementValidationText, StringComparison.Ordinal);
        Assert.Contains("not full-file validation", vm.Preview.MeasurementValidationText, StringComparison.OrdinalIgnoreCase);

        // Switch MissingPolicy to StopImport -> bounded sample validation catches N/A at row 3
        vm.MissingPolicy = MissingValuePolicy.StopImport;
        await vm.Preview.CurrentRefreshTask;
        Assert.False(vm.Preview.IsMeasurementValidationSuccess);
        Assert.Contains("MissingValueRejected", vm.Preview.MeasurementValidationText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RapidSampleReplacementUsesLatestRequestWinsAndHandlesErrorsAndDisposalCleanly()
    {
        var sampleA = WriteSampleCsv("sample_a.csv", "BIN,T1\n1,10.0\n");
        var sampleB = WriteSampleCsv("sample_b.csv", "BIN,T1\n1,20.0\n");
        var corruptZip = Path.Combine(_tempDir, "corrupt.zip");
        await File.WriteAllTextAsync(corruptZip, "not-a-zip", TestContext.Current.CancellationToken);

        using var vm = CreateNewBuilder();
        vm.ProfileName = "LatestWins_FT";

        // Rapid sample switch A -> B: latest request (B) wins
        vm.Preview.SetSampleFile(sampleA);
        vm.Preview.SetSampleFile(sampleB);
        await vm.Preview.CurrentRefreshTask;
        Assert.Equal(sampleB, vm.Preview.SampleFilePath);
        Assert.Contains("20.0", vm.Preview.RawPreviewText, StringComparison.Ordinal);

        // Corrupt ZIP fails cleanly without modifying draft settings
        vm.Preview.SetSampleFile(corruptZip);
        await vm.Preview.CurrentRefreshTask;
        Assert.True(vm.Preview.HasOpenError);
        Assert.Equal("LatestWins_FT", vm.ProfileName);

        // Clearing sample and disposing releases all handles (files can be deleted immediately)
        vm.Preview.ClearSampleFile();
        Assert.False(vm.Preview.HasSampleFile);
        vm.Dispose();
        File.Delete(sampleA);
        File.Delete(sampleB);
        File.Delete(corruptZip);
    }

    [Fact]
    public void Large105MbSampleInspectionRemainsBoundedFastAndDisposesHandles()
    {
        var largePath = Path.Combine(_tempDir, "large_105mb.csv");
        var header = "BIN,SITE," + string.Join(',', Enumerable.Range(0, 40).Select(i => $"T_{i:D2}")) + "\n";
        var row = "1,1," + string.Join(',', Enumerable.Range(0, 40).Select(i => $"{i}.25")) + "\n";
        using (var fs = new FileStream(largePath, FileMode.Create, FileAccess.Write, FileShare.None, 65_536))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            sw.Write(header);
            var targetBytes = 105L * 1024 * 1024;
            while (fs.Length < targetBytes)
            {
                sw.Write(row);
            }
        }

        using var vm = CreateNewBuilder();
        vm.ProfileName = "LargePreview_FT";
        var profile = vm.BuildProfile();

        var service = new DatalogSamplePreviewService();
        var swInspect = Stopwatch.StartNew();
        var result = service.Inspect(profile, largePath, cancellationToken: TestContext.Current.CancellationToken);
        swInspect.Stop();

        Assert.Null(result.OpenError);
        Assert.NotNull(result.Preview);
        Assert.True(result.Preview.WasTruncatedByRowLimit);
        Assert.Equal(DatalogPreviewReader.DefaultMaxRows, result.Preview.RawLines.Count);
        Assert.NotNull(result.MeasurementValidation);
        Assert.True(result.MeasurementValidation.IsSuccess, result.MeasurementValidation.Summary);
        Assert.Equal(DatalogSamplePreviewService.DefaultMaxMeasurementRows, result.MeasurementValidation.CheckedRowCount);
        Assert.True(swInspect.ElapsedMilliseconds < 2_000, $"Bounded preview took {swInspect.ElapsedMilliseconds}ms, expected < 2000ms.");

        // File must not remain locked after inspection
        File.Delete(largePath);
    }

    [Fact]
    public void ZeroDataRowsMeasurementValidationIsNotReportedAsSuccess()
    {
        var path = Path.Combine(_tempDir, "LOT1_FT_20261127.csv");
        File.WriteAllText(path, "BIN,SITE\n1,0\n", new UTF8Encoding(false));

        using var vm = CreateNewBuilder();
        vm.ProfileName = "ZeroRows_FT";
        vm.HeaderRow.Kind = RowMatchKind.FixedRowIndex;
        vm.HeaderRow.RowNumber = 1;
        vm.DataStartRow.Kind = RowMatchKind.FixedRowIndex;
        vm.DataStartRow.RowNumber = 2;
        vm.BinColumn.Kind = ColumnSelectorKind.HeaderName;
        vm.BinColumn.HeaderName = "BIN";
        vm.SiteColumn.IsEnabled = true;
        vm.SiteColumn.Kind = ColumnSelectorKind.HeaderName;
        vm.SiteColumn.HeaderName = "SITE";
        var profile = vm.BuildProfile();

        var service = new DatalogSamplePreviewService();
        var result = service.Inspect(profile, path, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(result.OpenError);
        Assert.NotNull(result.Preview);
        Assert.True(result.Preview.Structure?.IsSuccess);
        Assert.NotNull(result.MeasurementValidation);
        Assert.False(result.MeasurementValidation.IsAttempted);
        Assert.False(result.MeasurementValidation.IsSuccess);
        Assert.Equal(0, result.MeasurementValidation.CheckedRowCount);
        Assert.Contains("0 measurement rows checked", result.MeasurementValidation.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MultipleCompositeFieldsDuplicateAndUnresolvedHeadersAndStaleAssignmentSafety()
    {
        var path = Path.Combine(_tempDir, "LOT1_FT_20261127.csv");
        File.WriteAllText(
            path,
            "BIN,SITE,GRP_A,GRP_B,T1\n" +
            "1,0,10/11,20/21,1.0\n",
            new UTF8Encoding(false));

        using var vm = CreateNewBuilder(debounce: TimeSpan.FromMilliseconds(80));
        vm.ProfileName = "CompositeMulti_FT";
        vm.HeaderRow.Kind = RowMatchKind.FixedRowIndex;
        vm.HeaderRow.RowNumber = 1;
        vm.DataStartRow.Kind = RowMatchKind.FixedRowIndex;
        vm.DataStartRow.RowNumber = 2;
        vm.BinColumn.Kind = ColumnSelectorKind.HeaderName;
        vm.BinColumn.HeaderName = "BIN";
        vm.SiteColumn.IsEnabled = true;
        vm.SiteColumn.Kind = ColumnSelectorKind.HeaderName;
        vm.SiteColumn.HeaderName = "SITE";

        var compA = new CompositeFieldDraft { Separator = "/" };
        compA.SourceColumn.Kind = ColumnSelectorKind.HeaderName;
        compA.SourceColumn.HeaderName = "GRP_A";
        compA.OutputNames.Add(new CompositeOutputNameItem("A1"));
        compA.OutputNames.Add(new CompositeOutputNameItem("A2"));

        var compB = new CompositeFieldDraft { Separator = "/" };
        compB.SourceColumn.Kind = ColumnSelectorKind.HeaderName;
        compB.SourceColumn.HeaderName = "GRP_B";
        compB.OutputNames.Add(new CompositeOutputNameItem("B1"));
        compB.OutputNames.Add(new CompositeOutputNameItem("B2"));

        vm.CompositeFields.Add(compA);
        vm.CompositeFields.Add(compB);

        vm.Preview.SetSampleFile(path);
        WaitRefreshSync(vm);

        Assert.True(vm.Preview.IsStructureResolved);
        Assert.Equal(4, vm.Preview.PreviewColumns.Count(c => c.IsCompositeExpanded));

        // Duplicate headers do not crash preview and surface structure diagnostic cleanly.
        var dupPath = Path.Combine(_tempDir, "LOT1_FT_20261127_dup.csv");
        File.WriteAllText(dupPath, "BIN,SITE,DUP,DUP\n1,0,1.0,2.0\n", new UTF8Encoding(false));
        vm.Preview.SetSampleFile(dupPath);
        WaitRefreshSync(vm);
        Assert.Equal(4, vm.Preview.PreviewColumns.Count);
        vm.Preview.SetSampleFile(path);
        WaitRefreshSync(vm);
        Assert.True(vm.Preview.IsStructureResolved);

        // Manual rule edit starts a pending debounced refresh (IsLoading = true); stale column assignment must be ignored.
        var staleCol = vm.Preview.PreviewColumns[1];
        var originalSiteName = vm.SiteColumn.HeaderName;
        vm.HeaderRow.RowNumber = 2; // triggers debounced RequestRefresh -> IsLoading = true
        Assert.True(vm.Preview.IsLoading);
        vm.Preview.AssignColumnRole(staleCol, PreviewColumnAction.SetAsBin);
        Assert.Equal("BIN", vm.BinColumn.HeaderName); // untouched by stale assignment
        Assert.Equal(originalSiteName, vm.SiteColumn.HeaderName);

        // Unresolved headers when HeaderRow points to non-existent row
        WaitRefreshSync(vm);
        Assert.False(vm.Preview.IsStructureResolved);
        Assert.Equal("C1", vm.Preview.PreviewColumns[0].DisplayHeader);
    }

    [Fact]
    public async Task SlowThenFastRequestLatestWinsAndDisposePreventsUpdates()
    {
        var fastPath = Path.Combine(_tempDir, "LOT_FAST_FT_20261127.csv");
        File.WriteAllText(fastPath, "BIN,SITE,T1\n1,0,9.5\n", new UTF8Encoding(false));

        var slowPath = Path.Combine(_tempDir, "LOT_SLOW_FT_20261127.csv");
        File.WriteAllText(slowPath, "BIN,SITE,T1\n1,0,1.5\n", new UTF8Encoding(false));

        using var vm = CreateNewBuilder(debounce: TimeSpan.FromMilliseconds(120));
        vm.Preview.SetSampleFile(slowPath);
        // Immediately supersede with fastPath before debounce completes
        vm.Preview.SetSampleFile(fastPath);
        await vm.Preview.CurrentRefreshTask;

        Assert.Equal(fastPath, vm.Preview.SampleFilePath);
        Assert.Contains("9.5", vm.Preview.RawPreviewText, StringComparison.Ordinal);

        // Disposing while refresh is queued prevents subsequent UI updates
        vm.Preview.RequestRefresh(immediate: false);
        vm.Dispose();
        await vm.Preview.CurrentRefreshTask;
        Assert.Equal(fastPath, vm.Preview.SampleFilePath);
    }

    [Fact]
    public void Large105MbSampleInspectionWithKeywordStructureRemainsBoundedAndFast()
    {
        var largePath = Path.Combine(_tempDir, "large_keyword_105mb.csv");
        var header = "Test_Name,BIN,SITE," + string.Join(',', Enumerable.Range(0, 40).Select(i => $"T_{i:D2}")) + "\n";
        var unit = "UNIT,bin,site," + string.Join(',', Enumerable.Range(0, 40).Select(_ => "V")) + "\n";
        var row = "DATA,1,1," + string.Join(',', Enumerable.Range(0, 40).Select(i => $"{i}.25")) + "\n";
        using (var fs = new FileStream(largePath, FileMode.Create, FileAccess.Write, FileShare.None, 65_536))
        using (var sw = new StreamWriter(fs, new UTF8Encoding(false)))
        {
            sw.Write(header);
            sw.Write(unit);
            var targetBytes = 105L * 1024 * 1024;
            while (fs.Length < targetBytes)
            {
                sw.Write(row);
            }
        }

        using var vm = CreateNewBuilder();
        vm.ProfileName = "LargeKeyword_FT";
        vm.HeaderRow.Kind = RowMatchKind.KeywordMatch;
        vm.HeaderRow.Keyword = "Test_Name";
        vm.DataStartRow.Kind = RowMatchKind.KeywordMatch;
        vm.DataStartRow.Keyword = "DATA";
        vm.UnitRow.IsEnabled = true;
        vm.UnitRow.Kind = RowMatchKind.KeywordMatch;
        vm.UnitRow.Keyword = "UNIT";
        vm.BinColumn.Kind = ColumnSelectorKind.HeaderName;
        vm.BinColumn.HeaderName = "BIN";
        vm.SiteColumn.IsEnabled = true;
        vm.SiteColumn.Kind = ColumnSelectorKind.HeaderName;
        vm.SiteColumn.HeaderName = "SITE";
        var ignoredTestName = new ColumnSelectorDraft("Ignored", isRequired: true)
        {
            Kind = ColumnSelectorKind.HeaderName,
            HeaderName = "Test_Name"
        };
        vm.IgnoredColumns.Add(ignoredTestName);
        var profile = vm.BuildProfile();

        var service = new DatalogSamplePreviewService();
        var swInspect = Stopwatch.StartNew();
        var result = service.Inspect(profile, largePath, cancellationToken: TestContext.Current.CancellationToken);
        swInspect.Stop();

        Assert.Null(result.OpenError);
        Assert.NotNull(result.Preview);
        Assert.True(result.Preview.Structure?.IsSuccess);
        Assert.True(result.Preview.WasTruncatedByRowLimit);
        Assert.Equal(DatalogPreviewReader.DefaultMaxRows, result.Preview.RawLines.Count);
        Assert.NotNull(result.MeasurementValidation);
        Assert.True(result.MeasurementValidation.IsSuccess, result.MeasurementValidation.Summary);
        Assert.Equal(DatalogSamplePreviewService.DefaultMaxMeasurementRows, result.MeasurementValidation.CheckedRowCount);
        Assert.True(swInspect.ElapsedMilliseconds < 2_000, $"Keyword bounded preview took {swInspect.ElapsedMilliseconds}ms, expected < 2000ms.");

        File.Delete(largePath);
    }

    [Fact]
    public async Task Wide512ColumnTableModelAndRapidRefreshStressRemainBounded()
    {
        var widePath = Path.Combine(_tempDir, "LOT1_FT_20261127_wide.csv");
        var header = "BIN,SITE," + string.Join(',', Enumerable.Range(0, 510).Select(i => $"T_{i:D3}")) + "\n";
        var row = "1,0," + string.Join(',', Enumerable.Range(0, 510).Select(i => $"{i}.5")) + "\n";
        using (var sw = new StreamWriter(widePath, false, new UTF8Encoding(false)))
        {
            sw.Write(header);
            for (var r = 0; r < 60; r++)
            {
                sw.Write(row);
            }
        }

        using var vm = CreateNewBuilder(debounce: TimeSpan.FromMilliseconds(5));
        vm.Preview.SetSampleFile(widePath);
        await vm.Preview.CurrentRefreshTask;

        Assert.Equal(512, vm.Preview.PreviewColumns.Count);
        Assert.Equal(50, vm.Preview.PreviewTableRows.Count);

        // Rapid refresh stress (25 iterations)
        for (var i = 0; i < 25; i++)
        {
            vm.Preview.RequestRefresh(immediate: i % 5 == 0);
        }

        await vm.Preview.CurrentRefreshTask;
        Assert.False(vm.Preview.IsLoading);
        Assert.Equal(512, vm.Preview.PreviewColumns.Count);
    }

    private static void WaitRefreshSync(ImportProfileBuilderViewModel vm)
    {
        vm.Preview.CurrentRefreshTask.GetAwaiter().GetResult();
    }
}
