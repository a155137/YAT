using System.Diagnostics;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Exceptions;
using YAT.Domain.Entities;

namespace YAT.Application.FtImport;

// FT-03.3 Streaming Import Engine.
// Streams all selected source files from an immutable FtPreparedImportPlan through DatalogRowParser
// into a single atomic Worksheet transaction via IAtomicWorksheetCreator / IRawDataRowWriter.
//
// Progress observer contract and limitations:
//   - Synchronous IProgress<FtImportProgress> observers: exceptions thrown during Report() are caught and isolated
//     (recorded as a single non-fatal Warning diagnostic with FtImportDiagnosticCode.ProgressObserverFailed)
//     so a faulty observer can neither abort an active transaction nor misreport a committed import as failed.
//   - Asynchronous Progress<FtImportProgress> (posting to a SynchronizationContext or ThreadPool): exceptions thrown
//     later on the captured target context occur asynchronously outside SafeReport and cannot be caught here.
//   - Reentrancy prohibition: observers MUST NOT synchronously re-enter project database or MainWindowSession
//     operations while callbacks run, as the session and database gates are held during streaming and commit.
public sealed class FtStreamingImportService
{
    private const long ProgressMinIntervalMs = 100;

    private readonly DatalogRowParser _parser;

    public FtStreamingImportService(DatalogRowParser? parser = null)
    {
        _parser = parser ?? new DatalogRowParser();
    }

    public Task<FtStreamingImportResult> ImportAsync(
        IAtomicWorksheetCreator worksheetCreator,
        Guid projectId,
        string worksheetName,
        FtPreparedImportPlan preparedPlan,
        IProgress<FtImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(worksheetCreator);
        return ImportAsync(
            (name, columns, writeRows, ct) =>
                worksheetCreator.CreateWorksheetAsync(
                    new AtomicWorksheetCreateRequest(projectId, name, columns),
                    writeRows,
                    ct),
            worksheetName,
            preparedPlan,
            progress,
            cancellationToken);
    }

    public async Task<FtStreamingImportResult> ImportAsync(
        Func<string, IReadOnlyList<WorksheetColumnDefinition>, Action<IRawDataRowWriter>, CancellationToken, Task<CreatedWorksheetResult>> createWorksheetAsync,
        string worksheetName,
        FtPreparedImportPlan preparedPlan,
        IProgress<FtImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(createWorksheetAsync);
        ArgumentNullException.ThrowIfNull(preparedPlan);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<FtImportDiagnostic>();
        var warnings = new List<FtImportDiagnostic>();
        var observerWarningRecorded = false;

        void SafeReport(FtImportProgress snapshot)
        {
            if (progress is null)
            {
                return;
            }

            try
            {
                progress.Report(snapshot);
            }
            catch (Exception ex)
            {
                if (!observerWarningRecorded)
                {
                    observerWarningRecorded = true;
                    warnings.Add(new FtImportDiagnostic(
                        FtImportDiagnosticSeverity.Warning,
                        FtImportDiagnosticCode.ProgressObserverFailed,
                        $"Progress observer threw {ex.GetType().Name}: {ex.Message}"));
                }
            }
        }

        // 1. Pre-transaction validation (no database transaction acquired if invalid or stale).
        if (string.IsNullOrWhiteSpace(worksheetName))
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.WorksheetCreationFailed,
                "Worksheet name must not be empty."));
        }

        if (preparedPlan.Sources is null || preparedPlan.Sources.Count == 0)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.InvalidImportPlan,
                "Prepared import plan must contain at least one source file."));
        }

        if (preparedPlan.OutputColumns is null
            || preparedPlan.OutputColumns.Count != FtOutputSchemaColumns.MetadataColumns.Count + preparedPlan.SelectedMeasurementHeaders.Count)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.InvalidImportPlan,
                "Prepared import plan output column definitions do not match the selected measurement schema."));
        }

        if (errors.Count > 0)
        {
            return BuildFailure(errors, warnings);
        }

        var sources = preparedPlan.Sources!;
        var totalFiles = sources.Count;
        SafeReport(new FtImportProgress(FtImportProgressPhase.Validating, 0, totalFiles, null, 0, 0, 0));

        // Pre-build validated per-source column execution plans ordered explicitly by OutputWorksheetColumnIndex.
        var executionPlans = new FtSourceMeasurementColumnMapping[totalFiles][];
        for (var i = 0; i < totalFiles; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = sources[i];

            if (!source.Structure.IsSuccess)
            {
                errors.Add(new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Source '{source.SourceFile.FileName}' does not have a valid resolved structure. Rescan is required.",
                    SourcePath: source.SourceFile.SourcePath,
                    LotId: source.SourceFile.LotId,
                    TestStage: source.SourceFile.TestStage));
                continue;
            }

            if (!TryValidateSourceStamp(source, "before import", out var stampDiag))
            {
                errors.Add(stampDiag!);
                continue;
            }

            if (!TryBuildOrderedColumnPlan(source, preparedPlan.SelectedMeasurementHeaders.Count, out var orderedCols, out var mapDiag))
            {
                errors.Add(mapDiag!);
                continue;
            }

            executionPlans[i] = orderedCols!;
        }

        if (errors.Count > 0)
        {
            return BuildFailure(errors, warnings);
        }

        // Preserve any non-error diagnostics from the prepared plan.
        if (preparedPlan.Diagnostics is not null)
        {
            foreach (var diag in preparedPlan.Diagnostics)
            {
                if (diag.Severity != FtImportDiagnosticSeverity.Error)
                {
                    warnings.Add(diag);
                }
            }
        }

        // 2. Single atomic Worksheet transaction across all selected source files.
        long totalProcessed = 0;
        long totalRetained = 0;
        long totalFiltered = 0;
        var sourceSummaries = new List<FtSourceImportSummary>(totalFiles);
        var lastProgressTick = Stopwatch.GetTimestamp();
        var minProgressTicks = Stopwatch.Frequency * ProgressMinIntervalMs / 1000;

        CreatedWorksheetResult committedResult;
        try
        {
            committedResult = await createWorksheetAsync(
                worksheetName,
                preparedPlan.OutputColumns!,
                writer =>
                {
                    for (var i = 0; i < totalFiles; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        var source = sources[i];
                        var orderedCols = executionPlans[i];
                        var fileNumber = i + 1;

                        // Verify source file stamp immediately before opening this source.
                        if (!TryValidateSourceStamp(source, "before streaming", out var preStampDiag))
                        {
                            throw new FtImportAbortException(preStampDiag!);
                        }

                        SafeReport(new FtImportProgress(
                            FtImportProgressPhase.StreamingSource,
                            fileNumber,
                            totalFiles,
                            source.SourceFile.FileName,
                            totalProcessed,
                            totalRetained,
                            totalFiltered));
                        lastProgressTick = Stopwatch.GetTimestamp();

                        var lotId = source.SourceFile.LotId;
                        var testRoleText = source.SourceFile.TestRole == FtTestRole.Original ? "Original" : "Retest";
                        var baseProcessed = totalProcessed;
                        var baseRetained = totalRetained;
                        var baseFiltered = totalFiltered;

                        IProgress<DatalogParseProgress>? parserProgress = progress is null
                            ? null
                            : new InlineProgress<DatalogParseProgress>(p =>
                            {
                                var now = Stopwatch.GetTimestamp();
                                if (now - lastProgressTick < minProgressTicks)
                                {
                                    return;
                                }

                                lastProgressTick = now;
                                var curProcessed = baseProcessed + p.ProcessedRowCount;
                                var curRetained = baseRetained + p.RetainedRowCount;
                                var curFiltered = baseFiltered + (p.ProcessedRowCount - p.RetainedRowCount);
                                SafeReport(new FtImportProgress(
                                    FtImportProgressPhase.StreamingSource,
                                    fileNumber,
                                    totalFiles,
                                    source.SourceFile.FileName,
                                    curProcessed,
                                    curRetained,
                                    curFiltered));
                            });

                        var parseResult = _parser.ParseFile(
                            filePath: source.SourceFile.SourcePath,
                            profile: preparedPlan.ProfileSnapshot,
                            structure: source.Structure,
                            selectedHeaders: preparedPlan.SelectedMeasurementHeaders,
                            onRow: row =>
                            {
                                writer.BeginRow();
                                writer.WriteString(lotId);
                                writer.WriteString(row.Bin);
                                if (row.Site is null)
                                {
                                    writer.WriteNull();
                                }
                                else
                                {
                                    writer.WriteString(row.Site);
                                }

                                writer.WriteString(testRoleText);

                                var measurements = row.Measurements.Span;
                                for (var c = 0; c < orderedCols.Length; c++)
                                {
                                    var col = orderedCols[c];
                                    if (col.Presence == FtSourceMeasurementColumnPresence.AbsentFromSource)
                                    {
                                        writer.WriteDouble(0.0);
                                    }
                                    else
                                    {
                                        var value = measurements[col.ParserMeasurementIndex];
                                        if (value.HasValue)
                                        {
                                            writer.WriteDouble(value.Value);
                                        }
                                        else
                                        {
                                            writer.WriteNull();
                                        }
                                    }
                                }

                                writer.EndRow();
                            },
                            includeBin: source.BinRetentionPolicy.IncludeBinPredicate,
                            progress: parserProgress,
                            cancellationToken: cancellationToken,
                            maxDataRows: null);

                        // Explicitly fail and roll back on any unsuccessful parser result.
                        if (!parseResult.IsSuccess)
                        {
                            var parseDiag = parseResult.Diagnostics.Count > 0 ? parseResult.Diagnostics[0] : null;
                            var rowPart = parseDiag?.SourceRowNumber is { } r ? $" (row {r})" : string.Empty;
                            var colPart = !string.IsNullOrEmpty(parseDiag?.ColumnHeader) ? $" [column '{parseDiag.ColumnHeader}']" : string.Empty;
                            var msg = $"Source '{source.SourceFile.FileName}'{rowPart}{colPart}: {parseDiag?.Message ?? "Datalog row parsing failed."}";
                            throw new FtImportAbortException(new FtImportDiagnostic(
                                FtImportDiagnosticSeverity.Error,
                                FtImportDiagnosticCode.RowParseFailed,
                                msg,
                                SourcePath: source.SourceFile.SourcePath,
                                LotId: source.SourceFile.LotId,
                                TestStage: source.SourceFile.TestStage));
                        }

                        // Verify source file stamp immediately after streaming this source.
                        if (!TryValidateSourceStamp(source, "after streaming", out var postStampDiag))
                        {
                            throw new FtImportAbortException(postStampDiag!);
                        }

                        var sourceFiltered = parseResult.ProcessedRowCount - parseResult.RetainedRowCount;
                        totalProcessed += parseResult.ProcessedRowCount;
                        totalRetained += parseResult.RetainedRowCount;
                        totalFiltered += sourceFiltered;

                        sourceSummaries.Add(new FtSourceImportSummary(
                            source.SourceFile,
                            parseResult.ProcessedRowCount,
                            parseResult.RetainedRowCount,
                            sourceFiltered));
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    // Reject zero total retained rows without creating an empty FT Worksheet.
                    if (totalRetained == 0)
                    {
                        throw new FtImportAbortException(new FtImportDiagnostic(
                            FtImportDiagnosticSeverity.Error,
                            FtImportDiagnosticCode.ZeroRetainedRows,
                            "No datalog rows were retained after applying FT/RT Bin filtering; no Worksheet was created."));
                    }

                    // Final source stamp verification before producer completion and transaction commit.
                    for (var i = 0; i < totalFiles; i++)
                    {
                        if (!TryValidateSourceStamp(sources[i], "before commit", out var finalStampDiag))
                        {
                            throw new FtImportAbortException(finalStampDiag!);
                        }
                    }

                    // Streaming completed; transaction is about to commit.
                    SafeReport(new FtImportProgress(
                        FtImportProgressPhase.Committing,
                        totalFiles,
                        totalFiles,
                        null,
                        totalProcessed,
                        totalRetained,
                        totalFiltered));
                },
                cancellationToken);
        }
        catch (FtImportAbortException ex)
        {
            errors.Add(ex.Diagnostic);
            return BuildFailure(errors, warnings);
        }
        catch (ValidationException ex)
        {
            errors.Add(new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.WorksheetCreationFailed,
                ex.Message));
            return BuildFailure(errors, warnings);
        }

        // 3. Post-commit boundary: transaction is durably committed.
        // Always return the committed result even if cancellation or progress observer errors occur here.
        SafeReport(new FtImportProgress(
            FtImportProgressPhase.Completed,
            totalFiles,
            totalFiles,
            null,
            totalProcessed,
            totalRetained,
            totalFiltered));

        var warningSnapshot = Array.AsReadOnly(warnings.ToArray());
        return new FtStreamingImportResult(
            IsSuccess: true,
            Worksheet: committedResult.Worksheet,
            Columns: Array.AsReadOnly(committedResult.Columns.ToArray()),
            TotalProcessedRowCount: totalProcessed,
            TotalRetainedRowCount: totalRetained,
            TotalFilteredRowCount: totalFiltered,
            SourceSummaries: Array.AsReadOnly(sourceSummaries.ToArray()),
            Errors: Array.AsReadOnly(Array.Empty<FtImportDiagnostic>()),
            Warnings: warningSnapshot,
            AllDiagnostics: warningSnapshot);
    }

    private static bool TryValidateSourceStamp(
        FtPreparedSourceFile source,
        string stageLabel,
        out FtImportDiagnostic? error)
    {
        error = null;
        var expected = source.FileStamp;
        if (expected is null)
        {
            error = new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.SourceFileStaleOrMissing,
                $"Source '{source.SourceFile.FileName}' is missing a prepared file stamp ({stageLabel}). Rescan the selected files before importing.",
                SourcePath: source.SourceFile.SourcePath,
                LotId: source.SourceFile.LotId,
                TestStage: source.SourceFile.TestStage);
            return false;
        }

        try
        {
            var info = new FileInfo(source.SourceFile.SourcePath);
            if (!info.Exists)
            {
                error = new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.SourceFileStaleOrMissing,
                    $"Source '{source.SourceFile.FileName}' was not found ({stageLabel}). Rescan the selected files before importing.",
                    SourcePath: source.SourceFile.SourcePath,
                    LotId: source.SourceFile.LotId,
                    TestStage: source.SourceFile.TestStage);
                return false;
            }

            var current = new FtSourceFileStamp(info.Length, info.LastWriteTimeUtc);
            if (current != expected)
            {
                error = new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.SourceFileStaleOrMissing,
                    $"Source '{source.SourceFile.FileName}' changed on disk since schema preparation ({stageLabel}). Rescan the selected files before importing.",
                    SourcePath: source.SourceFile.SourcePath,
                    LotId: source.SourceFile.LotId,
                    TestStage: source.SourceFile.TestStage);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.SourceFileStaleOrMissing,
                $"Source '{source.SourceFile.FileName}' is inaccessible ({stageLabel}): {ex.Message}. Rescan the selected files before importing.",
                SourcePath: source.SourceFile.SourcePath,
                LotId: source.SourceFile.LotId,
                TestStage: source.SourceFile.TestStage);
            return false;
        }
    }

    private static bool TryBuildOrderedColumnPlan(
        FtPreparedSourceFile source,
        int expectedMeasurementCount,
        out FtSourceMeasurementColumnMapping[]? ordered,
        out FtImportDiagnostic? error)
    {
        ordered = null;
        error = null;

        var cols = source.MeasurementMapping.Columns;
        if (cols.Count != expectedMeasurementCount)
        {
            error = new FtImportDiagnostic(
                FtImportDiagnosticSeverity.Error,
                FtImportDiagnosticCode.InvalidImportPlan,
                $"Source '{source.SourceFile.FileName}' measurement mapping count ({cols.Count}) does not match selected measurement count ({expectedMeasurementCount}).",
                SourcePath: source.SourceFile.SourcePath,
                LotId: source.SourceFile.LotId,
                TestStage: source.SourceFile.TestStage);
            return false;
        }

        var metadataOffset = FtOutputSchemaColumns.MetadataColumns.Count;
        var result = new FtSourceMeasurementColumnMapping[expectedMeasurementCount];
        var filled = new bool[expectedMeasurementCount];

        for (var i = 0; i < cols.Count; i++)
        {
            var col = cols[i];
            var slot = col.OutputWorksheetColumnIndex - metadataOffset;
            if (slot < 0 || slot >= expectedMeasurementCount || filled[slot])
            {
                error = new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Source '{source.SourceFile.FileName}' has an invalid or duplicate OutputWorksheetColumnIndex ({col.OutputWorksheetColumnIndex}) for header '{col.Header}'.",
                    SourcePath: source.SourceFile.SourcePath,
                    LotId: source.SourceFile.LotId,
                    TestStage: source.SourceFile.TestStage);
                return false;
            }

            if (col.ParserMeasurementIndex < 0 || col.ParserMeasurementIndex >= expectedMeasurementCount)
            {
                error = new FtImportDiagnostic(
                    FtImportDiagnosticSeverity.Error,
                    FtImportDiagnosticCode.InvalidImportPlan,
                    $"Source '{source.SourceFile.FileName}' has an out-of-range ParserMeasurementIndex ({col.ParserMeasurementIndex}) for header '{col.Header}'.",
                    SourcePath: source.SourceFile.SourcePath,
                    LotId: source.SourceFile.LotId,
                    TestStage: source.SourceFile.TestStage);
                return false;
            }

            filled[slot] = true;
            result[slot] = col;
        }

        ordered = result;
        return true;
    }

    private static FtStreamingImportResult BuildFailure(
        List<FtImportDiagnostic> errors,
        List<FtImportDiagnostic> warnings)
    {
        var errArr = Array.AsReadOnly(errors.ToArray());
        var warnArr = Array.AsReadOnly(warnings.ToArray());
        var all = new List<FtImportDiagnostic>(errors.Count + warnings.Count);
        all.AddRange(errors);
        all.AddRange(warnings);
        return new FtStreamingImportResult(
            IsSuccess: false,
            Worksheet: null,
            Columns: Array.AsReadOnly(Array.Empty<WorksheetColumn>()),
            TotalProcessedRowCount: 0,
            TotalRetainedRowCount: 0,
            TotalFilteredRowCount: 0,
            SourceSummaries: Array.AsReadOnly(Array.Empty<FtSourceImportSummary>()),
            Errors: errArr,
            Warnings: warnArr,
            AllDiagnostics: Array.AsReadOnly(all.ToArray()));
    }

    private sealed class FtImportAbortException(FtImportDiagnostic diagnostic) : Exception(diagnostic.Message)
    {
        public FtImportDiagnostic Diagnostic { get; } = diagnostic;
    }

    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
