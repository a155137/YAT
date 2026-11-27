using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.Clipboard;
using YAT.app.Composition;
using YAT.app.Lifecycle;

namespace YAT.App.Tests;

public sealed class FtStreamingImportSessionTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FtFileDiscoveryService _discovery = new();
    private readonly FtSchemaPreparationService _schemaPrep = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FtStreamingImportSessionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft03-3-app-tests", Guid.NewGuid().ToString("N"));
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

    private sealed class StubClipboard : IClipboardTextReader, IClipboardTextWriter
    {
        public string? Text { get; set; }
        public Task<string?> ReadTextAsync(CancellationToken cancellationToken = default) => Task.FromResult(Text);
        public Task WriteTextAsync(string text, CancellationToken cancellationToken = default)
        {
            Text = text;
            return Task.CompletedTask;
        }
    }

    private sealed class StubDialogs : IProjectLifecycleDialogs
    {
        public string? SavePath { get; set; }
        public Task<SaveChangesChoice> AskSaveChangesAsync(string projectName, bool closing) => Task.FromResult(SaveChangesChoice.Discard);
        public Task<string?> PickProjectToOpenAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickSaveLocationAsync(string suggestedFileName) => Task.FromResult(SavePath);
        public Task<bool> ConfirmOverwriteAsync(string destinationPath) => Task.FromResult(true);
        public Task ShowErrorAsync(string message) => Task.CompletedTask;
    }

    private static ImportProfile CreateProfile() => new()
    {
        Id = Guid.NewGuid(),
        Name = "FT033_Session_Profile",
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = new FilenameRuleSet
        {
            AcceptedExtensions = [".csv"],
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
        BinRules = new BinRuleSet { GoodBins = ["01"], NonRetestFailBins = ["99"] },
        MissingValueRules = new MissingValueRuleSet { Markers = ["", "N/A"], Policy = MissingValuePolicy.UseMissing }
    };

    [Fact]
    public async Task MainWindowSession_ImportFtDatalogAsync_PreservesCommitOnlyDirtyStateAndNoAutoUiPublication()
    {
        var dataDir = Path.Combine(_tempDir, "ft_data");
        Directory.CreateDirectory(dataDir);
        var file1 = Path.Combine(dataDir, "E320BD-262.20261123111219.LOTA_TQB01RXAA1_F1_1_20261122171516_20261123110911.csv");
        File.WriteAllText(file1, "UNIT_ID,SOFT_BIN,SITE_NUM,IDD\n1,01,0,1.5\n2,05,1,2.5\n", new UTF8Encoding(false));

        var badDir = Path.Combine(_tempDir, "ft_bad");
        Directory.CreateDirectory(badDir);
        var badFile = Path.Combine(badDir, "E320BD-262.20261123111219.LOTA_TQB01RXAA1_F1_1_20261122171516_20261123110911.csv");
        File.WriteAllText(badFile, "UNIT_ID,SOFT_BIN,SITE_NUM,IDD\n1,01,0,BAD_NUM\n", new UTF8Encoding(false));

        var profile = CreateProfile();
        var validPlan = PreparePlan(profile, dataDir);
        var badPlan = PreparePlan(profile, badDir);

        var composition = new CompositionRoot(TimeProvider.System, Path.Combine(_tempDir, "projects"));
        using var workspace = composition.CreateProjectWorkspace();
        var clipboard = new StubClipboard();
        var dialogs = new StubDialogs();
        var lifecycle = composition.CreateProjectLifecycle(workspace, clipboard, clipboard, dialogs);

        Assert.True(await lifecycle.StartAsync());
        Assert.False(lifecycle.HasMeaningfulChanges);

        var session = lifecycle.CurrentSession!;
        var vm = lifecycle.Project!;
        var modifiedCount = 0;
        session.ProjectModified += (_, _) => modifiedCount++;

        // 1. Failed import must roll back and NOT raise ProjectModified or set HasMeaningfulChanges.
        var failRes = await session.ImportFtDatalogAsync("BadFT", badPlan, cancellationToken: Token);
        Assert.False(failRes.IsSuccess);
        Assert.Equal(0, modifiedCount);
        Assert.False(lifecycle.HasMeaningfulChanges);
        Assert.Single(vm.Worksheets);

        // 2. Successful import raises ProjectModified exactly once and does NOT auto-publish UI.
        var okRes = await session.ImportFtDatalogAsync("GoodFT", validPlan, cancellationToken: Token);
        Assert.True(okRes.IsSuccess);
        Assert.Equal(2, okRes.TotalRetainedRowCount);
        Assert.Equal(1, modifiedCount);
        Assert.True(lifecycle.HasMeaningfulChanges);
        Assert.Single(vm.Worksheets); // UI publication belongs to FT-04.

        // 3. Committed Worksheet is queryable via session paging immediately.
        var page = await session.LoadWorksheetPageAsync(okRes.Worksheet!.Id, 0, Token);
        Assert.Equal(2, page.TotalRowCount);
        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(["Lot_ID", "Bin", "Site", "Test_Role", "IDD"], page.Columns.Select(c => c.Name).ToArray());
    }

    [Fact]
    public async Task MainWindowSession_ImportFtDatalogAsync_CancellationWhileWaitingForGateDoesNotRunImport()
    {
        var dataDir = Path.Combine(_tempDir, "ft_gate_cancel");
        Directory.CreateDirectory(dataDir);
        var file1 = Path.Combine(dataDir, "E320BD-262.20261123111219.LOTA_TQB01RXAA1_F1_1_20261122171516_20261123110911.csv");
        File.WriteAllText(file1, "UNIT_ID,SOFT_BIN,SITE_NUM,IDD\n1,01,0,1.5\n", new UTF8Encoding(false));
        var plan = PreparePlan(CreateProfile(), dataDir);

        var composition = new CompositionRoot(TimeProvider.System, Path.Combine(_tempDir, "projects_gate"));
        using var workspace = composition.CreateProjectWorkspace();
        var clipboard = new StubClipboard();
        var lifecycle = composition.CreateProjectLifecycle(workspace, clipboard, clipboard, new StubDialogs());
        Assert.True(await lifecycle.StartAsync());
        var session = lifecycle.CurrentSession!;

        await session.SuspendAsync(Token);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                session.ImportFtDatalogAsync("GateCancelled", plan, cancellationToken: cts.Token));
        }
        finally
        {
            session.Resume();
        }

        Assert.False(lifecycle.HasMeaningfulChanges);
        Assert.Single(lifecycle.Project!.Worksheets);
    }

    [Fact]
    public async Task MainWindowSession_ImportFtDatalogAsync_SurvivesPostCommitCancellationAndNotificationFailure()
    {
        var dataDir = Path.Combine(_tempDir, "ft_post_notify");
        Directory.CreateDirectory(dataDir);
        var file1 = Path.Combine(dataDir, "E320BD-262.20261123111219.LOTA_TQB01RXAA1_F1_1_20261122171516_20261123110911.csv");
        File.WriteAllText(file1, "UNIT_ID,SOFT_BIN,SITE_NUM,IDD\n1,01,0,1.5\n", new UTF8Encoding(false));
        var plan = PreparePlan(CreateProfile(), dataDir);

        var composition = new CompositionRoot(TimeProvider.System, Path.Combine(_tempDir, "projects_notify"));
        using var workspace = composition.CreateProjectWorkspace();
        var clipboard = new StubClipboard();
        var lifecycle = composition.CreateProjectLifecycle(workspace, clipboard, clipboard, new StubDialogs());
        Assert.True(await lifecycle.StartAsync());
        var session = lifecycle.CurrentSession!;

        using var postCts = new CancellationTokenSource();
        session.ProjectModified += (_, _) =>
        {
            postCts.Cancel();
            throw new InvalidOperationException("Simulated ProjectModified listener failure");
        };

        var result = await session.ImportFtDatalogAsync("CommittedDespiteFault", plan, cancellationToken: postCts.Token);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Worksheet);
        Assert.Equal(1, result.TotalRetainedRowCount);
        Assert.Contains(result.Warnings, w => w.Code == FtImportDiagnosticCode.ProgressObserverFailed);

        // Verify the committed Worksheet is durably stored and readable.
        var page = await session.LoadWorksheetPageAsync(result.Worksheet!.Id, 0, Token);
        Assert.Equal(1, page.TotalRowCount);
    }

    private FtPreparedImportPlan PreparePlan(ImportProfile profile, string dir)
    {
        var discovery = _discovery.DiscoverFolder(profile, dir, Token);
        var plan = new FtImportSelectionModel(discovery).BuildImportPlan().Plan!;
        var union = _schemaPrep.ScanUnionSchema(plan, Token).UnionSchema!;
        return _schemaPrep.PrepareImportPlan(union, union.UnionMeasurementHeaders, Token).Plan!;
    }
}
