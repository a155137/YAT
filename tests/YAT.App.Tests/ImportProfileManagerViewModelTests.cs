using System.Text;
using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;
using YAT.app.ViewModels;
using YAT.Infrastructure.ImportProfiles;

namespace YAT.App.Tests;

public sealed class ImportProfileManagerViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storeDir;
    private readonly string _ioDir;

    public ImportProfileManagerViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft02-1-vm-tests", Guid.NewGuid().ToString("N"));
        _storeDir = Path.Combine(_tempDir, "UserData", "ImportProfiles");
        _ioDir = Path.Combine(_tempDir, "io");
        Directory.CreateDirectory(_ioDir);
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

    private static ImportProfile CreateValidProfile(string name, Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name,
        SchemaVersion = ImportProfile.CurrentSchemaVersion,
        FilenameRules = new FilenameRuleSet
        {
            AcceptedExtensions = [".Reg.zip", ".csv"],
            Mode = FilenameRuleMode.Structured,
            SegmentDelimiters = ['.', '_'],
            LotId = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 0 },
            TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 1 },
            Retest = new FilenameRetestRule
            {
                RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                OriginalToken = "ORG",
                RetestMarker = "RT"
            }
        },
        StructureRules = new DatalogStructureRuleSet
        {
            Delimiter = ",",
            HeaderRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Test_Name", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
            DataStartRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "DATA_START", ColumnIndex = 0, MatchMode = KeywordMatchMode.StartsWith },
            BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "HARD_BIN" },
            SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" }
        },
        BinRules = new BinRuleSet
        {
            GoodBins = ["1", "01"],
            NonRetestFailBins = ["2", "5"]
        },
        MissingValueRules = new MissingValueRuleSet
        {
            Markers = ["", "N/A", "NULL"],
            Policy = MissingValuePolicy.UseMissing
        }
    };

    private sealed class FakeDialogs : IImportProfileManagerDialogs
    {
        public bool ConfirmDeleteResult { get; set; } = true;
        public string? PickImportPath { get; set; }
        public string? PickExportPath { get; set; }
        public bool ConfirmOverwriteResult { get; set; } = true;
        public ImportDuplicateIdChoice DuplicateIdChoice { get; set; } = ImportDuplicateIdChoice.Cancel;
        public List<(string Message, string? Detail)> Errors { get; } = [];
        public int ConfirmDeleteCalls { get; private set; }
        public int ConfirmOverwriteCalls { get; private set; }
        public int AskDuplicateIdCalls { get; private set; }

        public Task<bool> ConfirmDeleteAsync(ImportProfile profile)
        {
            ConfirmDeleteCalls++;
            return Task.FromResult(ConfirmDeleteResult);
        }

        public Task<string?> PickImportFileAsync() => Task.FromResult(PickImportPath);

        public Task<string?> PickExportFileAsync(string suggestedFileName) => Task.FromResult(PickExportPath);

        public Task<bool> ConfirmOverwriteExportAsync(string destinationPath)
        {
            ConfirmOverwriteCalls++;
            return Task.FromResult(ConfirmOverwriteResult);
        }

        public Task<ImportDuplicateIdChoice> AskDuplicateIdAsync(ImportProfile importedProfile, ImportProfile existingProfile)
        {
            AskDuplicateIdCalls++;
            return Task.FromResult(DuplicateIdChoice);
        }

        public Task ShowErrorAsync(string message, string? detail = null)
        {
            Errors.Add((message, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class FailingDeleteStore(IImportProfileStore inner, Exception failure) : IImportProfileStore
    {
        public ImportProfileStoreSnapshot Enumerate() => inner.Enumerate();
        public ImportProfile? Load(Guid id) => inner.Load(id);
        public void Create(ImportProfile profile) => inner.Create(profile);
        public void Update(ImportProfile profile) => inner.Update(profile);
        public void Delete(Guid id) => throw failure;
    }

    private sealed class FakeEditor(bool isAvailable, Func<ImportProfileEditorRequest, ImportProfile?>? handler = null) : IImportProfileEditor
    {
        public bool IsAvailable { get; } = isAvailable;
        public ImportProfileEditorRequest? LastRequest { get; private set; }

        public Task<ImportProfile?> EditAsync(ImportProfileEditorRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(handler?.Invoke(request));
        }
    }

    private readonly JsonImportProfileSerializer _serializer = new();

    private ImportProfileManagerViewModel CreateVm(
        IImportProfileStore store,
        IImportProfileManagerDialogs dialogs,
        IImportProfileEditor? editor = null,
        Func<Guid>? newId = null) =>
        new(store, _serializer, dialogs, editor, newId);

    [Fact]
    public void EmptyStoreShowsEmptyStateDoesNotCreateDirectoryAndDisablesSelectionCommands()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var dialogs = new FakeDialogs();
        var vm = CreateVm(store, dialogs);

        Assert.Empty(vm.Profiles);
        Assert.True(vm.IsEmpty);
        Assert.Null(vm.Selected);
        Assert.False(vm.HasSelection);
        Assert.False(vm.HasIssues);
        Assert.False(Directory.Exists(_storeDir));

        Assert.False(vm.IsEditorAvailable);
        Assert.False(vm.NewCommand.CanExecute(null));
        Assert.False(vm.EditCommand.CanExecute(null));
        Assert.False(vm.DuplicateCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.True(vm.ImportCommand.CanExecute(null));
    }

    [Fact]
    public void ProductionDefaultStorePathTargetsAppContextBaseDirectoryUserDataImportProfiles()
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "UserData", "ImportProfiles");
        Assert.Equal(expected, JsonImportProfileStore.DefaultDirectoryPath);
    }

    [Fact]
    public void LoadingMultipleValidProfilesPopulatesDetailsAndEnablesSelectionCommands()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var p1 = CreateValidProfile("Alpha_FT");
        var p2 = CreateValidProfile("Zeta_FT");
        store.Create(p2);
        store.Create(p1);

        var vm = CreateVm(store, new FakeDialogs());

        Assert.False(vm.IsEmpty);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal(["Alpha_FT", "Zeta_FT"], vm.Profiles.Select(p => p.Name).ToArray());
        Assert.Equal(p1.Id, vm.Selected!.Id);
        Assert.Equal("Alpha_FT", vm.SelectedName);
        Assert.Equal(p1.Id.ToString("D"), vm.SelectedIdText);
        Assert.Equal("v1", vm.SelectedSchemaVersionText);
        Assert.Equal(".Reg.zip, .csv", vm.SelectedExtensionsText);
        Assert.Contains("Structured", vm.SelectedFilenameModeText, StringComparison.Ordinal);
        Assert.Equal("Valid", vm.SelectedValidationStatus);
        Assert.Contains("HARD_BIN", vm.SelectedStructureSummary, StringComparison.Ordinal);
        Assert.Contains("1, 01", vm.SelectedBinRulesSummary, StringComparison.Ordinal);
        Assert.Contains("UseMissing", vm.SelectedMissingRulesSummary, StringComparison.Ordinal);

        Assert.False(vm.NewCommand.CanExecute(null));
        Assert.False(vm.EditCommand.CanExecute(null));
        Assert.True(vm.DuplicateCommand.CanExecute(null));
        Assert.True(vm.DeleteCommand.CanExecute(null));
        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task DuplicateCreatesNewIdentityResolvesNameCollisionsAndPreservesOriginal()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var original = CreateValidProfile("KYEC_FT");
        store.Create(original);

        var vm = CreateVm(store, new FakeDialogs());
        vm.Selected = vm.Profiles[0];

        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal("KYEC_FT (Copy)", vm.Selected!.Name);
        Assert.NotEqual(original.Id, vm.Selected.Id);

        // Duplicating the original again generates "(Copy 2)" instead of colliding.
        vm.Selected = vm.Profiles.Single(p => p.Id == original.Id);
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Equal(3, vm.Profiles.Count);
        Assert.Equal("KYEC_FT (Copy 2)", vm.Selected!.Name);
        Assert.Equal("KYEC_FT", store.Load(original.Id)!.Name);
    }

    [Fact]
    public async Task DeleteRequiresConfirmationSupportsCancellationAndPreservesListOnFailure()
    {
        var innerStore = new JsonImportProfileStore(_storeDir);
        var profile = CreateValidProfile("ToDelete");
        innerStore.Create(profile);

        var dialogs = new FakeDialogs { ConfirmDeleteResult = false };
        var vm = CreateVm(innerStore, dialogs);

        // 1. Cancel confirmation -> profile stays
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.ConfirmDeleteCalls);
        Assert.Single(vm.Profiles);
        Assert.NotNull(innerStore.Load(profile.Id));

        // 2. Delete failure -> error shown and list preserved
        var failingVm = CreateVm(
            new FailingDeleteStore(innerStore, new UnauthorizedAccessException("Access denied")),
            new FakeDialogs { ConfirmDeleteResult = true });
        await failingVm.DeleteCommand.ExecuteAsync(null);
        Assert.Single(failingVm.Profiles);
        Assert.NotNull(innerStore.Load(profile.Id));

        // 3. Confirmed delete succeeds
        dialogs.ConfirmDeleteResult = true;
        await vm.DeleteCommand.ExecuteAsync(null);
        Assert.Empty(vm.Profiles);
        Assert.True(vm.IsEmpty);
        Assert.Null(innerStore.Load(profile.Id));
    }

    [Fact]
    public async Task ImportValidAndDuplicateIdentityAndRejectMalformedOrUnsupportedWithoutMutatingSource()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var dialogs = new FakeDialogs();
        var vm = CreateVm(store, dialogs);

        // 1. Import valid JSON
        var sourceProfile = CreateValidProfile("Imported_FT");
        var sourceJson = ImportProfileJsonSerializer.Serialize(sourceProfile);
        var validPath = Path.Combine(_ioDir, "imported.json");
        await File.WriteAllBytesAsync(validPath, sourceJson, TestContext.Current.CancellationToken);

        dialogs.PickImportPath = validPath;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Single(vm.Profiles);
        Assert.Equal(sourceProfile.Id, vm.Selected!.Id);
        Assert.Equal(sourceJson, await File.ReadAllBytesAsync(validPath, TestContext.Current.CancellationToken));

        // 2. Import duplicate ID -> Cancel leaves store untouched
        dialogs.DuplicateIdChoice = ImportDuplicateIdChoice.Cancel;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.AskDuplicateIdCalls);
        Assert.Single(vm.Profiles);

        // 3. Import duplicate ID -> Import as Copy creates new GUID and non-conflicting name
        dialogs.DuplicateIdChoice = ImportDuplicateIdChoice.ImportAsCopy;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.NotEqual(sourceProfile.Id, vm.Selected!.Id);
        Assert.Equal("Imported_FT (Copy)", vm.Selected.Name);

        // 4. Malformed JSON -> rejected with error, source & store untouched
        var malformedPath = Path.Combine(_ioDir, "malformed.json");
        var malformedBytes = Encoding.UTF8.GetBytes("{ not json");
        await File.WriteAllBytesAsync(malformedPath, malformedBytes, TestContext.Current.CancellationToken);
        dialogs.PickImportPath = malformedPath;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Errors);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal(malformedBytes, await File.ReadAllBytesAsync(malformedPath, TestContext.Current.CancellationToken));

        // 5. Unsupported schemaVersion -> rejected with error, source & store untouched
        var unsupportedPath = Path.Combine(_ioDir, "future.json");
        var unsupportedBytes = Encoding.UTF8.GetBytes("""{"schemaVersion":99,"id":"11111111-1111-1111-1111-111111111111","name":"Future"}""");
        await File.WriteAllBytesAsync(unsupportedPath, unsupportedBytes, TestContext.Current.CancellationToken);
        dialogs.PickImportPath = unsupportedPath;
        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Equal(2, dialogs.Errors.Count);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal(unsupportedBytes, await File.ReadAllBytesAsync(unsupportedPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExportValidProfileHandlesOverwriteConfirmationCancellationAndFailure()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var profile = CreateValidProfile("Export_FT");
        store.Create(profile);

        var dialogs = new FakeDialogs();
        var vm = CreateVm(store, dialogs);

        var exportPath = Path.Combine(_ioDir, "exported.json");
        await File.WriteAllTextAsync(exportPath, "existing-content", TestContext.Current.CancellationToken);
        dialogs.PickExportPath = exportPath;

        // 1. Overwrite cancelled -> destination file unchanged
        dialogs.ConfirmOverwriteResult = false;
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(1, dialogs.ConfirmOverwriteCalls);
        Assert.Equal("existing-content", await File.ReadAllTextAsync(exportPath, TestContext.Current.CancellationToken));

        // 2. Overwrite confirmed -> destination holds valid serialized profile preserving ID
        dialogs.ConfirmOverwriteResult = true;
        await vm.ExportCommand.ExecuteAsync(null);
        var exported = ImportProfileJsonSerializer.Deserialize(await File.ReadAllBytesAsync(exportPath, TestContext.Current.CancellationToken));
        Assert.Equal(profile.Id, exported.Id);
        Assert.Equal(profile.Name, exported.Name);

        // 3. Destination write failure -> error reported, stored profile intact
        dialogs.PickExportPath = Path.Combine(_ioDir, "missing-folder", "nested", "out.json");
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Errors);
        Assert.NotNull(store.Load(profile.Id));
    }

    [Fact]
    public void StoreDiagnosticsDoNotPreventValidProfilesFromLoadingOrDeleteProblemFiles()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var valid = CreateValidProfile("Valid_FT");
        store.Create(valid);

        var brokenPath = Path.Combine(_storeDir, "broken.json");
        File.WriteAllText(brokenPath, "{ invalid");
        var futurePath = Path.Combine(_storeDir, "future.json");
        File.WriteAllText(futurePath, """{"schemaVersion":99}""");

        var vm = CreateVm(store, new FakeDialogs());

        Assert.Single(vm.Profiles);
        Assert.Equal(valid.Id, vm.Profiles[0].Id);
        Assert.True(vm.HasIssues);
        Assert.Contains("broken.json", vm.IssuesSummary!, StringComparison.Ordinal);
        Assert.Contains("future.json", vm.IssuesSummary!, StringComparison.Ordinal);
        Assert.True(File.Exists(brokenPath));
        Assert.True(File.Exists(futurePath));
    }

    [Fact]
    public async Task EditorIntegrationBoundaryPreservesIdOnEditAndRefusesInvalidDrafts()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var existing = CreateValidProfile("Original_FT");
        store.Create(existing);

        var dialogs = new FakeDialogs();
        var editor = new FakeEditor(isAvailable: true, req =>
        {
            if (req.Mode == ImportProfileEditorMode.Edit)
            {
                // Even if editor returns a different Id, VM enforces preserving existing Id.
                return req.ExistingProfile! with { Id = Guid.NewGuid(), Name = "Edited_FT" };
            }

            // Invalid draft (blank name) for Create
            return CreateValidProfile("   ");
        });

        var vm = CreateVm(store, dialogs, editor);
        Assert.True(vm.NewCommand.CanExecute(null));
        Assert.True(vm.EditCommand.CanExecute(null));

        // Invalid new draft is rejected without persisting
        await vm.NewCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Errors);
        Assert.Single(vm.Profiles);

        // Edit preserves existing identity
        await vm.EditCommand.ExecuteAsync(null);
        Assert.Equal(ImportProfileEditorMode.Edit, editor.LastRequest!.Mode);
        Assert.Single(vm.Profiles);
        Assert.Equal(existing.Id, vm.Selected!.Id);
        Assert.Equal("Edited_FT", vm.Selected.Name);
        Assert.Equal("Edited_FT", store.Load(existing.Id)!.Name);
    }

    [Fact]
    public async Task ExportRejectsManagedProfileStoreDirectoryAndSubdirectoriesWithoutOverwriting()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var profile = CreateValidProfile("Managed_FT");
        store.Create(profile);
        var managedFile = Path.Combine(_storeDir, $"{profile.Id:D}.json");
        var originalBytes = await File.ReadAllBytesAsync(managedFile, TestContext.Current.CancellationToken);

        var dialogs = new FakeDialogs();
        var vm = CreateVm(store, dialogs);

        // 1. Direct target inside managed store (case-insensitive)
        dialogs.PickExportPath = Path.Combine(_tempDir, "userdata", "importprofiles", $"{profile.Id:D}.json");
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Single(dialogs.Errors);
        Assert.Equal(0, dialogs.ConfirmOverwriteCalls);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(managedFile, TestContext.Current.CancellationToken));

        // 2. Nested subdirectory inside managed store
        dialogs.PickExportPath = Path.Combine(_storeDir, "nested", "export.json");
        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Equal(2, dialogs.Errors.Count);
        Assert.False(Directory.Exists(Path.Combine(_storeDir, "nested")));
    }

    [Fact]
    public async Task ExportFailurePreservesExistingDestinationCleansUpTempFileAndKeepsSourceIntact()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var profile = CreateValidProfile("AtomicExport_FT");
        store.Create(profile);

        var destPath = Path.Combine(_ioDir, "existing-export.json");
        var originalDest = """{"existing":"keep-intact"}""";
        await File.WriteAllTextAsync(destPath, originalDest, TestContext.Current.CancellationToken);

        var dialogs = new FakeDialogs { PickExportPath = destPath, ConfirmOverwriteResult = true };
        var vm = CreateVm(store, dialogs);
        vm.ExportWritePhaseHook = () => throw new IOException("simulated disk full during move");

        await vm.ExportCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Errors);
        Assert.Equal(originalDest, await File.ReadAllTextAsync(destPath, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.GetFiles(_ioDir, "*.tmp"));
        Assert.Equal(profile.Name, store.Load(profile.Id)!.Name);
    }

    [Theory]
    [InlineData("{ malformed json")]
    [InlineData("""{"schemaVersion":99,"id":"11111111-1111-1111-1111-111111111111"}""")]
    [InlineData("""{"schemaVersion":1,"id":"22222222-2222-2222-2222-222222222222","name":"Mismatched"}""")]
    public async Task ImportMatchingExistingProblematicFileIdFailsSafelyWithoutOverwritingOrDeleting(string existingContent)
    {
        var targetId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Directory.CreateDirectory(_storeDir);
        var problematicPath = Path.Combine(_storeDir, $"{targetId:D}.json");
        await File.WriteAllTextAsync(problematicPath, existingContent, TestContext.Current.CancellationToken);

        var store = new JsonImportProfileStore(_storeDir);
        var validOther = CreateValidProfile("OtherValid_FT");
        store.Create(validOther);

        var incoming = CreateValidProfile("Incoming_FT", targetId);
        var importFile = Path.Combine(_ioDir, "incoming.json");
        await File.WriteAllBytesAsync(importFile, _serializer.Serialize(incoming), TestContext.Current.CancellationToken);

        var dialogs = new FakeDialogs { PickImportPath = importFile };
        var vm = CreateVm(store, dialogs);

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Single(dialogs.Errors);
        Assert.Equal(existingContent, await File.ReadAllTextAsync(problematicPath, TestContext.Current.CancellationToken));
        Assert.Single(vm.Profiles);
        Assert.Equal(validOther.Id, vm.Profiles[0].Id);
    }

    [Fact]
    public async Task AsyncOperationBusyStateBlocksOverlappingCommandExecution()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var profile = CreateValidProfile("BusyGuard_FT");
        store.Create(profile);

        var pickerGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gatedDialogs = new GatedImportDialogs(pickerGate);
        var vm = CreateVm(store, gatedDialogs);

        var importTask = vm.ImportCommand.ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.ImportCommand.CanExecute(null));
        Assert.False(vm.DuplicateCommand.CanExecute(null));
        Assert.False(vm.DeleteCommand.CanExecute(null));
        Assert.False(vm.ExportCommand.CanExecute(null));

        // Overlapping duplicate attempt while busy does nothing
        await vm.DuplicateCommand.ExecuteAsync(null);
        Assert.Single(vm.Profiles);

        pickerGate.SetResult(null);
        await importTask;
        Assert.False(vm.IsBusy);
        Assert.True(vm.ImportCommand.CanExecute(null));
        Assert.True(vm.DuplicateCommand.CanExecute(null));
    }

    private sealed class GatedImportDialogs(TaskCompletionSource<string?> importGate) : IImportProfileManagerDialogs
    {
        public Task<bool> ConfirmDeleteAsync(ImportProfile profile) => Task.FromResult(false);
        public Task<string?> PickImportFileAsync() => importGate.Task;
        public Task<string?> PickExportFileAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmOverwriteExportAsync(string destinationPath) => Task.FromResult(false);
        public Task<ImportDuplicateIdChoice> AskDuplicateIdAsync(ImportProfile importedProfile, ImportProfile existingProfile) =>
            Task.FromResult(ImportDuplicateIdChoice.Cancel);
        public Task ShowErrorAsync(string message, string? detail = null) => Task.CompletedTask;
    }
}
