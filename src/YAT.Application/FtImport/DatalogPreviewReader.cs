using YAT.Application.ImportProfiles;

namespace YAT.Application.FtImport;

public sealed record DatalogPreviewRawLine(
    int SourceRowNumber,
    string Text,
    bool IsTruncated);

public sealed record DatalogPreviewTableRow(
    int SourceRowNumber,
    IReadOnlyList<string> Cells,
    bool IsColumnTruncated);

public sealed record DatalogPreviewResult(
    IReadOnlyList<DatalogPreviewRawLine> RawLines,
    IReadOnlyList<DatalogPreviewTableRow> TableRows,
    DatalogStructureScanResult? Structure,
    IReadOnlyList<DatalogDiagnostic> Diagnostics,
    bool WasTruncatedByRowLimit,
    bool WasTruncatedBySizeLimit);

// Bounded Table / Raw preview reader for FT-02 Visual Profile Builder (FT-01B).
// Reads at most MaxRows lines and MaxPreviewChars characters, never loading the entire file.
public sealed class DatalogPreviewReader
{
    public const int DefaultMaxRows = 50;
    public const int DefaultMaxPreviewChars = 512 * 1024; // 512K chars (~1 MiB UTF-16)
    public const int DefaultMaxColumnsPerRow = 512;

    private readonly DatalogRecordTokenizer _tokenizer;
    private readonly DatalogStructureScanner _scanner;

    public DatalogPreviewReader(
        DatalogRecordTokenizer? tokenizer = null,
        DatalogStructureScanner? scanner = null)
    {
        _tokenizer = tokenizer ?? new DatalogRecordTokenizer();
        _scanner = scanner ?? new DatalogStructureScanner(_tokenizer);
    }

    public DatalogPreviewResult ReadPreview(
        ImportProfile profile,
        Func<TextReader> openReader,
        int maxRows = DefaultMaxRows,
        int maxPreviewChars = DefaultMaxPreviewChars,
        int maxColumnsPerRow = DefaultMaxColumnsPerRow,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(openReader);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPreviewChars, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxColumnsPerRow, 1);

        var rawLines = new List<DatalogPreviewRawLine>(Math.Min(maxRows, 64));
        var tableRows = new List<DatalogPreviewTableRow>(Math.Min(maxRows, 64));
        var diagnostics = new List<DatalogDiagnostic>();
        var totalChars = 0;
        var truncatedByRowLimit = false;
        var truncatedBySizeLimit = false;

        var delimiter = profile.StructureRules?.Delimiter;
        var hasValidDelimiter = !string.IsNullOrEmpty(delimiter);

        using (var reader = openReader())
        {
            var rowNumber = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (rowNumber >= maxRows)
                    {
                        if (reader.Peek() >= 0)
                        {
                            truncatedByRowLimit = true;
                        }

                        break;
                    }

                    var line = _tokenizer.ReadBoundedLine(reader, cancellationToken);
                    if (line is null)
                    {
                        break;
                    }

                    rowNumber++;
                    var remaining = maxPreviewChars - totalChars;
                    if (remaining <= 0)
                    {
                        truncatedBySizeLimit = true;
                        break;
                    }

                    var lineTruncated = false;
                    var storedLine = line;
                    if (storedLine.Length > remaining)
                    {
                        storedLine = storedLine[..remaining];
                        lineTruncated = true;
                        truncatedBySizeLimit = true;
                    }

                    totalChars += storedLine.Length;
                    rawLines.Add(new DatalogPreviewRawLine(rowNumber, storedLine, lineTruncated));

                    if (hasValidDelimiter)
                    {
                        var fields = DatalogRecordTokenizer.SplitFields(line, delimiter!);
                        var colTruncated = fields.Length > maxColumnsPerRow;
                        if (colTruncated)
                        {
                            fields = fields[..maxColumnsPerRow];
                        }

                        tableRows.Add(new DatalogPreviewTableRow(rowNumber, fields, colTruncated));
                    }

                    if (truncatedBySizeLimit)
                    {
                        break;
                    }
                }
            }
            catch (DatalogOpenException ex)
            {
                diagnostics.Add(new DatalogDiagnostic(DatalogScanErrorCode.InvalidEncoding, rowNumber + 1, ex.Message));
            }
            catch (DatalogRecordLimitException ex)
            {
                diagnostics.Add(new DatalogDiagnostic(DatalogScanErrorCode.RecordTooLarge, rowNumber + 1, ex.Message));
                truncatedBySizeLimit = true;
            }
        }

        // Run structural scan on a fresh forward-only reader (so structural rows beyond preview window e.g. >50 are still resolved).
        DatalogStructureScanResult? structure = null;
        if (diagnostics.Count == 0)
        {
            using var scanReader = openReader();
            structure = _scanner.Scan(profile, scanReader, cancellationToken);
            if (!structure.IsSuccess)
            {
                diagnostics.AddRange(structure.Diagnostics);
            }
        }

        return new DatalogPreviewResult(
            rawLines,
            tableRows,
            structure,
            diagnostics,
            truncatedByRowLimit,
            truncatedBySizeLimit);
    }
}
