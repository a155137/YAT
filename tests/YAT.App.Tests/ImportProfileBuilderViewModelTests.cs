using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;
using YAT.app.ViewModels;
using YAT.Infrastructure.ImportProfiles;

namespace YAT.App.Tests;

public sealed class ImportProfileBuilderViewModelTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _storeDir;
    private readonly JsonImportProfileSerializer _serializer = new();

    public ImportProfileBuilderViewModelTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft02-2-builder-tests", Guid.NewGuid().ToString("N"));
        _storeDir = Path.Combine(_tempDir, "UserData", "ImportProfiles");
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

    private sealed class FakeManagerDialogs : IImportProfileManagerDialogs
    {
        public List<(string Message, string? Detail)> Errors { get; } = [];
        public Task<bool> ConfirmDeleteAsync(ImportProfile profile) => Task.FromResult(true);
        public Task<string?> PickImportFileAsync() => Task.FromResult<string?>(null);
        public Task<string?> PickExportFileAsync(string suggestedFileName) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmOverwriteExportAsync(string destinationPath) => Task.FromResult(true);
        public Task<ImportDuplicateIdChoice> AskDuplicateIdAsync(ImportProfile importedProfile, ImportProfile existingProfile) =>
            Task.FromResult(ImportDuplicateIdChoice.Cancel);
        public Task ShowErrorAsync(string message, string? detail = null)
        {
            Errors.Add((message, detail));
            return Task.CompletedTask;
        }
    }

    private sealed class DelegateEditor(Func<ImportProfileEditorRequest, ImportProfile?> onEdit) : IImportProfileEditor
    {
        public bool IsAvailable => true;
        public Task<ImportProfile?> EditAsync(ImportProfileEditorRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(onEdit(request));
    }

    private sealed class FailingUpdateStore(IImportProfileStore inner, Exception failure) : IImportProfileStore
    {
        public string? StorageDirectoryPath => inner.StorageDirectoryPath;
        public ImportProfileStoreSnapshot Enumerate() => inner.Enumerate();
        public ImportProfile? Load(Guid id) => inner.Load(id);
        public void Create(ImportProfile profile) => inner.Create(profile);
        public void Update(ImportProfile profile) => throw failure;
        public void Delete(Guid id) => inner.Delete(id);
    }

    [Fact]
    public void CompleteDraftToModelRoundTripPreservesAllRulesIndicesOrderingAndAdvancedSettings()
    {
        var originalId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var original = new ImportProfile
        {
            Id = originalId,
            Name = "RoundTrip_FT",
            SchemaVersion = ImportProfile.CurrentSchemaVersion,
            FilenameRules = new FilenameRuleSet
            {
                AcceptedExtensions = [".Reg.zip", ".csv"],
                Mode = FilenameRuleMode.Structured,
                SegmentDelimiters = ['_', '.'],
                LotId = new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 0, Marker = ".", MarkerOccurrence = 2 },
                TestStage = new FilenameFieldRule { Kind = FilenameFieldKind.FixedRange, SegmentIndex = 2, StartIndex = 0, Length = 2 },
                Retest = new FilenameRetestRule
                {
                    RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                    OriginalToken = "ORG",
                    RetestMarker = "R",
                    RetestRoundField = new FilenameFieldRule { Kind = FilenameFieldKind.MarkerAfter, SegmentIndex = 2, Marker = "R", MarkerOccurrence = 1 }
                },
                FilePart = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 3 },
                AdvancedRegexPattern = "^(?<lot>.+)_(?<stage>F\\d)$"
            },
            StructureRules = new DatalogStructureRuleSet
            {
                Delimiter = "\t",
                HeaderRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Test_Name", ColumnIndex = 0, MatchMode = KeywordMatchMode.Exact },
                UnitRow = new RowIdentifier { Kind = RowMatchKind.KeywordMatch, Keyword = "Unit", ColumnIndex = -1, MatchMode = KeywordMatchMode.StartsWith },
                TargetRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 3 },
                LslRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 4 },
                UslRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 5 },
                DataStartRow = new RowIdentifier { Kind = RowMatchKind.FixedRowIndex, RowNumber = 6 },
                BinColumn = new ColumnSelector { Kind = ColumnSelectorKind.Index, ColumnIndex = 1 },
                SiteColumn = new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "SITE_NUM" },
                IgnoredColumns =
                [
                    new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "TIME_STAMP" },
                    new ColumnSelector { Kind = ColumnSelectorKind.Index, ColumnIndex = 4 }
                ],
                CompositeFields =
                [
                    new CompositeFieldRule(
                        new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "TAG" },
                        "/",
                        ["PART_A", "PART_B", "PART_C"])
                ]
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

        var vm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Edit, original));

        // Verify explicit 1-based UI positions
        Assert.Equal(1, vm.LotIdField.SegmentPosition);
        Assert.Equal(2, vm.LotIdField.MarkerOccurrence);
        Assert.Equal(3, vm.TestStageField.SegmentPosition);
        Assert.Equal(1, vm.TestStageField.StartPosition);
        Assert.Equal(2, vm.TestStageField.Length);
        Assert.Equal(4, vm.FilePartField.SegmentPosition);
        Assert.Equal(1, vm.HeaderRow.ColumnPosition);
        Assert.True(vm.UnitRow.SearchAnyColumn);
        Assert.Equal(2, vm.BinColumn.ColumnPosition);
        Assert.Equal(5, vm.IgnoredColumns[1].ColumnPosition);
        Assert.True(vm.IncludeBlankMissingMarker);
        Assert.Equal("^(?<lot>.+)_(?<stage>F\\d)$", vm.AdvancedRegexPattern);

        // Toggle to Regex and back to Structured -> must preserve AdvancedRegexPattern and Structured settings
        vm.FilenameMode = FilenameRuleMode.Regex;
        vm.FilenameMode = FilenameRuleMode.Structured;

        Assert.True(vm.TrySave());
        var roundTripped = vm.SavedProfile!;

        Assert.Equal(original.Id, roundTripped.Id);
        Assert.Equal(original.Name, roundTripped.Name);
        Assert.Equal(original.SchemaVersion, roundTripped.SchemaVersion);
        Assert.Equal(original.FilenameRules.AcceptedExtensions, roundTripped.FilenameRules.AcceptedExtensions);
        Assert.Equal(original.FilenameRules.Mode, roundTripped.FilenameRules.Mode);
        Assert.Equal(original.FilenameRules.SegmentDelimiters, roundTripped.FilenameRules.SegmentDelimiters);
        Assert.Equal(original.FilenameRules.LotId, roundTripped.FilenameRules.LotId);
        Assert.Equal(original.FilenameRules.TestStage, roundTripped.FilenameRules.TestStage);
        Assert.Equal(original.FilenameRules.Retest, roundTripped.FilenameRules.Retest);
        Assert.Equal(original.FilenameRules.FilePart, roundTripped.FilenameRules.FilePart);
        Assert.Equal(original.FilenameRules.AdvancedRegexPattern, roundTripped.FilenameRules.AdvancedRegexPattern);

        Assert.Equal(original.StructureRules.Delimiter, roundTripped.StructureRules.Delimiter);
        Assert.Equal(original.StructureRules.HeaderRow, roundTripped.StructureRules.HeaderRow);
        Assert.Equal(original.StructureRules.UnitRow, roundTripped.StructureRules.UnitRow);
        Assert.Equal(original.StructureRules.TargetRow, roundTripped.StructureRules.TargetRow);
        Assert.Equal(original.StructureRules.LslRow, roundTripped.StructureRules.LslRow);
        Assert.Equal(original.StructureRules.UslRow, roundTripped.StructureRules.UslRow);
        Assert.Equal(original.StructureRules.DataStartRow, roundTripped.StructureRules.DataStartRow);
        Assert.Equal(original.StructureRules.BinColumn, roundTripped.StructureRules.BinColumn);
        Assert.Equal(original.StructureRules.SiteColumn, roundTripped.StructureRules.SiteColumn);
        Assert.Equal(original.StructureRules.IgnoredColumns, roundTripped.StructureRules.IgnoredColumns);
        Assert.Single(roundTripped.StructureRules.CompositeFields);
        Assert.Equal(["PART_A", "PART_B", "PART_C"], roundTripped.StructureRules.CompositeFields[0].OutputNames);

        Assert.Equal(["1", "01", "001"], roundTripped.BinRules.GoodBins);
        Assert.Equal(["2", "02"], roundTripped.BinRules.NonRetestFailBins);
        Assert.Equal(["", "N/A", "NULL", "*****"], roundTripped.MissingValueRules.Markers);
        Assert.Equal(MissingValuePolicy.ReplaceWithZero, roundTripped.MissingValueRules.Policy);
    }

    [Fact]
    public void RepresentativeStructuredProfileCanBeConfiguredViaBuilderAndMatchesAllThreeSampleFilenames()
    {
        var vm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null));
        vm.ProfileName = "Representative_Structured_FT";
        vm.AcceptedExtensions.Clear();
        vm.NewExtensionText = ".Reg.zip";
        vm.AddExtensionCommand.Execute(null);

        vm.FilenameMode = FilenameRuleMode.Structured;
        vm.SegmentDelimitersText = "_";

        // Lot ID: 1st segment (1-based), after 2nd "."
        vm.LotIdField.Kind = FilenameFieldKind.MarkerAfter;
        vm.LotIdField.SegmentPosition = 1;
        vm.LotIdField.Marker = ".";
        vm.LotIdField.MarkerOccurrence = 2;

        // Test Stage: first 2 characters of 3rd segment (1-based)
        vm.TestStageField.Kind = FilenameFieldKind.FixedRange;
        vm.TestStageField.SegmentPosition = 3;
        vm.TestStageField.StartPosition = 1;
        vm.TestStageField.Length = 2;

        // Retest detection + Retest Round after "R"
        vm.RetestRoleField.IsEnabled = true;
        vm.RetestRoleField.Kind = FilenameFieldKind.WholeSegment;
        vm.RetestRoleField.SegmentPosition = 3;
        vm.RetestMarker = "R";
        vm.RetestRoundField.IsEnabled = true;
        vm.RetestRoundField.Kind = FilenameFieldKind.MarkerAfter;
        vm.RetestRoundField.SegmentPosition = 3;
        vm.RetestRoundField.Marker = "R";
        vm.RetestRoundField.MarkerOccurrence = 1;

        // File Part: 4th segment (1-based)
        vm.FilePartField.IsEnabled = true;
        vm.FilePartField.Kind = FilenameFieldKind.WholeSegment;
        vm.FilePartField.SegmentPosition = 4;

        Assert.True(vm.TrySave());
        var profile = vm.SavedProfile!;

        var matcher = new FilenameRuleMatcher();
        var r1 = matcher.Match(profile, "E320BD-262.20261123111219.XXX123.1-6846_TQB01RXAA1_F1_1_20261122171516_20261123110911.Reg.zip");
        var r2 = matcher.Match(profile, "E320BD-262.20261124094509.XXX123.1-6846_TQB01RXAA1_F1_2_20261123111608_20261124094143.Reg.zip");
        var r3 = matcher.Match(profile, "E320BD-262.20261124104655.XXX123.1-6846_TQB01RXAA1_F1B45678R1_1_20261124095542_20261124104352.Reg.zip");

        Assert.True(r1.IsSuccess, r1.ErrorMessage);
        Assert.Equal(("XXX123.1-6846", "F1", FtTestRole.Original, (int?)null, (int?)1),
            (r1.Value!.LotId, r1.Value.TestStage, r1.Value.TestRole, r1.Value.RetestRound, r1.Value.FilePart));

        Assert.True(r2.IsSuccess, r2.ErrorMessage);
        Assert.Equal(("XXX123.1-6846", "F1", FtTestRole.Original, (int?)null, (int?)2),
            (r2.Value!.LotId, r2.Value.TestStage, r2.Value.TestRole, r2.Value.RetestRound, r2.Value.FilePart));

        Assert.True(r3.IsSuccess, r3.ErrorMessage);
        Assert.Equal(("XXX123.1-6846", "F1", FtTestRole.Retest, (int?)1, (int?)1),
            (r3.Value!.LotId, r3.Value.TestStage, r3.Value.TestRole, r3.Value.RetestRound, r3.Value.FilePart));
    }

    [Fact]
    public void ValidationPreventsSaveAndSurfacesActionableSectionErrors()
    {
        var vm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null));

        // 1. Blank Profile Name + empty extensions
        vm.ProfileName = "   ";
        vm.AcceptedExtensions.Clear();

        // 2. Invalid filename rule (MarkerAfter with empty marker)
        vm.LotIdField.Kind = FilenameFieldKind.MarkerAfter;
        vm.LotIdField.Marker = "";

        // 3. Invalid structure (DataStartRow <= HeaderRow)
        vm.HeaderRow.RowNumber = 5;
        vm.DataStartRow.RowNumber = 5;

        // 4. Conflicting Bin & Site column + invalid Composite Field (empty output names)
        vm.BinColumn.HeaderName = "BIN";
        vm.SiteColumn.IsEnabled = true;
        vm.SiteColumn.HeaderName = "BIN";
        vm.AddCompositeFieldCommand.Execute(null);
        vm.CompositeFields[0].SourceColumn.HeaderName = "COMP";
        vm.CompositeFields[0].Separator = "";

        // 5. Conflicting Good / Non-Retest Fail Bin + no missing markers
        vm.GoodBins.Clear();
        vm.NewGoodBinText = "01";
        vm.AddGoodBinCommand.Execute(null);
        vm.NewNonRetestFailBinText = "01";
        vm.AddNonRetestFailBinCommand.Execute(null);
        vm.IncludeBlankMissingMarker = false;
        vm.CustomMissingMarkers.Clear();

        Assert.False(vm.TrySave());
        Assert.Null(vm.SavedProfile);
        Assert.True(vm.HasValidationErrors);
        Assert.NotNull(vm.ValidationSummary);
        Assert.NotNull(vm.GeneralErrors);
        Assert.NotNull(vm.FilenameErrors);
        Assert.NotNull(vm.StructureErrors);
        Assert.NotNull(vm.ColumnsErrors);
        Assert.NotNull(vm.BinMissingErrors);
    }

    [Fact]
    public async Task ManagerNewAndEditIntegrationPersistsValidCancelsCleanlyAndPreservesDataOnFailure()
    {
        var store = new JsonImportProfileStore(_storeDir);
        var dialogs = new FakeManagerDialogs();

        // 1. New -> Cancel creates no directory or file
        var cancelEditor = new DelegateEditor(_ => null);
        var cancelVm = new ImportProfileManagerViewModel(store, _serializer, dialogs, cancelEditor);
        Assert.True(cancelVm.NewCommand.CanExecute(null));
        await cancelVm.NewCommand.ExecuteAsync(null);
        Assert.Empty(cancelVm.Profiles);
        Assert.False(Directory.Exists(_storeDir));

        // 2. New -> Save via Builder creates valid Profile and refreshes selection
        var createEditor = new DelegateEditor(req =>
        {
            var bvm = new ImportProfileBuilderViewModel(req);
            bvm.ProfileName = "Created_In_Builder";
            Assert.True(bvm.TrySave());
            return bvm.SavedProfile;
        });
        var managerVm = new ImportProfileManagerViewModel(store, _serializer, dialogs, createEditor);
        await managerVm.NewCommand.ExecuteAsync(null);
        Assert.Single(managerVm.Profiles);
        var created = managerVm.Selected!;
        Assert.Equal("Created_In_Builder", created.Name);

        // 3. Edit -> Cancel preserves original Profile
        var editCancelManager = new ImportProfileManagerViewModel(store, _serializer, dialogs, cancelEditor);
        await editCancelManager.EditCommand.ExecuteAsync(null);
        Assert.Equal("Created_In_Builder", store.Load(created.Id)!.Name);

        // 4. Edit -> Storage failure preserves original Profile
        var failingStore = new FailingUpdateStore(store, new UnauthorizedAccessException("Read-only folder"));
        var editValidEditor = new DelegateEditor(req =>
        {
            var bvm = new ImportProfileBuilderViewModel(req);
            bvm.ProfileName = "Updated_In_Builder";
            Assert.True(bvm.TrySave());
            return bvm.SavedProfile;
        });
        var failDialogs = new FakeManagerDialogs();
        var failManager = new ImportProfileManagerViewModel(failingStore, _serializer, failDialogs, editValidEditor);
        await failManager.EditCommand.ExecuteAsync(null);
        Assert.Single(failDialogs.Errors);
        Assert.Equal("Created_In_Builder", store.Load(created.Id)!.Name);

        // 5. Edit -> Save preserves Profile identity and refreshes list
        var editManager = new ImportProfileManagerViewModel(store, _serializer, dialogs, editValidEditor);
        await editManager.EditCommand.ExecuteAsync(null);
        Assert.Single(editManager.Profiles);
        Assert.Equal(created.Id, editManager.Selected!.Id);
        Assert.Equal("Updated_In_Builder", editManager.Selected.Name);
        Assert.Equal("Updated_In_Builder", store.Load(created.Id)!.Name);
    }

    [Theory]
    [InlineData(MissingValuePolicy.StopImport)]
    [InlineData(MissingValuePolicy.UseMissing)]
    [InlineData(MissingValuePolicy.ReplaceWithZero)]
    public void BuilderSupportsAllThreeMissingPoliciesAndExplicitBlankMarker(MissingValuePolicy policy)
    {
        var vm = new ImportProfileBuilderViewModel(new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null));
        vm.ProfileName = $"Policy_{policy}";
        vm.IncludeBlankMissingMarker = true;
        vm.AddPresetMissingMarkerCommand.Execute("N/A");
        vm.AddPresetMissingMarkerCommand.Execute("*****");
        vm.MissingPolicy = policy;

        Assert.True(vm.TrySave());
        Assert.Equal(policy, vm.SavedProfile!.MissingValueRules.Policy);
        Assert.Equal(["", "N/A", "*****"], vm.SavedProfile.MissingValueRules.Markers);
    }
}
