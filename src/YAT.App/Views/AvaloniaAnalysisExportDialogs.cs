using Avalonia.Controls;
using Avalonia.Platform.Storage;
using YAT.app.Analyses.Export;

namespace YAT.app.Views;

// Save-file and message dialogs for exporting an analysis result table to Excel (.xlsx).
public sealed class AvaloniaAnalysisExportDialogs : IAnalysisExportDialogs
{
    private static readonly FilePickerFileType ExcelFileType = new("Excel Workbook")
    {
        Patterns = ["*.xlsx"]
    };

    private readonly Window _owner;

    public AvaloniaAnalysisExportDialogs(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        _owner = owner;
    }

    public async Task<string?> PickExcelAsync(string suggestedFileName)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Excel",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = AnalysisExportController.ExcelExtension,
            FileTypeChoices = [ExcelFileType]
        });

        return file?.TryGetLocalPath();
    }

    public Task ShowInformationAsync(string message) =>
        LifecycleMessageWindow.ShowInformationAsync(_owner, message);

    public Task ShowErrorAsync(string message) =>
        LifecycleMessageWindow.ShowErrorAsync(_owner, message);
}
