using YAT.Application.Abstractions.Persistence;
using YAT.Domain.Entities;

namespace YAT.Application.FtImport;

public enum FtImportProgressPhase
{
    Validating,
    StreamingSource,
    Committing,
    Completed
}

// Lightweight Application-level progress snapshot reported at throttled intervals during FT-03.3 streaming import.
// Distinguishes StreamingSource (reading/appending rows), Committing (transaction commit), and Completed (post-commit).
public sealed record FtImportProgress(
    FtImportProgressPhase Phase,
    int CurrentFileIndex,
    int TotalFileCount,
    string? CurrentFileName,
    long ProcessedRowCount,
    long RetainedRowCount,
    long FilteredRowCount);

public sealed record FtSourceImportSummary(
    FtPlannedSourceFile SourceFile,
    long ProcessedRowCount,
    long RetainedRowCount,
    long FilteredRowCount);

public sealed record FtStreamingImportResult(
    bool IsSuccess,
    Worksheet? Worksheet,
    IReadOnlyList<WorksheetColumn> Columns,
    long TotalProcessedRowCount,
    long TotalRetainedRowCount,
    long TotalFilteredRowCount,
    IReadOnlyList<FtSourceImportSummary> SourceSummaries,
    IReadOnlyList<FtImportDiagnostic> Errors,
    IReadOnlyList<FtImportDiagnostic> Warnings,
    IReadOnlyList<FtImportDiagnostic> AllDiagnostics);
