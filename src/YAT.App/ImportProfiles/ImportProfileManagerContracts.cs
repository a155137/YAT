using YAT.Application.ImportProfiles;

namespace YAT.app.ImportProfiles;

public enum ImportProfileEditorMode
{
    Create,
    Edit
}

public sealed record ImportProfileEditorRequest(
    ImportProfileEditorMode Mode,
    ImportProfile? ExistingProfile);

// Integration boundary for the future Visual Profile Builder (FT-02.2).
public interface IImportProfileEditor
{
    bool IsAvailable { get; }

    Task<ImportProfile?> EditAsync(ImportProfileEditorRequest request, CancellationToken cancellationToken = default);
}

internal sealed class UnavailableImportProfileEditor : IImportProfileEditor
{
    public static UnavailableImportProfileEditor Instance { get; } = new();

    public bool IsAvailable => false;

    public Task<ImportProfile?> EditAsync(ImportProfileEditorRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Visual Profile Builder is not available until FT-02.2.");
}

public enum ImportDuplicateIdChoice
{
    Cancel,
    ImportAsCopy
}

public interface IImportProfileManagerDialogs
{
    Task<bool> ConfirmDeleteAsync(ImportProfile profile);

    Task<string?> PickImportFileAsync();

    Task<string?> PickExportFileAsync(string suggestedFileName);

    Task<bool> ConfirmOverwriteExportAsync(string destinationPath);

    Task<ImportDuplicateIdChoice> AskDuplicateIdAsync(ImportProfile importedProfile, ImportProfile existingProfile);

    Task ShowErrorAsync(string message, string? detail = null);
}
