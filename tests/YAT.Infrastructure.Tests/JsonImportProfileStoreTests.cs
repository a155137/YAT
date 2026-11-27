using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.ImportProfiles;
using YAT.Infrastructure.ImportProfiles;

namespace YAT.Infrastructure.Tests;

public sealed class JsonImportProfileStoreTests : IDisposable
{
    private readonly string _tempRoot;

    public JsonImportProfileStoreTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "yat-profile-tests", Guid.NewGuid().ToString("N"));
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

    private static ImportProfile CreateProfile(Guid? id = null, string name = "OSAT_FT_測試") => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = new FilenameRuleSet
        {
            AcceptedExtensions = [".Reg.zip", ".csv"],
            Mode = FilenameRuleMode.Structured,
            SegmentDelimiters = ['.', '_'],
            LotId = new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 0, Marker = ".", MarkerOccurrence = 2 },
            TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.FixedRange, SegmentIndex = 5, StartIndex = 0, Length = 2 },
            Retest = new FilenameRetestRule
            {
                RetestMarker = "R",
                RetestRoundField = new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 5, Marker = "R" }
            },
            FilePart = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 6 }
        },
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = "\t",
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 1 },
            UnitRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 2 },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 6 },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "BIN" },
            SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE" },
            IgnoredColumns = [new ColumnSelector { Kind = ColumnSelectorKind.Index, ColumnIndex = 9 }]
        },
        BinRules = new BinRuleSet
        {
            GoodBins = ["1", "01", "001"],
            NonRetestFailBins = ["2", "02"]
        },
        MissingValueRules = new MissingValueRuleSet
        {
            Markers = ["", "N/A", "NULL", "*****"],
            Policy = MissingValuePolicy.ReplaceWithZero
        }
    };

    [Fact]
    public void JsonRoundTripPreservesIdentityUnicodeRulesAndLeadingZeroBins()
    {
        var original = CreateProfile(name: "京元電子_FT_晶圓測試_Profile");

        var bytes = ImportProfileJsonSerializer.Serialize(original);
        var restored = ImportProfileJsonSerializer.Deserialize(bytes);

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal("京元電子_FT_晶圓測試_Profile", restored.Name);
        Assert.Equal(original.SchemaVersion, restored.SchemaVersion);
        Assert.Equal([".Reg.zip", ".csv"], restored.FilenameRules.AcceptedExtensions);
        Assert.Equal(2, restored.FilenameRules.LotId.MarkerOccurrence);
        Assert.Equal("\t", restored.StructureRules.Delimiter);
        Assert.Equal(["1", "01", "001"], restored.BinRules.GoodBins);
        Assert.Equal(["2", "02"], restored.BinRules.NonRetestFailBins);
        Assert.Equal(MissingValuePolicy.ReplaceWithZero, restored.MissingValueRules.Policy);
    }

    [Fact]
    public void JsonDeserializeRejectsUnsupportedSchemaVersionAndMalformedJson()
    {
        var badVersionJson = "{\"id\":\"11111111-1111-1111-1111-111111111111\",\"name\":\"V99\",\"schemaVersion\":99}"u8.ToArray();
        var versionEx = Assert.Throws<ImportProfileStoreException>(() => ImportProfileJsonSerializer.Deserialize(badVersionJson));
        Assert.Equal(ImportProfileStoreError.UnsupportedSchemaVersion, versionEx.Error);

        var malformedJson = "{ not-json"u8.ToArray();
        var malformedEx = Assert.Throws<ImportProfileStoreException>(() => ImportProfileJsonSerializer.Deserialize(malformedJson));
        Assert.Equal(ImportProfileStoreError.MalformedJson, malformedEx.Error);

        var missingVersionJson = """
            {
              "id": "11111111-1111-1111-1111-111111111111",
              "name": "NoSchemaVersion",
              "filenameRules": {
                "acceptedExtensions": [".csv"],
                "mode": "structured",
                "segmentDelimiters": ["_"],
                "lotId": { "kind": "wholeSegment", "segmentIndex": 0 },
                "testStage": { "kind": "wholeSegment", "segmentIndex": 1 },
                "retest": { "retestMarker": "R" }
              },
              "structureRules": {
                "delimiter": ",",
                "headerRow": { "kind": "fixedRowIndex", "rowNumber": 1 },
                "dataStartRow": { "kind": "fixedRowIndex", "rowNumber": 2 },
                "binColumn": { "kind": "headerName", "headerName": "BIN" },
                "ignoredColumns": []
              },
              "binRules": { "goodBins": ["1"], "nonRetestFailBins": [] },
              "missingValueRules": { "markers": [""], "policy": "useMissing" }
            }
            """u8.ToArray();
        var missingVersionEx = Assert.Throws<ImportProfileStoreException>(() => ImportProfileJsonSerializer.Deserialize(missingVersionJson));
        Assert.Equal(ImportProfileStoreError.MalformedJson, missingVersionEx.Error);
    }

    [Fact]
    public void EmptyNonExistentDirectoryEnumeratesEmptyWithoutCreatingFolder()
    {
        var missingDir = Path.Combine(_tempRoot, "missing", "ImportProfiles");
        var store = new JsonImportProfileStore(missingDir);

        var snapshot = store.Enumerate();

        Assert.Empty(snapshot.Profiles);
        Assert.Empty(snapshot.Issues);
        Assert.False(Directory.Exists(missingDir));
    }

    [Fact]
    public void CreateLoadEnumerateUpdateDeleteWorkAcrossMultipleProfiles()
    {
        var store = new JsonImportProfileStore(_tempRoot);
        var p1 = CreateProfile(name: "Alpha");
        var p2 = CreateProfile(name: "Beta");

        store.Create(p1);
        store.Create(p2);

        var snapshot = store.Enumerate();
        Assert.Empty(snapshot.Issues);
        Assert.Equal(2, snapshot.Profiles.Count);
        Assert.Equal(p1.Id, store.Load(p1.Id)!.Id);

        var updated = p1 with { Name = "Alpha_Renamed" };
        store.Update(updated);
        Assert.Equal("Alpha_Renamed", store.Load(p1.Id)!.Name);
        Assert.Equal(2, store.Enumerate().Profiles.Count);

        store.Delete(p1.Id);
        Assert.Null(store.Load(p1.Id));
        var remaining = Assert.Single(store.Enumerate().Profiles);
        Assert.Equal(p2.Id, remaining.Id);
    }

    [Fact]
    public void CreateRejectsDuplicateIdentityAndDoesNotOverwrite()
    {
        var store = new JsonImportProfileStore(_tempRoot);
        var id = Guid.NewGuid();
        var original = CreateProfile(id, "Original");
        store.Create(original);

        var duplicate = CreateProfile(id, "OverwriteAttempt");
        var ex = Assert.Throws<ImportProfileStoreException>(() => store.Create(duplicate));
        Assert.Equal(ImportProfileStoreError.AlreadyExists, ex.Error);
        Assert.Equal("Original", store.Load(id)!.Name);
    }

    [Fact]
    public void DuplicateIdentityAcrossFilesIsReportedDeterministicallyWithoutDataLoss()
    {
        var store = new JsonImportProfileStore(_tempRoot);
        var id = Guid.NewGuid();
        var canonical = CreateProfile(id, "Canonical");
        store.Create(canonical);

        var copyFile = Path.Combine(_tempRoot, "manual-copy.json");
        File.WriteAllBytes(copyFile, ImportProfileJsonSerializer.Serialize(canonical with { Name = "Copied" }));

        var snapshot = store.Enumerate();
        var loaded = Assert.Single(snapshot.Profiles);
        Assert.Equal("Canonical", loaded.Name);
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(ImportProfileLoadIssueKind.DuplicateId, issue.Kind);
        Assert.Equal("manual-copy.json", issue.FileName);
    }

    [Fact]
    public void FailedAtomicUpdatePreservesPreviousProfileAndCleansTempFiles()
    {
        var store = new JsonImportProfileStore(_tempRoot);
        var original = CreateProfile(name: "Original");
        store.Create(original);

        store.WritePhaseHook = phase =>
        {
            if (phase == ImportProfileWritePhase.BeforeReplace)
            {
                throw new IOException("Simulated write interruption.");
            }
        };

        var ex = Assert.Throws<ImportProfileStoreException>(() => store.Update(original with { Name = "CorruptedUpdate" }));
        Assert.Equal(ImportProfileStoreError.StorageFailure, ex.Error);

        store.WritePhaseHook = null;
        Assert.Equal("Original", store.Load(original.Id)!.Name);
        Assert.Empty(Directory.GetFiles(_tempRoot, "*.tmp"));

        store.WritePhaseHook = phase =>
        {
            if (phase == ImportProfileWritePhase.WritingTemporaryFile)
            {
                throw new IOException("Simulated mid-write failure.");
            }
        };

        var midWriteEx = Assert.Throws<ImportProfileStoreException>(() => store.Update(original with { Name = "HalfWritten" }));
        Assert.Equal(ImportProfileStoreError.StorageFailure, midWriteEx.Error);
        store.WritePhaseHook = null;
        Assert.Equal("Original", store.Load(original.Id)!.Name);
        Assert.Empty(Directory.GetFiles(_tempRoot, "*.tmp"));
    }

    [Fact]
    public void MismatchedFilenameGuidIsRejectedOnLoadEnumerateAndUpdateWithoutOverwriting()
    {
        var store = new JsonImportProfileStore(_tempRoot);
        var fileId = Guid.NewGuid();
        var internalId = Guid.NewGuid();
        var wrongFilePath = Path.Combine(_tempRoot, $"{fileId:D}.json");
        var originalBytes = ImportProfileJsonSerializer.Serialize(CreateProfile(internalId, "InternalProfile"));
        File.WriteAllBytes(wrongFilePath, originalBytes);

        var loadEx = Assert.Throws<ImportProfileStoreException>(() => store.Load(fileId));
        Assert.Equal(ImportProfileStoreError.DuplicateIdentity, loadEx.Error);

        var snapshot = store.Enumerate();
        Assert.Empty(snapshot.Profiles);
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(ImportProfileLoadIssueKind.InvalidProfile, issue.Kind);
        Assert.Equal($"{fileId:D}.json", issue.FileName);

        var updateEx = Assert.Throws<ImportProfileStoreException>(() => store.Update(CreateProfile(fileId, "OverwriteAttempt")));
        Assert.Equal(ImportProfileStoreError.DuplicateIdentity, updateEx.Error);
        Assert.Equal(originalBytes, File.ReadAllBytes(wrongFilePath));
    }

    [Fact]
    public void UpdateRefusesToOverwriteMalformedOrUnsupportedSchemaFile()
    {
        var store = new JsonImportProfileStore(_tempRoot);
        var malformedId = Guid.NewGuid();
        var malformedPath = Path.Combine(_tempRoot, $"{malformedId:D}.json");
        File.WriteAllText(malformedPath, "{ broken");

        var malformedEx = Assert.Throws<ImportProfileStoreException>(() => store.Update(CreateProfile(malformedId, "TryOverwrite")));
        Assert.Equal(ImportProfileStoreError.MalformedJson, malformedEx.Error);
        Assert.Equal("{ broken", File.ReadAllText(malformedPath));

        var futureId = Guid.NewGuid();
        var futurePath = Path.Combine(_tempRoot, $"{futureId:D}.json");
        var futureJson = $"{{\"id\":\"{futureId:D}\",\"name\":\"Future\",\"schemaVersion\":99}}";
        File.WriteAllText(futurePath, futureJson);

        var futureEx = Assert.Throws<ImportProfileStoreException>(() => store.Update(CreateProfile(futureId, "TryOverwriteFuture")));
        Assert.Equal(ImportProfileStoreError.UnsupportedSchemaVersion, futureEx.Error);
        Assert.Equal(futureJson, File.ReadAllText(futurePath));
    }

    [Fact]
    public void CorruptProfileFileDoesNotDestroyOtherValidProfiles()
    {
        var store = new JsonImportProfileStore(_tempRoot);
        var valid = CreateProfile(name: "ValidProfile");
        store.Create(valid);

        var corruptPath = Path.Combine(_tempRoot, "broken.json");
        File.WriteAllText(corruptPath, "{ broken json");

        var snapshot = store.Enumerate();
        var loaded = Assert.Single(snapshot.Profiles);
        Assert.Equal(valid.Id, loaded.Id);
        var issue = Assert.Single(snapshot.Issues);
        Assert.Equal(ImportProfileLoadIssueKind.MalformedJson, issue.Kind);
        Assert.Equal("broken.json", issue.FileName);
        Assert.Equal("{ broken json", File.ReadAllText(corruptPath));
    }

    [Fact]
    public void DefaultStoragePathIsUnderAppBaseDirectoryAndIndependentOfCurrentWorkingDirectory()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "UserData", "ImportProfiles");
        Assert.Equal(Path.GetFullPath(expected), JsonImportProfileStore.DefaultDirectoryPath);

        var store = new JsonImportProfileStore(_tempRoot);
        var originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = Path.GetTempPath();
            var profile = CreateProfile(name: "CwdIndependent");
            store.Create(profile);
            Assert.NotNull(store.Load(profile.Id));
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }
    }
}
