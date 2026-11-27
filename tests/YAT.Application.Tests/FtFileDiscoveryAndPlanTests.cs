using System.Text;
using YAT.Application.FtImport;
using YAT.Application.ImportProfiles;

namespace YAT.Application.Tests;

public sealed class FtFileDiscoveryAndPlanTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FtFileDiscoveryService _discovery = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FtFileDiscoveryAndPlanTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "yat-ft03-1-tests", Guid.NewGuid().ToString("N"));
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

    private string TouchFile(string relativePath, string content = "NOT_OPENED_BY_DISCOVERY")
    {
        var full = Path.Combine(_tempDir, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        return full;
    }

    private static ImportProfile Profile() => FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile();

    // Representative OSAT filename helper matching FilenameRuleMatcherTests.CreateStructuredRepresentativeProfile().
    private static string OsatName(
        string lotId,
        string stage,
        int? retestRound,
        int part,
        string ext = ".Reg.zip",
        string stamp = "20261123111219")
    {
        var stageSeg = retestRound is { } r ? $"{stage}B45678R{r}" : stage;
        return $"E320BD-262.{stamp}.{lotId}_TQB01RXAA1_{stageSeg}_{part}_20261122171516_20261123110911{ext}";
    }

    // ---- A. Discovery ----

    [Fact]
    public void DiscoveryHandlesExplicitFilesNonRecursiveFolderCompoundExtensionsDuplicatesMissingCjkAndCancellation()
    {
        var profile = Profile();

        // Folder with supported compound (.Reg.zip), supported (.csv), unsupported (.txt, .zip), and nested subfolder file (must NOT be discovered).
        var f1 = TouchFile(Path.Combine("測 試 資料夾", OsatName("LOT001", "F1", null, 1, ".Reg.zip")));
        var f2 = TouchFile(Path.Combine("測 試 資料夾", OsatName("LOT001", "F1", null, 2, ".csv")));
        TouchFile(Path.Combine("測 試 資料夾", "readme.txt"));
        TouchFile(Path.Combine("測 試 資料夾", "plain_archive.zip"));
        TouchFile(Path.Combine("測 試 資料夾", "nested", OsatName("LOT_NESTED", "F1", null, 1, ".Reg.zip")));

        var folderResult = _discovery.DiscoverFolder(profile, Path.Combine(_tempDir, "測 試 資料夾"), Token);
        Assert.Equal(2, folderResult.Files.Count);
        Assert.All(folderResult.Files, f => Assert.True(f.IsIdentified));
        Assert.Equal(2, folderResult.SkippedUnsupportedFolderFileCount);
        Assert.DoesNotContain(folderResult.Files, f => f.Match?.LotId == "LOT_NESTED");
        Assert.Contains(folderResult.Files, f => f.SourcePath.Contains("測 試 資料夾", StringComparison.Ordinal));

        // Explicit files: duplicate physical paths (including Windows case-insensitive variant), missing file, unsupported extension, invalid filename.
        var unsupported = TouchFile("notes.txt");
        var badName = TouchFile("bad_name.csv");
        var missing = Path.Combine(_tempDir, "does_not_exist.Reg.zip");

        var explicitPaths = new List<string>
        {
            f1,
            OperatingSystem.IsWindows() ? f1.ToUpperInvariant() : f1,
            missing,
            unsupported,
            badName
        };

        var explicitResult = _discovery.DiscoverFiles(profile, explicitPaths, Token);
        Assert.Equal(1, explicitResult.SkippedDuplicatePathCount);
        Assert.Contains(explicitResult.Diagnostics, d => d.Code == FtImportDiagnosticCode.DuplicatePhysicalPath);
        Assert.Contains(explicitResult.Diagnostics, d => d.Code == FtImportDiagnosticCode.MissingPath && d.SourcePath == missing);
        Assert.Contains(explicitResult.Diagnostics, d => d.Code == FtImportDiagnosticCode.UnsupportedExtension && d.SourcePath == unsupported);
        Assert.Contains(explicitResult.Diagnostics, d => d.Code == FtImportDiagnosticCode.FilenameMatchFailed && d.SourcePath == badName);
        Assert.Equal(3, explicitResult.Files.Count); // f1 (identified), unsupported (unidentified), badName (unidentified)
        Assert.Single(explicitResult.Files, f => f.IsIdentified);

        // Cancellation before discovery throws OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => _discovery.DiscoverFiles(profile, [f1], cts.Token));
    }

    // ---- B. Grouping ----

    [Fact]
    public void GroupingHandlesRepresentativeFilenamesNumericRetestOrderNonContiguousRoundsMultipleStagesAndCollisions()
    {
        var profile = Profile();

        // Exact representative filenames from specification.
        var rep1 = TouchFile("E320BD-262.20261123111219.XXX123.1-6846_TQB01RXAA1_F1_1_20261122171516_20261123110911.Reg.zip");
        var rep2 = TouchFile("E320BD-262.20261124094509.XXX123.1-6846_TQB01RXAA1_F1_2_20261123111608_20261124094143.Reg.zip");
        var rep3 = TouchFile("E320BD-262.20261124104655.XXX123.1-6846_TQB01RXAA1_F1B45678R1_1_20261124095542_20261124104352.Reg.zip");

        // Numeric ordering RT2 vs RT10, non-contiguous RT5, multiple parts in RT2, second stage F2, second Lot.
        var rt2p2 = TouchFile(OsatName("XXX123.1-6846", "F1", 2, 2));
        var rt2p1 = TouchFile(OsatName("XXX123.1-6846", "F1", 2, 1));
        var rt10p1 = TouchFile(OsatName("XXX123.1-6846", "F1", 10, 1));
        var rt5p1 = TouchFile(OsatName("XXX123.1-6846", "F1", 5, 1));
        var f2orig = TouchFile(OsatName("XXX123.1-6846", "F2", null, 1));
        var lotB = TouchFile(OsatName("LOT-B", "F1", null, 1));

        // Ambiguous logical collision: different timestamp prefix & path, same Lot/Stage/Role/Round/Part as rep3.
        var rep3Collision = TouchFile(
            Path.Combine("alt", OsatName("XXX123.1-6846", "F1", 1, 1, stamp: "20261125999999")));

        var result = _discovery.DiscoverFiles(
            profile,
            [rt10p1, rep2, lotB, rep3, rep1, rt2p2, rt5p1, f2orig, rt2p1, rep3Collision],
            Token);

        Assert.Equal(2, result.LotGroups.Count);
        Assert.Equal("LOT-B", result.LotGroups[0].LotId);
        Assert.Equal("XXX123.1-6846", result.LotGroups[1].LotId);

        var lotX = result.LotGroups[1];
        Assert.Equal(["F1", "F2"], lotX.Stages.Select(s => s.TestStage).ToArray());

        var f1 = lotX.Stages[0];
        Assert.Equal([1, 2], f1.OriginalFiles.Select(f => f.Match!.FilePart!.Value).ToArray());
        // Numeric ordering: 1, 2, 5, 10 (NOT lexicographic 1, 10, 2, 5).
        Assert.Equal([1, 2, 5, 10], f1.RetestRounds.Select(r => r.RetestRound!.Value).ToArray());
        Assert.Equal([1, 2], f1.RetestRounds[1].Files.Select(f => f.Match!.FilePart!.Value).ToArray());

        // Logical collision is explicitly detected and flagged, never silently dropped.
        var collision = Assert.Single(result.Collisions);
        Assert.Equal(new FtLogicalFileKey("XXX123.1-6846", "F1", FtTestRole.Retest, 1, 1), collision.LogicalKey);
        Assert.Equal(2, collision.Files.Count);
        Assert.All(collision.Files, f => Assert.True(f.HasLogicalCollision));
        Assert.Contains(result.Diagnostics, d => d.Code == FtImportDiagnosticCode.LogicalFileCollision);
    }

    // ---- C. Selection, Mixed Stages & Logical Collision Resolution ----

    [Fact]
    public void SelectionModelSupportsDefaultAllLotAndFileTogglePartialStateCollisionResolutionAndSingleStageValidation()
    {
        var profile = Profile();
        var f1Orig = TouchFile(OsatName("LOT-A", "F1", null, 1));
        var f1Rt1a = TouchFile(OsatName("LOT-A", "F1", 1, 1, stamp: "20261124000001"));
        var f1Rt1b = TouchFile(Path.Combine("dup", OsatName("LOT-A", "F1", 1, 1, stamp: "20261124000002")));
        var f2Orig = TouchFile(OsatName("LOT-A", "F2", null, 1));
        var badFile = TouchFile("unmatched.csv");

        var discovery = _discovery.DiscoverFiles(profile, [f1Orig, f1Rt1a, f1Rt1b, f2Orig, badFile], Token);
        var model = new FtImportSelectionModel(discovery);

        // Default: all 4 identified files selected, unidentified file NOT selected.
        Assert.Equal(4, model.SelectedFileCount);
        Assert.Equal(FtSelectionState.Selected, model.GetLotSelectionState("LOT-A"));

        // Default plan fails due to both mixed stages (F1, F2) AND unresolved logical collision.
        var invalidPlan = model.BuildImportPlan();
        Assert.False(invalidPlan.IsValid);
        Assert.Null(invalidPlan.Plan);
        Assert.Contains(invalidPlan.Errors, e => e.Code == FtImportDiagnosticCode.MultipleTestStages && e.Message.Contains("F1") && e.Message.Contains("F2"));
        Assert.Contains(invalidPlan.Errors, e => e.Code == FtImportDiagnosticCode.LogicalFileCollision);

        // Deselect F2 -> Partial selection state on LOT-A; still blocked by logical collision.
        model.SetStageSelected("LOT-A", "F2", false);
        Assert.Equal(FtSelectionState.PartiallySelected, model.GetLotSelectionState("LOT-A"));
        Assert.Equal(FtSelectionState.Unselected, model.GetStageSelectionState("LOT-A", "F2"));
        Assert.Equal(FtSelectionState.Selected, model.GetStageSelectionState("LOT-A", "F1"));

        var collisionOnly = model.BuildImportPlan();
        Assert.False(collisionOnly.IsValid);
        Assert.Single(collisionOnly.Errors, e => e.Code == FtImportDiagnosticCode.LogicalFileCollision);

        // Resolve logical collision by deselecting one colliding file -> plan becomes valid!
        var collidingId = discovery.Files.First(f => f.SourcePath == f1Rt1b).Id;
        model.SetFileSelected(collidingId, false);
        Assert.Equal(FtSelectionState.PartiallySelected, model.GetStageSelectionState("LOT-A", "F1"));
        Assert.Equal(5, discovery.Files.Count); // Deselecting never removes from discovery results.

        var validPlan = model.BuildImportPlan();
        Assert.True(validPlan.IsValid);
        Assert.NotNull(validPlan.Plan);
        Assert.Equal("F1", validPlan.Plan!.TestStage);
        Assert.Equal(2, validPlan.Plan.OrderedFiles.Count);

        // Forcing selection of an unidentified file is rejected.
        var badId = discovery.Files.First(f => !f.IsIdentified).Id;
        model.SetFileSelected(badId, true);
        var unresolvedPlan = model.BuildImportPlan();
        Assert.False(unresolvedPlan.IsValid);
        Assert.Contains(unresolvedPlan.Errors, e => e.Code == FtImportDiagnosticCode.UnresolvedSelectedFile);
        model.SetFileSelected(badId, false);

        // Deselecting Lot deselects all its files -> empty selection rejected.
        model.SetLotSelected("LOT-A", false);
        Assert.Equal(FtSelectionState.Unselected, model.GetLotSelectionState("LOT-A"));
        Assert.Equal(0, model.SelectedFileCount);
        var emptyPlan = model.BuildImportPlan();
        Assert.False(emptyPlan.IsValid);
        Assert.Single(emptyPlan.Errors, e => e.Code == FtImportDiagnosticCode.EmptySelection);
    }

    // ---- D. Retest Resolution & Partial Selection Diagnostics ----

    [Fact]
    public void LatestSelectedRetestRoundIsResolvedIndependentlyPerLotAndEmitsNonBlockingPartialDiagnostics()
    {
        var profile = Profile();

        // Lot A: Original, RT1, RT2 (2 parts)
        var aOrig = TouchFile(OsatName("LOT-A", "F1", null, 1));
        var aRt1 = TouchFile(OsatName("LOT-A", "F1", 1, 1));
        var aRt2p1 = TouchFile(OsatName("LOT-A", "F1", 2, 1));
        var aRt2p2 = TouchFile(OsatName("LOT-A", "F1", 2, 2));

        // Lot B: Original, RT1
        var bOrig = TouchFile(OsatName("LOT-B", "F1", null, 1));
        var bRt1 = TouchFile(OsatName("LOT-B", "F1", 1, 1));

        // Lot C: Original only
        var cOrig = TouchFile(OsatName("LOT-C", "F1", null, 1));

        // Lot D: Retest-only (Original excluded)
        var dOrig = TouchFile(OsatName("LOT-D", "F1", null, 1));
        var dRt1 = TouchFile(OsatName("LOT-D", "F1", 1, 1));

        var discovery = _discovery.DiscoverFiles(profile, [aOrig, aRt1, aRt2p1, aRt2p2, bOrig, bRt1, cOrig, dOrig, dRt1], Token);
        var model = new FtImportSelectionModel(discovery);

        // Full selection: A=2, B=1, C=null, D=1
        var full = model.BuildImportPlan();
        Assert.True(full.IsValid);
        Assert.Empty(full.Warnings);
        Assert.Equal(2, full.Plan!.Lots.Single(l => l.LotId == "LOT-A").LatestSelectedRetestRound);
        Assert.Equal(FtLatestRetestKind.NumberedRound, full.Plan.Lots.Single(l => l.LotId == "LOT-A").LatestRetestKind);
        Assert.Equal([1, 2], full.Plan.Lots.Single(l => l.LotId == "LOT-A").RetestRounds.Last().FileParts);
        Assert.Equal(1, full.Plan.Lots.Single(l => l.LotId == "LOT-B").LatestSelectedRetestRound);
        Assert.Null(full.Plan.Lots.Single(l => l.LotId == "LOT-C").LatestSelectedRetestRound);
        Assert.Equal(FtLatestRetestKind.None, full.Plan.Lots.Single(l => l.LotId == "LOT-C").LatestRetestKind);
        Assert.False(full.Plan.Lots.Single(l => l.LotId == "LOT-C").HasRetestFiles);

        // Deselect RT2 from Lot A -> Lot A latest selected Retest Round becomes 1 (with HigherRetestRoundsExcluded warning).
        // Deselect Original from Lot D -> Retest-only selection (with OriginalFilesExcluded + RetestOnlySelection warnings).
        model.SetFileSelected(discovery.Files.Single(f => f.SourcePath == aRt2p1).Id, false);
        model.SetFileSelected(discovery.Files.Single(f => f.SourcePath == aRt2p2).Id, false);
        model.SetFileSelected(discovery.Files.Single(f => f.SourcePath == dOrig).Id, false);

        var partial = model.BuildImportPlan();
        Assert.True(partial.IsValid);
        Assert.Equal(1, partial.Plan!.Lots.Single(l => l.LotId == "LOT-A").LatestSelectedRetestRound);
        Assert.Equal(1, partial.Plan.Lots.Single(l => l.LotId == "LOT-B").LatestSelectedRetestRound);
        Assert.False(partial.Plan.Lots.Single(l => l.LotId == "LOT-D").HasOriginalFiles);
        Assert.True(partial.Plan.Lots.Single(l => l.LotId == "LOT-D").HasRetestFiles);

        Assert.Contains(partial.Warnings, w => w.Code == FtImportDiagnosticCode.HigherRetestRoundsExcluded && w.LotId == "LOT-A");
        Assert.Contains(partial.Warnings, w => w.Code == FtImportDiagnosticCode.PartialLotSelection && w.LotId == "LOT-A");
        Assert.Contains(partial.Warnings, w => w.Code == FtImportDiagnosticCode.OriginalFilesExcluded && w.LotId == "LOT-D");
        Assert.Contains(partial.Warnings, w => w.Code == FtImportDiagnosticCode.RetestOnlySelection && w.LotId == "LOT-D");

        // Partial selection within latest round (select only Part 1 of Lot A RT2)
        model.SetFileSelected(discovery.Files.Single(f => f.SourcePath == aRt2p1).Id, true);
        var partialRound = model.BuildImportPlan();
        Assert.True(partialRound.IsValid);
        Assert.Equal(2, partialRound.Plan!.Lots.Single(l => l.LotId == "LOT-A").LatestSelectedRetestRound);
        Assert.Equal([1], partialRound.Plan.Lots.Single(l => l.LotId == "LOT-A").RetestRounds.Last().FileParts);
        Assert.Contains(partialRound.Warnings, w => w.Code == FtImportDiagnosticCode.PartialRoundSelection && w.LotId == "LOT-A");
    }

    // ---- E. Import Plan Snapshot Safety & No Content Loading ----

    [Fact]
    public void ImportPlanIsImmutableSnapshotIndependentOfLaterSelectionOrProfileMutationsAndNeverReadsFileContents()
    {
        var mutableGoodBins = new List<string> { "1", "01" };
        var baseProfile = Profile();
        var profile = baseProfile with
        {
            BinRules = baseProfile.BinRules with { GoodBins = mutableGoodBins }
        };

        // Write corrupt/locked content: discovery & plan must NEVER open or decompress the file.
        var filePath = TouchFile(OsatName("LOT-LOCK", "F1", null, 1), "CORRUPT_NOT_A_ZIP");
        using var exclusiveLock = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var discovery = _discovery.DiscoverFiles(profile, [filePath], Token);
        var model = new FtImportSelectionModel(discovery);
        var planResult = model.BuildImportPlan();
        Assert.True(planResult.IsValid);
        var plan = planResult.Plan!;

        // Mutate selection AND caller's original profile list after plan creation.
        model.ClearSelection();
        mutableGoodBins.Clear();
        mutableGoodBins.Add("999");

        Assert.Single(plan.OrderedFiles);
        Assert.Equal(filePath, plan.OrderedFiles[0].SourcePath);
        Assert.Equal(["1", "01"], plan.ProfileSnapshot.BinRules.GoodBins);
    }

    private static ImportProfile RegexUnnumberedRetestProfile()
    {
        var baseProfile = Profile();
        return baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                Mode = FilenameRuleMode.Regex,
                // Matches both numbered (_F1R1_ / _F1R2_) and unnumbered (_F1R_) Retest tokens, or _F1_ for Original.
                AdvancedRegexPattern = @"^(?<lot_id>[^_]+)_(?<test_stage>F\d+)(?:(?<retest>R)(?<retest_round>\d+)?)?(?:_(?<file_part>\d+))?",
                Retest = new FilenameRetestRule { OriginalToken = "Original", RetestMarker = "R" }
            }
        };
    }

    [Fact]
    public void OriginalOnlyPlanSetsLatestRetestKindNone()
    {
        var profile = Profile();
        var f1 = TouchFile(OsatName("LOT-ORIG", "F1", null, 1));
        var f2 = TouchFile(OsatName("LOT-ORIG", "F1", null, 2));

        var discovery = _discovery.DiscoverFiles(profile, [f1, f2], Token);
        var planResult = new FtImportSelectionModel(discovery).BuildImportPlan();

        Assert.True(planResult.IsValid);
        var lot = Assert.Single(planResult.Plan!.Lots);
        Assert.True(lot.HasOriginalFiles);
        Assert.False(lot.HasRetestFiles);
        Assert.Equal(FtLatestRetestKind.None, lot.LatestRetestKind);
        Assert.Null(lot.LatestSelectedRetestRound);
        Assert.Empty(lot.RetestRounds);
        Assert.Equal(2, lot.OriginalFiles.Count);
    }

    [Fact]
    public void UnnumberedRetestOnlyPlanSetsUnnumberedGroupAndPreservesAllFileParts()
    {
        // Profile without RetestRoundField (AdvancedRegex where retest_round is omitted, or RetestMarker without round).
        var baseProfile = Profile();
        var unnumberedProfile = baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with
            {
                Retest = new FilenameRetestRule
                {
                    RoleField = new FilenameFieldRule { Kind = FilenameFieldKind.WholeSegment, SegmentIndex = 2 },
                    RetestMarker = "R",
                    RetestRoundField = null
                }
            }
        };

        var orig = TouchFile(OsatName("LOT-UN", "F1", null, 1));
        var rtPart1 = TouchFile(OsatName("LOT-UN", "F1", 1, 1, stamp: "20261124000001"));
        var rtPart2 = TouchFile(OsatName("LOT-UN", "F1", 1, 2, stamp: "20261124000002"));

        var discovery = _discovery.DiscoverFiles(unnumberedProfile, [orig, rtPart2, rtPart1], Token);
        var planResult = new FtImportSelectionModel(discovery).BuildImportPlan();

        Assert.True(planResult.IsValid);
        var lot = Assert.Single(planResult.Plan!.Lots);
        Assert.True(lot.HasOriginalFiles);
        Assert.True(lot.HasRetestFiles);
        Assert.Equal(FtLatestRetestKind.UnnumberedGroup, lot.LatestRetestKind);
        Assert.Null(lot.LatestSelectedRetestRound);
        Assert.Empty(lot.SelectedRetestRounds);

        var roundGroup = Assert.Single(lot.RetestRounds);
        Assert.Null(roundGroup.RetestRound);
        Assert.Equal([1, 2], roundGroup.FileParts);
        Assert.Equal(2, roundGroup.Files.Count);
    }

    [Fact]
    public void MixedNumberedAndUnnumberedSelectedRetestFilesRejectsPlanUnlessAmbiguousFilesDeselected()
    {
        // Regex mode where "LOT1_F1R1_1.csv" has RetestRound=1, while "LOT1_F1R_1.csv" has RetestRound=null.
        // Note: FilenameRuleMatcher treats unnumbered 'retest' group "R" as UnmatchedRetestRule in Regex mode unless
        // RetestRoundField is omitted in Structured mode; so we test via Structured-like or direct Discovery result
        // representing mixed numbered & unnumbered Retest files in the same Lot/Stage.
        var profile = Profile();
        var origFile = TouchFile(OsatName("LOT-MIX", "F1", null, 1));
        var numberedRtFile = TouchFile(OsatName("LOT-MIX", "F1", 1, 1));
        var unnumberedRtFile = TouchFile("unnumbered_rt.csv");

        var baseDiscovery = _discovery.DiscoverFiles(profile, [origFile, numberedRtFile], Token);
        var unnumberedDiscovered = new FtDiscoveredFile(
            Id: Guid.NewGuid(),
            SourcePath: unnumberedRtFile,
            NormalizedPathKey: unnumberedRtFile.ToUpperInvariant(),
            FileName: "unnumbered_rt.csv",
            IsFromFolder: false,
            IsIdentified: true,
            Match: new MatchedDatalogFilename("unnumbered_rt.csv", ".csv", "LOT-MIX", "F1", FtTestRole.Retest, RetestRound: null, FilePart: 1),
            LogicalKey: new FtLogicalFileKey("LOT-MIX", "F1", FtTestRole.Retest, null, 1),
            HasLogicalCollision: false,
            IdentificationDiagnostic: null);

        var combinedFiles = baseDiscovery.Files.Append(unnumberedDiscovered).ToArray();
        var origList = baseDiscovery.LotGroups[0].Stages[0].OriginalFiles;
        var rtRounds = baseDiscovery.LotGroups[0].Stages[0].RetestRounds
            .Append(new FtRetestRoundGroup(null, Array.AsReadOnly([unnumberedDiscovered])))
            .ToArray();
        var mixedDiscovery = baseDiscovery with
        {
            Files = Array.AsReadOnly(combinedFiles),
            LotGroups = Array.AsReadOnly([new FtLotGroup("LOT-MIX", Array.AsReadOnly([new FtStageGroup("F1", origList, Array.AsReadOnly(rtRounds))]))])
        };

        var model = new FtImportSelectionModel(mixedDiscovery);

        // Both numbered and unnumbered Retest files selected -> Case D must reject with AmbiguousMixedRetestRounds.
        var rejected = model.BuildImportPlan();
        Assert.False(rejected.IsValid);
        Assert.Null(rejected.Plan);
        Assert.Contains(rejected.Errors, e => e.Code == FtImportDiagnosticCode.AmbiguousMixedRetestRounds && e.LotId == "LOT-MIX");

        // Excluding the unnumbered Retest file resolves the ambiguity -> valid NumberedRound plan.
        model.SetFileSelected(unnumberedDiscovered.Id, false);
        var resolvedNumbered = model.BuildImportPlan();
        Assert.True(resolvedNumbered.IsValid);
        var lotNum = Assert.Single(resolvedNumbered.Plan!.Lots);
        Assert.Equal(FtLatestRetestKind.NumberedRound, lotNum.LatestRetestKind);
        Assert.Equal(1, lotNum.LatestSelectedRetestRound);

        // Selecting only unnumbered Retest (excluding numbered Retest) -> valid UnnumberedGroup plan.
        model.SetFileSelected(unnumberedDiscovered.Id, true);
        model.SetFileSelected(baseDiscovery.Files.Single(f => f.SourcePath == numberedRtFile).Id, false);
        var resolvedUnnumbered = model.BuildImportPlan();
        Assert.True(resolvedUnnumbered.IsValid);
        var lotUn = Assert.Single(resolvedUnnumbered.Plan!.Lots);
        Assert.Equal(FtLatestRetestKind.UnnumberedGroup, lotUn.LatestRetestKind);
        Assert.Null(lotUn.LatestSelectedRetestRound);
    }

    [Fact]
    public void ImportPlanNestedProfileAndPlanCollectionsAreReadOnlySnapshots()
    {
        var baseProfile = Profile();
        var compositeOutputs = new List<string> { "A1", "A2" };
        var profile = baseProfile with
        {
            StructureRules = baseProfile.StructureRules with
            {
                CompositeFields = [new CompositeFieldRule(new ColumnSelector { Kind = ColumnSelectorKind.HeaderName, HeaderName = "COMP" }, "/", compositeOutputs)]
            }
        };

        var f1 = TouchFile(OsatName("LOT-RO", "F1", null, 1));
        var discovery = _discovery.DiscoverFiles(profile, [f1], Token);
        var planResult = new FtImportSelectionModel(discovery).BuildImportPlan();
        Assert.True(planResult.IsValid);
        var plan = planResult.Plan!;

        // Mutating caller's nested OutputNames after plan creation does not affect snapshot.
        compositeOutputs.Add("A3");
        Assert.Equal(["A1", "A2"], plan.ProfileSnapshot.StructureRules.CompositeFields[0].OutputNames);

        // Even casting IReadOnlyList<T> to IList<T> must reject in-place mutation on both Plan and ProfileSnapshot collections.
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.ProfileSnapshot.BinRules.GoodBins)[0] = "MUTATED");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.ProfileSnapshot.FilenameRules.AcceptedExtensions)[0] = ".bad");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.ProfileSnapshot.StructureRules.CompositeFields[0].OutputNames)[0] = "MUTATED");
        Assert.Throws<NotSupportedException>(() => ((IList<string>)plan.ProfileSnapshot.MissingValueRules.Markers)[0] = "MUTATED");
        Assert.Throws<NotSupportedException>(() => ((IList<FtPlannedSourceFile>)plan.OrderedFiles)[0] = plan.OrderedFiles[0] with { LotId = "X" });
        Assert.Throws<NotSupportedException>(() => ((IList<FtPlannedLot>)plan.Lots)[0] = plan.Lots[0] with { LotId = "X" });
    }

    [Fact]
    public void IncompleteLogicalIdentifiersCollideWhenSelectedAndResolveOnDeselection()
    {
        // Profile without FilePart -> FilePart == null for both files.
        var baseProfile = Profile();
        var noPartProfile = baseProfile with
        {
            FilenameRules = baseProfile.FilenameRules with { FilePart = null }
        };

        var fileA = TouchFile(Path.Combine("dirA", OsatName("LOT-INC", "F1", null, 1, stamp: "20261124000001")));
        var fileB = TouchFile(Path.Combine("dirB", OsatName("LOT-INC", "F1", null, 2, stamp: "20261124000002")));

        var discovery = _discovery.DiscoverFiles(noPartProfile, [fileA, fileB], Token);
        Assert.Single(discovery.Collisions);
        Assert.All(discovery.Files, f => Assert.True(f.HasLogicalCollision));

        var model = new FtImportSelectionModel(discovery);
        var blocked = model.BuildImportPlan();
        Assert.False(blocked.IsValid);
        Assert.Contains(blocked.Errors, e => e.Code == FtImportDiagnosticCode.LogicalFileCollision);

        // Deselecting one file resolves the collision even though discovery warning remains on Discovery.Diagnostics.
        model.SetFileSelected(discovery.Files[1].Id, false);
        var resolved = model.BuildImportPlan();
        Assert.True(resolved.IsValid);
        Assert.Single(resolved.Plan!.OrderedFiles);
    }
}
