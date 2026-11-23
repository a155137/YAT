namespace YAT.app.Analyses.Export;

public interface IAnalysisExcelExporter
{
    void Save(string path, AnalysisResultTable table);
}

public interface IAnalysisExportDialogs
{
    Task<string?> PickExcelAsync(string suggestedFileName);

    Task ShowInformationAsync(string message);

    Task ShowErrorAsync(string message);
}

// Coordinates exporting an AnalysisResultTable to an Excel (.xlsx) workbook: validates that results exist,
// suggests a default file name per analysis type, runs the OpenXML export off the UI thread, and reports the outcome.
public sealed class AnalysisExportController
{
    public const string ExcelExtension = "xlsx";
    public const string NoResultsMessage = "There are no analysis results to export.";
    private const string FailureMessage = "Unable to export the Excel file.";

    private readonly IAnalysisExportDialogs _dialogs;
    private readonly IAnalysisExcelExporter _exporter;

    public AnalysisExportController(
        IAnalysisExportDialogs dialogs,
        IAnalysisExcelExporter exporter)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(exporter);
        _dialogs = dialogs;
        _exporter = exporter;
    }

    public string? LastExportedPath { get; private set; }

    public static string SuggestedFileName(AnalysisExportKind kind) => kind switch
    {
        AnalysisExportKind.DescriptiveStatistics => $"YAT_DescriptiveStatistics.{ExcelExtension}",
        AnalysisExportKind.CapabilityAnalysis => $"YAT_CapabilityAnalysis.{ExcelExtension}",
        _ => $"YAT_AnalysisResult.{ExcelExtension}"
    };

    public async Task<bool> ExportExcelAsync(AnalysisResultTable? table, CancellationToken cancellationToken = default)
    {
        if (table is null || table.Rows.Count == 0)
        {
            await _dialogs.ShowErrorAsync(NoResultsMessage);
            return false;
        }

        var path = await _dialogs.PickExcelAsync(SuggestedFileName(table.ExportKind));
        if (string.IsNullOrWhiteSpace(path))
        {
            // Cancelling the save dialog is not an error.
            return false;
        }

        try
        {
            await Task.Run(() => _exporter.Save(path, table), cancellationToken);
            LastExportedPath = path;
            await _dialogs.ShowInformationAsync($"Exported to {Path.GetFileName(path)}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception)
        {
            await _dialogs.ShowErrorAsync(FailureMessage);
            return false;
        }
    }
}
