using YAT.Application.Abstractions.Persistence;
using YAT.Application.ImportProfiles;
using YAT.Domain.Enums;

namespace YAT.Application.FtImport;

// Reserved output Worksheet metadata column schema for FT Datalog Import (FT-03.2 / FT-03.3).
public static class FtOutputSchemaColumns
{
    public const string LotId = "Lot_ID";
    public const string Bin = "Bin";
    public const string Site = "Site";
    public const string TestRole = "Test_Role";

    public static IReadOnlyList<string> MetadataColumnNames { get; } =
        Array.AsReadOnly([LotId, Bin, Site, TestRole]);

    public static IReadOnlyList<WorksheetColumnDefinition> MetadataColumns { get; } =
        Array.AsReadOnly(
        [
            new WorksheetColumnDefinition(LotId, WorksheetDataType.String, ColumnSemanticType.Lot),
            new WorksheetColumnDefinition(Bin, WorksheetDataType.String, ColumnSemanticType.Bin),
            new WorksheetColumnDefinition(Site, WorksheetDataType.String, ColumnSemanticType.Site),
            new WorksheetColumnDefinition(TestRole, WorksheetDataType.String, SemanticType: null)
        ]);

    public static bool IsReservedColumnName(string header) =>
        string.Equals(header, LotId, StringComparison.Ordinal)
        || string.Equals(header, Bin, StringComparison.Ordinal)
        || string.Equals(header, Site, StringComparison.Ordinal)
        || string.Equals(header, TestRole, StringComparison.Ordinal);
}

// Best-effort source file metadata captured during structure scanning for FT-03.3 stale-plan detection.
// This is lightweight change detection, not cryptographic content identity; matching stamps do not guarantee identical contents.
public sealed record FtSourceFileStamp(
    long LengthBytes,
    DateTime LastWriteTimeUtc);

// Immutable per-source structure scan snapshot produced during FT-03.2 Union Schema preparation.
public sealed record FtScannedSourceFile(
    FtPlannedSourceFile SourceFile,
    DatalogStructureScanResult Structure,
    string? ArchiveEntryName,
    FtSourceFileStamp? FileStamp,
    IReadOnlyList<string> AvailableMeasurementHeaders);

// Immutable multi-file Union Schema snapshot across all selected source files in an FtImportPlan.
public sealed record FtUnionSchema(
    FtImportPlan ImportPlan,
    ImportProfile ProfileSnapshot,
    IReadOnlyList<FtScannedSourceFile> ScannedFiles,
    IReadOnlyList<string> UnionMeasurementHeaders,
    IReadOnlyList<FtImportDiagnostic> Diagnostics);

public sealed record FtSchemaScanResult(
    bool IsSuccess,
    FtUnionSchema? UnionSchema,
    IReadOnlyList<FtImportDiagnostic> Errors,
    IReadOnlyList<FtImportDiagnostic> Warnings,
    IReadOnlyList<FtImportDiagnostic> AllDiagnostics);

// Explicit classification of whether a selected output measurement column exists in a specific source file's schema.
// Distinguishes schema-level absence (FT-03.3 writes Numeric 0) from cell-level Missing values or Parse Errors.
public enum FtSourceMeasurementColumnPresence
{
    PresentInSource,
    AbsentFromSource
}

// Deterministic per-column mapping from a selected output measurement to one source file's resolved header and parser slot.
public sealed record FtSourceMeasurementColumnMapping(
    int SelectedMeasurementIndex,
    int OutputWorksheetColumnIndex,
    string Header,
    FtSourceMeasurementColumnPresence Presence,
    int? SourceColumnIndex,
    int ParserMeasurementIndex);

// Deterministic per-source measurement mapping aligned with both the final Worksheet schema and DatalogRowParser.
public sealed record FtSourceMeasurementMapping(
    IReadOnlyList<FtSourceMeasurementColumnMapping> Columns,
    IReadOnlyList<int> PresentMeasurementIndices,
    IReadOnlyList<int> AbsentMeasurementIndices,
    IReadOnlyList<string> AbsentHeaders,
    SelectedMeasurementMapping ParserMapping);

// Complete immutable per-source execution specification consumed by FT-03.3 streaming import.
public sealed record FtPreparedSourceFile(
    FtPlannedSourceFile SourceFile,
    DatalogStructureScanResult Structure,
    string? ArchiveEntryName,
    FtSourceFileStamp? FileStamp,
    FtSourceMeasurementMapping MeasurementMapping,
    FtSourceBinRetentionPolicy BinRetentionPolicy);

// Immutable prepared FT datalog import plan containing schema, per-source mappings, and Bin retention policies.
// Holds no open streams, no parsed row datasets, no mutable selection references, and performs no persistence.
public sealed record FtPreparedImportPlan(
    FtImportPlan ImportPlan,
    ImportProfile ProfileSnapshot,
    IReadOnlyList<string> UnionMeasurementHeaders,
    IReadOnlyList<string> SelectedMeasurementHeaders,
    IReadOnlyList<WorksheetColumnDefinition> OutputColumns,
    IReadOnlyList<FtPreparedSourceFile> Sources,
    IReadOnlyList<FtImportDiagnostic> Diagnostics);

public sealed record FtPreparedImportPlanResult(
    bool IsValid,
    FtPreparedImportPlan? Plan,
    IReadOnlyList<FtImportDiagnostic> Errors,
    IReadOnlyList<FtImportDiagnostic> Warnings,
    IReadOnlyList<FtImportDiagnostic> AllDiagnostics);
