using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public sealed record DatalogSampleMeasurementValidation(
    bool IsAttempted,
    bool IsSuccess,
    long CheckedRowCount,
    int CheckedColumnCount,
    int MaxRowLimit,
    DatalogParseDiagnostic? FailureDiagnostic,
    string Summary);

public sealed record DatalogSampleInspectionResult(
    string FilePath,
    string FileName,
    FilenameMatchResult FilenameMatch,
    DatalogPreviewResult? Preview,
    string? OpenError,
    DatalogSampleMeasurementValidation? MeasurementValidation)
{
    public bool IsBoundedSample => true;
}

// Orchestrates bounded sample datalog inspection for FT-02.3 without loading full files or duplicating FT-01 parsers.
// Reuses FilenameRuleMatcher, DatalogStreamOpener, DatalogPreviewReader, DatalogStructureScanner, and DatalogRowParser.
public sealed class DatalogSamplePreviewService
{
    public const int DefaultMaxPreviewRows = DatalogPreviewReader.DefaultMaxRows;
    public const int DefaultMaxMeasurementRows = 50;
    public const int DefaultMaxMeasurementColumns = DatalogPreviewReader.DefaultMaxColumnsPerRow;

    private readonly FilenameRuleMatcher _filenameMatcher;
    private readonly DatalogStreamOpener _opener;
    private readonly DatalogPreviewReader _previewReader;
    private readonly DatalogRowParser _rowParser;

    public DatalogSamplePreviewService(
        FilenameRuleMatcher? filenameMatcher = null,
        DatalogStreamOpener? opener = null,
        DatalogPreviewReader? previewReader = null,
        DatalogRowParser? rowParser = null)
    {
        _filenameMatcher = filenameMatcher ?? new FilenameRuleMatcher();
        _opener = opener ?? new DatalogStreamOpener();
        _previewReader = previewReader ?? new DatalogPreviewReader();
        _rowParser = rowParser ?? new DatalogRowParser(_opener);
    }

    public DatalogSampleInspectionResult Inspect(
        ImportProfile profile,
        string filePath,
        int maxPreviewRows = DefaultMaxPreviewRows,
        int maxMeasurementRows = DefaultMaxMeasurementRows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        cancellationToken.ThrowIfCancellationRequested();
        var fileName = Path.GetFileName(filePath);

        // 1. Live filename matching (never blocks reading the sample data preview).
        var filenameMatch = _filenameMatcher.Match(profile, filePath);

        // 2. Bounded Table + Raw preview and structural scan (disposes every opened stream/archive).
        DatalogPreviewResult preview;
        try
        {
            var openedHandles = new List<OpenedDatalogStream>();
            try
            {
                preview = _previewReader.ReadPreview(
                    profile,
                    () =>
                    {
                        var opened = _opener.Open(filePath, cancellationToken);
                        openedHandles.Add(opened);
                        return new DisposingTextReader(opened);
                    },
                    maxRows: maxPreviewRows,
                    cancellationToken: cancellationToken);
            }
            finally
            {
                foreach (var handle in openedHandles)
                {
                    handle.Dispose();
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatalogOpenException ex)
        {
            return new DatalogSampleInspectionResult(filePath, fileName, filenameMatch, null, ex.Message, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new DatalogSampleInspectionResult(filePath, fileName, filenameMatch, null, ex.Message, null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 3. Bounded measurement validation using DatalogRowParser when structure resolved cleanly.
        DatalogSampleMeasurementValidation? measurementValidation = null;
        if (preview.Structure is { IsSuccess: true } structure)
        {
            var selectedHeaders = structure.AvailableTestHeaders
                .Take(DefaultMaxMeasurementColumns)
                .Select(c => c.Name)
                .ToArray();

            if (selectedHeaders.Length == 0)
            {
                measurementValidation = new DatalogSampleMeasurementValidation(
                    IsAttempted: false,
                    IsSuccess: false,
                    CheckedRowCount: 0,
                    CheckedColumnCount: 0,
                    MaxRowLimit: maxMeasurementRows,
                    FailureDiagnostic: null,
                    Summary: "Structure resolved; no measurement columns available to validate (0 measurement rows checked).");
            }
            else
            {
                var parseResult = _rowParser.ParseFile(
                    filePath,
                    profile,
                    structure,
                    selectedHeaders,
                    _ => { },
                    cancellationToken: cancellationToken,
                    maxDataRows: maxMeasurementRows);

                if (parseResult.IsSuccess)
                {
                    if (parseResult.ProcessedRowCount == 0)
                    {
                        measurementValidation = new DatalogSampleMeasurementValidation(
                            IsAttempted: true,
                            IsSuccess: false,
                            CheckedRowCount: 0,
                            CheckedColumnCount: selectedHeaders.Length,
                            MaxRowLimit: maxMeasurementRows,
                            FailureDiagnostic: null,
                            Summary: $"No data rows were found to validate (0 data rows, {selectedHeaders.Length} measurement column(s) configured).");
                    }
                    else
                    {
                        measurementValidation = new DatalogSampleMeasurementValidation(
                            IsAttempted: true,
                            IsSuccess: true,
                            CheckedRowCount: parseResult.ProcessedRowCount,
                            CheckedColumnCount: selectedHeaders.Length,
                            MaxRowLimit: maxMeasurementRows,
                            FailureDiagnostic: null,
                            Summary: $"Bounded sample validation passed ({parseResult.ProcessedRowCount} data row(s), {selectedHeaders.Length} measurement column(s) checked; max {maxMeasurementRows} rows — not full-file validation).");
                    }
                }
                else
                {
                    var diag = parseResult.Diagnostics.FirstOrDefault();
                    measurementValidation = new DatalogSampleMeasurementValidation(
                        IsAttempted: true,
                        IsSuccess: false,
                        CheckedRowCount: parseResult.ProcessedRowCount,
                        CheckedColumnCount: selectedHeaders.Length,
                        MaxRowLimit: maxMeasurementRows,
                        FailureDiagnostic: diag,
                        Summary: diag is null
                            ? "Bounded sample measurement validation failed."
                            : $"Sample validation failed at row {diag.SourceRowNumber?.ToString() ?? "?"} ({diag.Code}): {diag.Message}");
                }
            }
        }

        return new DatalogSampleInspectionResult(filePath, fileName, filenameMatch, preview, null, measurementValidation);
    }

    private sealed class DisposingTextReader(OpenedDatalogStream opened) : TextReader
    {
        public override int Peek() => opened.Reader.Peek();
        public override int Read() => opened.Reader.Read();
        public override int Read(char[] buffer, int index, int count) => opened.Reader.Read(buffer, index, count);
        public override string? ReadLine() => opened.Reader.ReadLine();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                opened.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
