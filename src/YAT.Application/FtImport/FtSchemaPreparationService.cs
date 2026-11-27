using YAT.Application.Abstractions.Persistence;
using YAT.Application.ImportProfiles;
using YAT.Domain.Enums;

namespace YAT.Application.FtImport;

// Orchestrates FT-03.2 multi-file structure scanning, Multi-Lot Union Schema construction,
// per-file measurement column mapping, and FT/RT Bin retention policy resolution.
// Reuses DatalogStreamOpener, DatalogStructureScanner, and DatalogRowParser without creating a second parser.
public sealed class FtSchemaPreparationService
{
    private readonly DatalogStreamOpener _opener;
    private readonly DatalogStructureScanner _scanner;
    private readonly ImportProfileValidator _validator;

    public FtSchemaPreparationService(
        DatalogStreamOpener? opener = null,
        DatalogStructureScanner? scanner = null,
        ImportProfileValidator? validator = null)
    {
        _opener = opener ?? new DatalogStreamOpener();
        _scanner = scanner ?? new DatalogStructureScanner();
        _validator = validator ?? new ImportProfileValidator();
    }

    public FtSchemaScanResult ScanUnionSchema(
        FtImportPlan importPlan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(importPlan);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<FtImportDiagnostic>();
        var warnings = new List<FtImportDiagnostic>();

        var planSnapshot = CloneImportPlan(importPlan);
        var profileSnapshot = planSnapshot.ProfileSnapshot;

        ValidateProfile(profileSnapshot, errors);
        ValidateImportPlanInvariants(planSnapshot, errors);

        if (planSnapshot.Diagnostics is not null)
        {
            foreach (var diag in planSnapshot.Diagnostics)
            {
                if (diag.Severity == FtImportDiagnosticSeverity.Error)
                {
                    errors.Add(diag);
                }
                else
                {
                    warnings.Add(diag);
                }
            }
        }

        if (errors.Count > 0)
        {
            return BuildScanFailure(errors, warnings);
        }

        var scannedFiles = new List<FtScannedSourceFile>(planSnapshot.OrderedFiles.Count);
        var unionHeaders = new List<string>();
        var unionHeaderSet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in planSnapshot.OrderedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stampBefore = TryCaptureFileStamp(file.SourcePath);
            try
            {
                DatalogStructureScanResult scanResult;
                string? archiveEntryName;
                using (var opened = _opener.Open(file.SourcePath, cancellationToken))
                {
                    archiveEntryName = opened.ArchiveEntryName;
                    scanResult = _scanner.Scan(profileSnapshot, opened.Reader, cancellationToken);
                }

                var stampAfter = TryCaptureFileStamp(file.SourcePath);
                if (stampBefore is null || stampAfter is null || stampBefore != stampAfter)
                {
                    errors.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Error,
                        FtImportDiagnosticCode.SourceFileChangedDuringScan,
                        $"Source '{file.FileName}' changed or became inaccessible while scanning its structure.",
                        SourcePath: file.SourcePath,
                        LotId: file.LotId,
                        TestStage: file.TestStage));
                    continue;
                }

                if (!scanResult.IsSuccess)
                {
                    foreach (var diag in scanResult.Diagnostics)
                    {
                        var code = diag.Code == DatalogScanErrorCode.DuplicateHeader
                            ? FtImportDiagnosticCode.DuplicateMeasurementHeader
                            : FtImportDiagnosticCode.StructureScanFailed;
                        var rowContext = diag.SourceRowNumber is { } row ? $" (row {row})" : string.Empty;
                        errors.Add(new FtImportDiagnostic(
                            FtImportDiagnosticSeverity.Error,
                            code,
                            $"Source '{file.FileName}'{rowContext}: {diag.Message}",
                            SourcePath: file.SourcePath,
                            LotId: file.LotId,
                            TestStage: file.TestStage));
                    }

                    continue;
                }

                var frozenStructure = FreezeStructure(scanResult);
                var availableHeaders = new string[frozenStructure.AvailableTestHeaders.Count];
                for (var i = 0; i < frozenStructure.AvailableTestHeaders.Count; i++)
                {
                    var header = frozenStructure.AvailableTestHeaders[i].Name;
                    availableHeaders[i] = header;
                    if (unionHeaderSet.Add(header))
                    {
                        unionHeaders.Add(header);
                    }
                }

                scannedFiles.Add(new FtScannedSourceFile(
                    SourceFile: file,
                    Structure: frozenStructure,
                    ArchiveEntryName: archiveEntryName,
                    FileStamp: stampAfter,
                    AvailableMeasurementHeaders: Array.AsReadOnly(availableHeaders)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DatalogOpenException ex)
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.SourceOpenFailed,
                    $"Source '{file.FileName}': {ex.Message}",
                    SourcePath: file.SourcePath,
                    LotId: file.LotId,
                    TestStage: file.TestStage));
            }
        }

        if (errors.Count > 0)
        {
            return BuildScanFailure(errors, warnings);
        }

        if (unionHeaders.Count == 0)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.NoAvailableMeasurementHeaders,
                "None of the selected datalog files contain any available measurement headers."));
            return BuildScanFailure(errors, warnings);
        }

        var warningSnapshot = Array.AsReadOnly(warnings.ToArray());
        var unionSchema = new FtUnionSchema(
            ImportPlan: planSnapshot,
            ProfileSnapshot: profileSnapshot,
            ScannedFiles: Array.AsReadOnly(scannedFiles.ToArray()),
            UnionMeasurementHeaders: Array.AsReadOnly(unionHeaders.ToArray()),
            Diagnostics: warningSnapshot);

        return new FtSchemaScanResult(
            IsSuccess: true,
            UnionSchema: unionSchema,
            Errors: Array.AsReadOnly(Array.Empty<FtImportDiagnostic>()),
            Warnings: warningSnapshot,
            AllDiagnostics: warningSnapshot);
    }

    public FtPreparedImportPlanResult PrepareImportPlan(
        FtTestItemSelectionModel selectionModel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectionModel);
        return PrepareImportPlan(selectionModel.UnionSchema, selectionModel.GetSelectedHeaders(), cancellationToken);
    }

    public FtPreparedImportPlanResult PrepareImportPlan(
        FtUnionSchema unionSchema,
        IReadOnlyList<string> selectedMeasurementHeaders,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unionSchema);
        ArgumentNullException.ThrowIfNull(selectedMeasurementHeaders);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<FtImportDiagnostic>();
        var warnings = new List<FtImportDiagnostic>();

        var planSnapshot = CloneImportPlan(unionSchema.ImportPlan);
        var profileSnapshot = FtImportProfileCloner.Clone(unionSchema.ProfileSnapshot);

        ValidateProfile(profileSnapshot, errors);
        ValidateImportPlanInvariants(planSnapshot, errors);

        if (unionSchema.Diagnostics is not null)
        {
            foreach (var diag in unionSchema.Diagnostics)
            {
                if (diag.Severity == FtImportDiagnosticSeverity.Error)
                {
                    errors.Add(diag);
                }
                else
                {
                    warnings.Add(diag);
                }
            }
        }

        var unionHeadersArray = unionSchema.UnionMeasurementHeaders.ToArray();
        var unionHeaderSet = new HashSet<string>(unionHeadersArray, StringComparer.Ordinal);

        if (selectedMeasurementHeaders.Count == 0)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.EmptyMeasurementSelection,
                "At least one measurement header must be selected to prepare an FT import plan."));
        }

        var selectedSet = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < selectedMeasurementHeaders.Count; i++)
        {
            var header = selectedMeasurementHeaders[i];
            if (header is null || !unionHeaderSet.Contains(header))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.UnknownSelectedMeasurement,
                    $"Selected measurement header '{header ?? "<null>"}' does not exist in the Union Schema."));
                continue;
            }

            if (!selectedSet.Add(header))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.DuplicateSelectedMeasurement,
                    $"Selected measurement header '{header}' was specified more than once."));
                continue;
            }

            if (FtOutputSchemaColumns.IsReservedColumnName(header))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.ReservedColumnNameConflict,
                    $"Selected measurement header '{header}' conflicts with a reserved output metadata column name ({string.Join(", ", FtOutputSchemaColumns.MetadataColumnNames)})."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(header))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.IncompatibleWorksheetColumnName,
                    $"Selected measurement header at index {i} is empty or whitespace-only, which is not permitted as a Worksheet column name."));
                continue;
            }

            if (!string.Equals(header, header.Trim(), StringComparison.Ordinal))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.IncompatibleWorksheetColumnName,
                    $"Selected measurement header '{header}' contains leading or trailing whitespace, which is incompatible with Worksheet column naming because Worksheet persistence trims column names."));
            }
        }

        if (errors.Count > 0)
        {
            return BuildPrepareFailure(errors, warnings);
        }

        // Preserve deterministic union first-seen order in the final selected measurement schema.
        var orderedSelected = new string[selectedSet.Count];
        var selIdx = 0;
        foreach (var header in unionHeadersArray)
        {
            if (selectedSet.Contains(header))
            {
                orderedSelected[selIdx++] = header;
            }
        }

        var orderedSelectedReadOnly = Array.AsReadOnly(orderedSelected);

        // Build final ordered Worksheet column definitions: Lot_ID, Bin, Site, Test_Role, followed by Numeric measurements.
        var outputColumns = new WorksheetColumnDefinition[FtOutputSchemaColumns.MetadataColumns.Count + orderedSelected.Length];
        for (var i = 0; i < FtOutputSchemaColumns.MetadataColumns.Count; i++)
        {
            outputColumns[i] = FtOutputSchemaColumns.MetadataColumns[i];
        }

        for (var i = 0; i < orderedSelected.Length; i++)
        {
            outputColumns[FtOutputSchemaColumns.MetadataColumns.Count + i] = new WorksheetColumnDefinition(
                Name: orderedSelected[i],
                DataType: WorksheetDataType.Numeric,
                SemanticType: ColumnSemanticType.TestParameter);
        }

        // Build shared immutable Bin retention policies once for the entire prepared plan.
        var retainAllPolicy = FtSourceBinRetentionPolicy.CreateRetainAll();
        var filteredPolicy = FtSourceBinRetentionPolicy.CreateFiltered(profileSnapshot.BinRules);

        var lotsByKey = new Dictionary<(string LotId, string TestStage), FtPlannedLot>(planSnapshot.Lots.Count);
        foreach (var lot in planSnapshot.Lots)
        {
            lotsByKey[(lot.LotId, lot.TestStage)] = lot;
        }

        var preparedSources = new List<FtPreparedSourceFile>(unionSchema.ScannedFiles.Count);
        foreach (var scanned in unionSchema.ScannedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frozenStructure = FreezeStructure(scanned.Structure);
            if (!DatalogRowParser.TryResolveSelectedColumns(
                frozenStructure,
                orderedSelectedReadOnly,
                scanned.SourceFile.FileName,
                out var rawParserMapping,
                out var parseError))
            {
                var isRoleConflict = parseError?.ColumnHeader is not null
                    && frozenStructure.AllHeaders.Contains(parseError.ColumnHeader, StringComparer.Ordinal);
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    isRoleConflict ? FtImportDiagnosticCode.MeasurementColumnRoleConflict : FtImportDiagnosticCode.StructureScanFailed,
                    $"Source '{scanned.SourceFile.FileName}': {parseError?.Message ?? "Failed to resolve selected measurement columns."}",
                    SourcePath: scanned.SourceFile.SourcePath,
                    LotId: scanned.SourceFile.LotId,
                    TestStage: scanned.SourceFile.TestStage));
                continue;
            }

            var mapping = BuildFrozenSourceMapping(rawParserMapping!);
            if (!lotsByKey.TryGetValue((scanned.SourceFile.LotId, scanned.SourceFile.TestStage), out var lot))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Source '{scanned.SourceFile.FileName}' references Lot '{scanned.SourceFile.LotId}' ({scanned.SourceFile.TestStage}) which is not present in FtImportPlan.Lots.",
                    SourcePath: scanned.SourceFile.SourcePath,
                    LotId: scanned.SourceFile.LotId,
                    TestStage: scanned.SourceFile.TestStage));
                continue;
            }

            FtSourceBinRetentionPolicy retentionPolicy;
            try
            {
                retentionPolicy = FtBinRetentionPolicyResolver.ResolveForFile(
                    lot,
                    scanned.SourceFile,
                    retainAllPolicy,
                    filteredPolicy);
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.AmbiguousMixedRetestRounds,
                    ex.Message,
                    SourcePath: scanned.SourceFile.SourcePath,
                    LotId: scanned.SourceFile.LotId,
                    TestStage: scanned.SourceFile.TestStage));
                continue;
            }

            preparedSources.Add(new FtPreparedSourceFile(
                SourceFile: scanned.SourceFile with { },
                Structure: frozenStructure,
                ArchiveEntryName: scanned.ArchiveEntryName,
                FileStamp: scanned.FileStamp is null ? null : scanned.FileStamp with { },
                MeasurementMapping: mapping,
                BinRetentionPolicy: retentionPolicy));
        }

        if (errors.Count > 0)
        {
            return BuildPrepareFailure(errors, warnings);
        }

        var warningSnapshot = Array.AsReadOnly(warnings.ToArray());
        var preparedPlan = new FtPreparedImportPlan(
            ImportPlan: planSnapshot,
            ProfileSnapshot: profileSnapshot,
            UnionMeasurementHeaders: Array.AsReadOnly(unionHeadersArray),
            SelectedMeasurementHeaders: orderedSelectedReadOnly,
            OutputColumns: Array.AsReadOnly(outputColumns),
            Sources: Array.AsReadOnly(preparedSources.ToArray()),
            Diagnostics: warningSnapshot);

        return new FtPreparedImportPlanResult(
            IsValid: true,
            Plan: preparedPlan,
            Errors: Array.AsReadOnly(Array.Empty<FtImportDiagnostic>()),
            Warnings: warningSnapshot,
            AllDiagnostics: warningSnapshot);
    }

    private void ValidateProfile(ImportProfile profile, List<FtImportDiagnostic> errors)
    {
        var validation = _validator.Validate(profile);
        if (!validation.IsValid)
        {
            foreach (var err in validation.Errors)
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidProfile,
                    $"{err.Field}: {err.Message}"));
            }
        }
    }

    private static void ValidateImportPlanInvariants(FtImportPlan plan, List<FtImportDiagnostic> errors)
    {
        if (plan.OrderedFiles.Count == 0 || plan.Lots.Count == 0)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.InvalidImportPlan,
                "FtImportPlan must contain at least one selected Lot and one selected source file."));
            return;
        }

        var lotKeys = new HashSet<(string LotId, string TestStage)>();
        foreach (var lot in plan.Lots)
        {
            lotKeys.Add((lot.LotId, lot.TestStage));

            var hasNumberedRetest = lot.OrderedFiles.Any(f => f.TestRole == FtTestRole.Retest && f.RetestRound.HasValue);
            var hasUnnumberedRetest = lot.OrderedFiles.Any(f => f.TestRole == FtTestRole.Retest && !f.RetestRound.HasValue);

            if (hasNumberedRetest && hasUnnumberedRetest)
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.AmbiguousMixedRetestRounds,
                    $"Lot '{lot.LotId}' ({lot.TestStage}) mixes numbered Retest Round(s) and unnumbered Retest file(s).",
                    LotId: lot.LotId,
                    TestStage: lot.TestStage));
                continue;
            }

            if (lot.LatestRetestKind == FtLatestRetestKind.None && (hasNumberedRetest || hasUnnumberedRetest))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Lot '{lot.LotId}' ({lot.TestStage}) declares LatestRetestKind=None but contains Retest file(s).",
                    LotId: lot.LotId,
                    TestStage: lot.TestStage));
            }
            else if (lot.LatestRetestKind == FtLatestRetestKind.NumberedRound && (!lot.LatestSelectedRetestRound.HasValue || hasUnnumberedRetest || !hasNumberedRetest))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Lot '{lot.LotId}' ({lot.TestStage}) declares LatestRetestKind=NumberedRound with inconsistent Retest metadata.",
                    LotId: lot.LotId,
                    TestStage: lot.TestStage));
            }
            else if (lot.LatestRetestKind == FtLatestRetestKind.UnnumberedGroup && (lot.LatestSelectedRetestRound.HasValue || hasNumberedRetest || !hasUnnumberedRetest))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Lot '{lot.LotId}' ({lot.TestStage}) declares LatestRetestKind=UnnumberedGroup with inconsistent Retest metadata.",
                    LotId: lot.LotId,
                    TestStage: lot.TestStage));
            }
        }

        foreach (var file in plan.OrderedFiles)
        {
            if (!lotKeys.Contains((file.LotId, file.TestStage)))
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Selected source file '{file.FileName}' references Lot '{file.LotId}' ({file.TestStage}) which is missing from FtImportPlan.Lots.",
                    SourcePath: file.SourcePath,
                    LotId: file.LotId,
                    TestStage: file.TestStage));
            }
        }
    }

    private static FtSourceFileStamp? TryCaptureFileStamp(string sourcePath)
    {
        try
        {
            var info = new FileInfo(sourcePath);
            if (!info.Exists)
            {
                return null;
            }

            return new FtSourceFileStamp(info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static FtSourceMeasurementMapping BuildFrozenSourceMapping(SelectedMeasurementMapping parserMapping)
    {
        var count = parserMapping.Columns.Count;
        var columns = new FtSourceMeasurementColumnMapping[count];
        var frozenParserColumns = new ResolvedMeasurementColumn[count];
        var presentIndices = new List<int>(count);
        var absentIndices = new List<int>();
        var absentHeaders = new List<string>();
        var metadataOffset = FtOutputSchemaColumns.MetadataColumns.Count;

        for (var i = 0; i < count; i++)
        {
            var srcCol = parserMapping.Columns[i];
            frozenParserColumns[i] = srcCol with { };
            var presence = srcCol.IsPresentInSource
                ? FtSourceMeasurementColumnPresence.PresentInSource
                : FtSourceMeasurementColumnPresence.AbsentFromSource;

            columns[i] = new FtSourceMeasurementColumnMapping(
                SelectedMeasurementIndex: i,
                OutputWorksheetColumnIndex: metadataOffset + i,
                Header: srcCol.Header,
                Presence: presence,
                SourceColumnIndex: srcCol.SourceColumnIndex,
                ParserMeasurementIndex: i);

            if (srcCol.IsPresentInSource)
            {
                presentIndices.Add(i);
            }
            else
            {
                absentIndices.Add(i);
                absentHeaders.Add(srcCol.Header);
            }
        }

        var frozenAbsentHeaders = Array.AsReadOnly(absentHeaders.ToArray());
        var frozenParserMapping = new SelectedMeasurementMapping(
            Array.AsReadOnly(frozenParserColumns),
            frozenAbsentHeaders);

        return new FtSourceMeasurementMapping(
            Columns: Array.AsReadOnly(columns),
            PresentMeasurementIndices: Array.AsReadOnly(presentIndices.ToArray()),
            AbsentMeasurementIndices: Array.AsReadOnly(absentIndices.ToArray()),
            AbsentHeaders: frozenAbsentHeaders,
            ParserMapping: frozenParserMapping);
    }

    // Defensive deep freeze of DatalogStructureScanResult so callers cannot mutate underlying arrays or lists.
    private static DatalogStructureScanResult FreezeStructure(DatalogStructureScanResult structure)
    {
        var frozenComposites = new ResolvedCompositePlan[structure.CompositePlans.Count];
        for (var i = 0; i < structure.CompositePlans.Count; i++)
        {
            var plan = structure.CompositePlans[i];
            frozenComposites[i] = new ResolvedCompositePlan(
                plan.RawColumnIndex,
                plan.Separator,
                Array.AsReadOnly(plan.OutputNames.ToArray()));
        }

        return new DatalogStructureScanResult(
            IsSuccess: structure.IsSuccess,
            RowPositions: structure.RowPositions is null ? null : structure.RowPositions with { },
            RawHeaders: Array.AsReadOnly(structure.RawHeaders.ToArray()),
            AllHeaders: Array.AsReadOnly(structure.AllHeaders.ToArray()),
            CompositePlans: Array.AsReadOnly(frozenComposites),
            BinColumn: structure.BinColumn is null ? null : structure.BinColumn with { },
            SiteColumn: structure.SiteColumn is null ? null : structure.SiteColumn with { },
            HasConfiguredSiteColumn: structure.HasConfiguredSiteColumn,
            IgnoredColumns: Array.AsReadOnly(structure.IgnoredColumns.Select(c => c with { }).ToArray()),
            AvailableTestHeaders: Array.AsReadOnly(structure.AvailableTestHeaders.Select(c => c with { }).ToArray()),
            RowsScanned: structure.RowsScanned,
            Diagnostics: Array.AsReadOnly(structure.Diagnostics.Select(d => d with { }).ToArray()));
    }

    // Defensive deep clone of FtImportPlan so even manually constructed plans with mutable lists cannot mutate snapshots.
    private static FtImportPlan CloneImportPlan(FtImportPlan plan)
    {
        var clonedProfile = FtImportProfileCloner.Clone(plan.ProfileSnapshot);
        var clonedLots = plan.Lots
            .Select(lot => new FtPlannedLot(
                LotId: lot.LotId,
                TestStage: lot.TestStage,
                HasOriginalFiles: lot.HasOriginalFiles,
                HasRetestFiles: lot.HasRetestFiles,
                LatestRetestKind: lot.LatestRetestKind,
                SelectedRetestRounds: Array.AsReadOnly(lot.SelectedRetestRounds.ToArray()),
                LatestSelectedRetestRound: lot.LatestSelectedRetestRound,
                OriginalFiles: Array.AsReadOnly(lot.OriginalFiles.Select(f => f with { }).ToArray()),
                RetestRounds: Array.AsReadOnly(lot.RetestRounds
                    .Select(r => new FtPlannedRetestRound(
                        r.RetestRound,
                        Array.AsReadOnly(r.Files.Select(f => f with { }).ToArray()),
                        Array.AsReadOnly(r.FileParts.ToArray())))
                    .ToArray()),
                OrderedFiles: Array.AsReadOnly(lot.OrderedFiles.Select(f => f with { }).ToArray())))
            .ToArray();

        return new FtImportPlan(
            ProfileSnapshot: clonedProfile,
            TestStage: plan.TestStage,
            Lots: Array.AsReadOnly(clonedLots),
            OrderedFiles: Array.AsReadOnly(plan.OrderedFiles.Select(f => f with { }).ToArray()),
            Diagnostics: Array.AsReadOnly(plan.Diagnostics.Select(d => d with { }).ToArray()));
    }

    private static FtSchemaScanResult BuildScanFailure(
        List<FtImportDiagnostic> errors,
        List<FtImportDiagnostic> warnings)
    {
        var errArr = Array.AsReadOnly(errors.ToArray());
        var warnArr = Array.AsReadOnly(warnings.ToArray());
        var all = new List<FtImportDiagnostic>(errors.Count + warnings.Count);
        all.AddRange(errors);
        all.AddRange(warnings);
        return new FtSchemaScanResult(
            IsSuccess: false,
            UnionSchema: null,
            Errors: errArr,
            Warnings: warnArr,
            AllDiagnostics: Array.AsReadOnly(all.ToArray()));
    }

    private static FtPreparedImportPlanResult BuildPrepareFailure(
        List<FtImportDiagnostic> errors,
        List<FtImportDiagnostic> warnings)
    {
        var errArr = Array.AsReadOnly(errors.ToArray());
        var warnArr = Array.AsReadOnly(warnings.ToArray());
        var all = new List<FtImportDiagnostic>(errors.Count + warnings.Count);
        all.AddRange(errors);
        all.AddRange(warnings);
        return new FtPreparedImportPlanResult(
            IsValid: false,
            Plan: null,
            Errors: errArr,
            Warnings: warnArr,
            AllDiagnostics: Array.AsReadOnly(all.ToArray()));
    }
}
