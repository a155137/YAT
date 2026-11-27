using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

// Application-level selection model and Import Plan builder over an immutable FtFileDiscoveryResult.
// Deselecting files never removes them from Discovery; BuildImportPlan always produces an independent,
// immutable snapshot unaffected by later selection changes.
public sealed class FtImportSelectionModel
{
    private readonly Dictionary<Guid, FtDiscoveredFile> _filesById;
    private readonly HashSet<Guid> _selectedFileIds;
    private readonly ImportProfileValidator _validator;

    public FtImportSelectionModel(
        FtFileDiscoveryResult discovery,
        ImportProfileValidator? validator = null)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        Discovery = discovery;
        _validator = validator ?? new ImportProfileValidator();

        _filesById = discovery.Files.ToDictionary(f => f.Id);
        // Confirmed default: all successfully identified files are selected by default.
        _selectedFileIds = discovery.Files
            .Where(f => f.IsIdentified)
            .Select(f => f.Id)
            .ToHashSet();
    }

    public FtFileDiscoveryResult Discovery { get; }

    public int SelectedFileCount => _selectedFileIds.Count;

    public bool IsFileSelected(Guid fileId) => _selectedFileIds.Contains(fileId);

    public void SetFileSelected(Guid fileId, bool isSelected)
    {
        if (!_filesById.ContainsKey(fileId))
        {
            throw new ArgumentException($"File id '{fileId}' does not belong to this discovery result.", nameof(fileId));
        }

        if (isSelected)
        {
            _selectedFileIds.Add(fileId);
        }
        else
        {
            _selectedFileIds.Remove(fileId);
        }
    }

    public void SetLotSelected(string lotId, bool isSelected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lotId);
        var lot = Discovery.LotGroups.FirstOrDefault(l => string.Equals(l.LotId, lotId, StringComparison.Ordinal));
        if (lot is null)
        {
            return;
        }

        foreach (var file in lot.GetAllFiles())
        {
            if (!file.IsIdentified)
            {
                continue;
            }

            if (isSelected)
            {
                _selectedFileIds.Add(file.Id);
            }
            else
            {
                _selectedFileIds.Remove(file.Id);
            }
        }
    }

    public void SetStageSelected(string lotId, string testStage, bool isSelected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lotId);
        ArgumentException.ThrowIfNullOrWhiteSpace(testStage);

        var lot = Discovery.LotGroups.FirstOrDefault(l => string.Equals(l.LotId, lotId, StringComparison.Ordinal));
        var stage = lot?.Stages.FirstOrDefault(s => string.Equals(s.TestStage, testStage, StringComparison.Ordinal));
        if (stage is null)
        {
            return;
        }

        foreach (var file in stage.GetAllFiles())
        {
            if (!file.IsIdentified)
            {
                continue;
            }

            if (isSelected)
            {
                _selectedFileIds.Add(file.Id);
            }
            else
            {
                _selectedFileIds.Remove(file.Id);
            }
        }
    }

    public void SelectOnlyStage(string testStage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testStage);
        _selectedFileIds.Clear();
        foreach (var file in Discovery.Files)
        {
            if (file.IsIdentified && string.Equals(file.Match!.TestStage, testStage, StringComparison.Ordinal))
            {
                _selectedFileIds.Add(file.Id);
            }
        }
    }

    public void SelectAll()
    {
        _selectedFileIds.Clear();
        foreach (var file in Discovery.Files)
        {
            if (file.IsIdentified)
            {
                _selectedFileIds.Add(file.Id);
            }
        }
    }

    public void ClearSelection() => _selectedFileIds.Clear();

    public FtSelectionState GetLotSelectionState(string lotId)
    {
        var lot = Discovery.LotGroups.FirstOrDefault(l => string.Equals(l.LotId, lotId, StringComparison.Ordinal));
        if (lot is null)
        {
            return FtSelectionState.Unselected;
        }

        return ComputeState(lot.GetAllFiles());
    }

    public FtSelectionState GetStageSelectionState(string lotId, string testStage)
    {
        var lot = Discovery.LotGroups.FirstOrDefault(l => string.Equals(l.LotId, lotId, StringComparison.Ordinal));
        var stage = lot?.Stages.FirstOrDefault(s => string.Equals(s.TestStage, testStage, StringComparison.Ordinal));
        if (stage is null)
        {
            return FtSelectionState.Unselected;
        }

        return ComputeState(stage.GetAllFiles());
    }

    public IReadOnlyList<FtDiscoveredFile> GetSelectedFiles() =>
        Discovery.Files.Where(f => _selectedFileIds.Contains(f.Id)).ToArray();

    public FtImportPlanResult BuildImportPlan()
    {
        var errors = new List<FtImportDiagnostic>();
        var warnings = new List<FtImportDiagnostic>();

        var profileSnapshot = FtImportProfileCloner.Clone(Discovery.ProfileSnapshot);
        var profileValidation = _validator.Validate(profileSnapshot);
        if (!profileValidation.IsValid)
        {
            foreach (var err in profileValidation.Errors)
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidProfile,
                    $"{err.Field}: {err.Message}"));
            }
        }

        var selectedFiles = Discovery.Files.Where(f => _selectedFileIds.Contains(f.Id)).ToList();
        if (selectedFiles.Count == 0)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.EmptySelection,
                "At least one identified datalog file must be selected to create an import plan."));
            return BuildFailureResult(errors, warnings);
        }

        // Reject any selected file that failed filename identification.
        foreach (var unresolved in selectedFiles.Where(f => !f.IsIdentified || f.Match is null))
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.UnresolvedSelectedFile,
                $"Selected file '{unresolved.FileName}' could not be identified: {unresolved.IdentificationDiagnostic?.Message ?? "Filename rule match failed."}",
                SourcePath: unresolved.SourcePath));
        }

        var identifiedSelected = selectedFiles.Where(f => f.IsIdentified && f.Match is not null).ToList();

        // Reject unresolved logical file collisions within the selected subset.
        foreach (var group in identifiedSelected
            .GroupBy(f => f.LogicalKey!)
            .Where(g => g.Count() > 1)
            .OrderBy(g => g.Key.LotId, StringComparer.Ordinal)
            .ThenBy(g => g.Key.TestStage, StringComparer.Ordinal)
            .ThenBy(g => g.Key.TestRole)
            .ThenBy(g => g.Key.RetestRound ?? int.MaxValue)
            .ThenBy(g => g.Key.FilePart ?? int.MaxValue))
        {
            var key = group.Key;
            var collidingNames = string.Join(", ", group.Select(f => f.FileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.LogicalFileCollision,
                $"Selected files contain an unresolved logical collision for Lot '{key.LotId}', Stage '{key.TestStage}' ({collidingNames}). Deselect duplicate logical file(s) to resolve.",
                LotId: key.LotId,
                TestStage: key.TestStage));
        }

        // Enforce single Test Stage restriction across selected files.
        var selectedStages = identifiedSelected
            .Select(f => f.Match!.TestStage)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        if (selectedStages.Length > 1)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.MultipleTestStages,
                $"Selected files span multiple Test Stages ({string.Join(", ", selectedStages)}). Select files from a single Test Stage per import."));
        }

        if (errors.Count > 0)
        {
            return BuildFailureResult(errors, warnings);
        }

        var targetStage = selectedStages[0];
        var plannedLots = new List<FtPlannedLot>();
        var allOrderedFiles = new List<FtPlannedSourceFile>(identifiedSelected.Count);

        foreach (var lotGroup in Discovery.LotGroups)
        {
            var stageGroup = lotGroup.Stages.FirstOrDefault(s => string.Equals(s.TestStage, targetStage, StringComparison.Ordinal));
            if (stageGroup is null)
            {
                continue;
            }

            var selectedOriginals = stageGroup.OriginalFiles
                .Where(f => _selectedFileIds.Contains(f.Id))
                .Select(ToPlannedFile)
                .ToArray();

            var plannedRetestRounds = new List<FtPlannedRetestRound>();
            var selectedRetestRoundNumbers = new List<int>();
            var hasAnySelectedRetest = false;
            var hasSelectedUnnumberedRetest = false;

            foreach (var roundGroup in stageGroup.RetestRounds)
            {
                var selectedInRound = roundGroup.Files
                    .Where(f => _selectedFileIds.Contains(f.Id))
                    .Select(ToPlannedFile)
                    .ToArray();

                if (selectedInRound.Length == 0)
                {
                    continue;
                }

                hasAnySelectedRetest = true;
                if (roundGroup.RetestRound is { } roundNum)
                {
                    selectedRetestRoundNumbers.Add(roundNum);
                }
                else
                {
                    hasSelectedUnnumberedRetest = true;
                }

                var parts = selectedInRound
                    .Where(f => f.FilePart.HasValue)
                    .Select(f => f.FilePart!.Value)
                    .Distinct()
                    .OrderBy(p => p)
                    .ToArray();

                plannedRetestRounds.Add(new FtPlannedRetestRound(
                    roundGroup.RetestRound,
                    Array.AsReadOnly(selectedInRound),
                    Array.AsReadOnly(parts)));

                // Partial round diagnostic (non-blocking).
                if (selectedInRound.Length < roundGroup.Files.Count)
                {
                    var roundLabel = roundGroup.RetestRound is { } r ? $"Retest Round {r}" : "Retest";
                    warnings.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Warning,
                        FtImportDiagnosticCode.PartialRoundSelection,
                        $"Lot '{lotGroup.LotId}' ({targetStage}) {roundLabel}: selected {selectedInRound.Length} of {roundGroup.Files.Count} discovered files.",
                        LotId: lotGroup.LotId,
                        TestStage: targetStage));
                }
            }

            if (selectedOriginals.Length == 0 && !hasAnySelectedRetest)
            {
                continue;
            }

            // Case D: Mixed numbered and unnumbered Retest files in the same Lot & Stage is ambiguous.
            if (selectedRetestRoundNumbers.Count > 0 && hasSelectedUnnumberedRetest)
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.AmbiguousMixedRetestRounds,
                    $"Lot '{lotGroup.LotId}' ({targetStage}) mixes numbered Retest Round(s) and unnumbered Retest file(s) in the current selection. Exclude either the numbered or unnumbered Retest file(s) so the latest Retest group is unambiguous.",
                    LotId: lotGroup.LotId,
                    TestStage: targetStage));
                continue;
            }

            selectedRetestRoundNumbers.Sort();
            int? latestSelectedRound = selectedRetestRoundNumbers.Count > 0
                ? selectedRetestRoundNumbers[^1]
                : null;
            var latestRetestKind = !hasAnySelectedRetest
                ? FtLatestRetestKind.None
                : hasSelectedUnnumberedRetest
                    ? FtLatestRetestKind.UnnumberedGroup
                    : FtLatestRetestKind.NumberedRound;

            // Partial selection diagnostics for this Lot & Stage (informational/warning only; never invalidate).
            var totalDiscoveredInStage = stageGroup.OriginalFiles.Count + stageGroup.RetestRounds.Sum(r => r.Files.Count);
            var totalSelectedInStage = selectedOriginals.Length + plannedRetestRounds.Sum(r => r.Files.Count);

            if (totalSelectedInStage < totalDiscoveredInStage)
            {
                warnings.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Warning,
                    FtImportDiagnosticCode.PartialLotSelection,
                    $"Lot '{lotGroup.LotId}' ({targetStage}) is partially selected ({totalSelectedInStage} of {totalDiscoveredInStage} discovered files).",
                    LotId: lotGroup.LotId,
                    TestStage: targetStage));
            }

            if (stageGroup.OriginalFiles.Count > 0 && selectedOriginals.Length > 0 && selectedOriginals.Length < stageGroup.OriginalFiles.Count)
            {
                warnings.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Warning,
                    FtImportDiagnosticCode.PartialRoundSelection,
                    $"Lot '{lotGroup.LotId}' ({targetStage}) Original: selected {selectedOriginals.Length} of {stageGroup.OriginalFiles.Count} discovered files.",
                    LotId: lotGroup.LotId,
                    TestStage: targetStage));
            }

            if (stageGroup.OriginalFiles.Count > 0 && selectedOriginals.Length == 0)
            {
                warnings.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Warning,
                    FtImportDiagnosticCode.OriginalFilesExcluded,
                    $"Lot '{lotGroup.LotId}' ({targetStage}) has {stageGroup.OriginalFiles.Count} discovered Original file(s) excluded from selection.",
                    LotId: lotGroup.LotId,
                    TestStage: targetStage));
            }

            if (hasAnySelectedRetest && selectedOriginals.Length == 0)
            {
                warnings.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Info,
                    FtImportDiagnosticCode.RetestOnlySelection,
                    $"Lot '{lotGroup.LotId}' ({targetStage}) has Retest file(s) selected without any Original files.",
                    LotId: lotGroup.LotId,
                    TestStage: targetStage));
            }

            var highestDiscoveredRound = stageGroup.RetestRounds
                .Where(r => r.RetestRound.HasValue)
                .Select(r => r.RetestRound!.Value)
                .DefaultIfEmpty()
                .Max();
            var hasDiscoveredNumberedRetest = stageGroup.RetestRounds.Any(r => r.RetestRound.HasValue);

            if (hasDiscoveredNumberedRetest && (!latestSelectedRound.HasValue || highestDiscoveredRound > latestSelectedRound.Value))
            {
                warnings.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Warning,
                    FtImportDiagnosticCode.HigherRetestRoundsExcluded,
                    latestSelectedRound.HasValue
                        ? $"Lot '{lotGroup.LotId}' ({targetStage}) excluded higher discovered Retest Round(s) above R{latestSelectedRound.Value} (highest discovered: R{highestDiscoveredRound})."
                        : $"Lot '{lotGroup.LotId}' ({targetStage}) excluded discovered Retest Round(s) (highest discovered: R{highestDiscoveredRound}).",
                    LotId: lotGroup.LotId,
                    TestStage: targetStage));
            }

            var lotOrderedFiles = new List<FtPlannedSourceFile>(totalSelectedInStage);
            lotOrderedFiles.AddRange(selectedOriginals);
            foreach (var round in plannedRetestRounds)
            {
                lotOrderedFiles.AddRange(round.Files);
            }

            allOrderedFiles.AddRange(lotOrderedFiles);

            plannedLots.Add(new FtPlannedLot(
                LotId: lotGroup.LotId,
                TestStage: targetStage,
                HasOriginalFiles: selectedOriginals.Length > 0,
                HasRetestFiles: hasAnySelectedRetest,
                LatestRetestKind: latestRetestKind,
                SelectedRetestRounds: Array.AsReadOnly(selectedRetestRoundNumbers.ToArray()),
                LatestSelectedRetestRound: latestSelectedRound,
                OriginalFiles: Array.AsReadOnly(selectedOriginals),
                RetestRounds: Array.AsReadOnly(plannedRetestRounds.ToArray()),
                OrderedFiles: Array.AsReadOnly(lotOrderedFiles.ToArray())));
        }

        if (errors.Count > 0)
        {
            return BuildFailureResult(errors, warnings);
        }

        var planDiagnostics = Array.AsReadOnly(warnings.ToArray());
        var plan = new FtImportPlan(
            ProfileSnapshot: profileSnapshot,
            TestStage: targetStage,
            Lots: Array.AsReadOnly(plannedLots.ToArray()),
            OrderedFiles: Array.AsReadOnly(allOrderedFiles.ToArray()),
            Diagnostics: planDiagnostics);

        return new FtImportPlanResult(
            IsValid: true,
            Plan: plan,
            Errors: [],
            Warnings: planDiagnostics,
            AllDiagnostics: planDiagnostics);
    }

    private FtSelectionState ComputeState(IReadOnlyList<FtDiscoveredFile> files)
    {
        var eligible = 0;
        var selected = 0;
        foreach (var f in files)
        {
            if (!f.IsIdentified)
            {
                continue;
            }

            eligible++;
            if (_selectedFileIds.Contains(f.Id))
            {
                selected++;
            }
        }

        if (eligible == 0 || selected == 0)
        {
            return FtSelectionState.Unselected;
        }

        return selected == eligible ? FtSelectionState.Selected : FtSelectionState.PartiallySelected;
    }

    private static FtPlannedSourceFile ToPlannedFile(FtDiscoveredFile file) => new(
        FileId: file.Id,
        SourcePath: file.SourcePath,
        FileName: file.FileName,
        LotId: file.Match!.LotId,
        TestStage: file.Match.TestStage,
        TestRole: file.Match.TestRole,
        RetestRound: file.Match.RetestRound,
        FilePart: file.Match.FilePart);

    private static FtImportPlanResult BuildFailureResult(
        List<FtImportDiagnostic> errors,
        List<FtImportDiagnostic> warnings)
    {
        var errArr = Array.AsReadOnly(errors.ToArray());
        var warnArr = Array.AsReadOnly(warnings.ToArray());
        var all = new List<FtImportDiagnostic>(errors.Count + warnings.Count);
        all.AddRange(errors);
        all.AddRange(warnings);
        return new FtImportPlanResult(
            IsValid: false,
            Plan: null,
            Errors: errArr,
            Warnings: warnArr,
            AllDiagnostics: Array.AsReadOnly(all.ToArray()));
    }
}
