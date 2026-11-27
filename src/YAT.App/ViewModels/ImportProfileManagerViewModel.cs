using System.Collections.ObjectModel;
using System.Security;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Abstractions.ImportProfiles;
using YAT.Application.ImportProfiles;
using YAT.app.ImportProfiles;

namespace YAT.app.ViewModels;

// ViewModel for the FT Import Profile Manager (FT-02.1).
// Manages listing, selection, duplication, deletion, JSON import/export, and the FT-02.2 editor boundary.
public sealed partial class ImportProfileManagerViewModel : ObservableObject
{
    private readonly IImportProfileStore _store;
    private readonly IImportProfileSerializer _serializer;
    private readonly IImportProfileManagerDialogs _dialogs;
    private readonly IImportProfileEditor _editor;
    private readonly Func<Guid> _newId;
    private readonly ImportProfileValidator _validator = new();

    public ImportProfileManagerViewModel(
        IImportProfileStore store,
        IImportProfileSerializer serializer,
        IImportProfileManagerDialogs dialogs,
        IImportProfileEditor? editor = null,
        Func<Guid>? newId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(serializer);
        ArgumentNullException.ThrowIfNull(dialogs);

        _store = store;
        _serializer = serializer;
        _dialogs = dialogs;
        _editor = editor ?? UnavailableImportProfileEditor.Instance;
        _newId = newId ?? Guid.NewGuid;

        NewCommand = new AsyncRelayCommand(NewAsync, () => CanNew);
        EditCommand = new AsyncRelayCommand(EditAsync, () => CanEdit);
        DuplicateCommand = new AsyncRelayCommand(DuplicateAsync, () => CanDuplicate);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => CanDelete);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => CanImport);
        ExportCommand = new AsyncRelayCommand(ExportAsync, () => CanExport);

        Reload();
    }

    public ObservableCollection<ImportProfile> Profiles { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanDuplicate))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    [NotifyPropertyChangedFor(nameof(SelectedName))]
    [NotifyPropertyChangedFor(nameof(SelectedIdText))]
    [NotifyPropertyChangedFor(nameof(SelectedSchemaVersionText))]
    [NotifyPropertyChangedFor(nameof(SelectedExtensionsText))]
    [NotifyPropertyChangedFor(nameof(SelectedFilenameModeText))]
    [NotifyPropertyChangedFor(nameof(SelectedValidationStatus))]
    [NotifyPropertyChangedFor(nameof(SelectedStructureSummary))]
    [NotifyPropertyChangedFor(nameof(SelectedBinRulesSummary))]
    [NotifyPropertyChangedFor(nameof(SelectedMissingRulesSummary))]
    public partial ImportProfile? Selected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanNew))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyPropertyChangedFor(nameof(CanDuplicate))]
    [NotifyPropertyChangedFor(nameof(CanDelete))]
    [NotifyPropertyChangedFor(nameof(CanImport))]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssues))]
    public partial string? IssuesSummary { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    public bool HasSelection => Selected is not null;

    public bool HasIssues => !string.IsNullOrEmpty(IssuesSummary);

    public bool IsEditorAvailable => _editor.IsAvailable;

    public string? EditorUnavailableNotice =>
        IsEditorAvailable ? null : "New and Edit require the Visual Profile Builder (coming in FT-02.2).";

    public string EmptyStateText =>
        "No FT Import Profiles found. Use Import to load an existing profile JSON.";

    public bool CanNew => IsEditorAvailable && !IsBusy;

    public bool CanEdit => IsEditorAvailable && !IsBusy && Selected is not null;

    public bool CanDuplicate => !IsBusy && Selected is not null;

    public bool CanDelete => !IsBusy && Selected is not null;

    public bool CanImport => !IsBusy;

    public bool CanExport => !IsBusy && Selected is not null;

    public string SelectedName => Selected?.Name ?? string.Empty;

    public string SelectedIdText => Selected is { } p ? p.Id.ToString("D") : string.Empty;

    public string SelectedSchemaVersionText => Selected is { } p ? $"v{p.SchemaVersion}" : string.Empty;

    public string SelectedExtensionsText =>
        Selected is { } p ? string.Join(", ", p.FilenameRules.AcceptedExtensions) : string.Empty;

    public string SelectedFilenameModeText =>
        Selected is { } p ? FormatFilenameMode(p.FilenameRules) : string.Empty;

    public string SelectedValidationStatus =>
        Selected is { } p
            ? (_validator.Validate(p).IsValid ? "Valid" : "Invalid")
            : string.Empty;

    public string SelectedStructureSummary =>
        Selected is { } p ? FormatStructureSummary(p.StructureRules) : string.Empty;

    public string SelectedBinRulesSummary =>
        Selected is { } p ? FormatBinRulesSummary(p.BinRules) : string.Empty;

    public string SelectedMissingRulesSummary =>
        Selected is { } p ? FormatMissingRulesSummary(p.MissingValueRules) : string.Empty;

    public IAsyncRelayCommand NewCommand { get; }

    public IAsyncRelayCommand EditCommand { get; }

    public IAsyncRelayCommand DuplicateCommand { get; }

    public IAsyncRelayCommand DeleteCommand { get; }

    public IAsyncRelayCommand ImportCommand { get; }

    public IAsyncRelayCommand ExportCommand { get; }

    // Test-only fault injection for atomic export verification. Always null in production.
    internal Action? ExportWritePhaseHook { get; set; }

    public void Reload(Guid? selectProfileId = null)
    {
        var preferredId = selectProfileId ?? Selected?.Id;

        ImportProfileStoreSnapshot snapshot;
        try
        {
            snapshot = _store.Enumerate();
        }
        catch (Exception ex) when (ex is ImportProfileStoreException or IOException or UnauthorizedAccessException or SecurityException)
        {
            Profiles.Clear();
            Selected = null;
            IsEmpty = true;
            IssuesSummary = $"Could not read import profiles: {ex.Message}";
            NotifyCommandStates();
            return;
        }

        Profiles.Clear();
        foreach (var profile in snapshot.Profiles)
        {
            Profiles.Add(profile);
        }

        IsEmpty = Profiles.Count == 0;
        IssuesSummary = FormatIssues(snapshot.Issues);

        Selected = preferredId is { } id
            ? Profiles.FirstOrDefault(p => p.Id == id) ?? Profiles.FirstOrDefault()
            : Profiles.FirstOrDefault();

        NotifyCommandStates();
    }

    partial void OnSelectedChanged(ImportProfile? value) => NotifyCommandStates();

    partial void OnIsBusyChanged(bool value) => NotifyCommandStates();

    private void NotifyCommandStates()
    {
        NewCommand.NotifyCanExecuteChanged();
        EditCommand.NotifyCanExecuteChanged();
        DuplicateCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        ImportCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
    }

    private async Task NewAsync()
    {
        if (!CanNew)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var draft = await _editor.EditAsync(new ImportProfileEditorRequest(ImportProfileEditorMode.Create, null));
            if (draft is null)
            {
                return;
            }

            var validation = _validator.Validate(draft);
            if (!validation.IsValid)
            {
                await _dialogs.ShowErrorAsync("The new profile is invalid and was not saved.", validation.Errors[0].Message);
                return;
            }

            _store.Create(draft);
            Reload(draft.Id);
        }
        catch (Exception ex) when (IsHandledStorageOrEditorException(ex))
        {
            await _dialogs.ShowErrorAsync("Could not create the import profile.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task EditAsync()
    {
        if (!CanEdit || Selected is not { } current)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var edited = await _editor.EditAsync(new ImportProfileEditorRequest(ImportProfileEditorMode.Edit, current));
            if (edited is null)
            {
                return;
            }

            // Preserve existing identity when editing.
            var normalized = edited with { Id = current.Id };
            var validation = _validator.Validate(normalized);
            if (!validation.IsValid)
            {
                await _dialogs.ShowErrorAsync("The edited profile is invalid and changes were not saved.", validation.Errors[0].Message);
                return;
            }

            _store.Update(normalized);
            Reload(normalized.Id);
        }
        catch (Exception ex) when (IsHandledStorageOrEditorException(ex))
        {
            await _dialogs.ShowErrorAsync("Could not update the import profile.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DuplicateAsync()
    {
        if (!CanDuplicate || Selected is not { } source)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var copyName = GenerateNonConflictingName(source.Name);
            var copy = source with
            {
                Id = _newId(),
                Name = copyName
            };

            var validation = _validator.Validate(copy);
            if (!validation.IsValid)
            {
                await _dialogs.ShowErrorAsync("Could not duplicate the selected profile because it failed validation.", validation.Errors[0].Message);
                return;
            }

            _store.Create(copy);
            Reload(copy.Id);
        }
        catch (Exception ex) when (IsHandledStorageOrEditorException(ex))
        {
            await _dialogs.ShowErrorAsync("Could not duplicate the import profile.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (!CanDelete || Selected is not { } target)
        {
            return;
        }

        IsBusy = true;
        try
        {
            if (!await _dialogs.ConfirmDeleteAsync(target))
            {
                return;
            }

            _store.Delete(target.Id);
            Reload();
        }
        catch (Exception ex) when (IsHandledStorageOrEditorException(ex))
        {
            await _dialogs.ShowErrorAsync($"Could not delete “{target.Name}”.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ImportAsync()
    {
        if (!CanImport)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var path = await _dialogs.PickImportFileAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                await _dialogs.ShowErrorAsync("Could not read the selected profile file.", ex.Message);
                return;
            }

            ImportProfile imported;
            try
            {
                imported = _serializer.Deserialize(bytes);
            }
            catch (ImportProfileStoreException ex)
            {
                await _dialogs.ShowErrorAsync("The selected file is not a supported import profile.", ex.Message);
                return;
            }

            var validation = _validator.Validate(imported);
            if (!validation.IsValid)
            {
                await _dialogs.ShowErrorAsync("The imported profile is invalid.", validation.Errors[0].Message);
                return;
            }

            ImportProfile? existing;
            try
            {
                existing = _store.Load(imported.Id) ?? Profiles.FirstOrDefault(p => p.Id == imported.Id);
            }
            catch (ImportProfileStoreException ex) when (ex.Error is ImportProfileStoreError.MalformedJson
                or ImportProfileStoreError.UnsupportedSchemaVersion
                or ImportProfileStoreError.DuplicateIdentity
                or ImportProfileStoreError.InvalidProfile)
            {
                await _dialogs.ShowErrorAsync(
                    $"Cannot import profile “{imported.Name}” because an existing file for ID “{imported.Id:D}” in storage is invalid or unsupported.",
                    $"{ex.Message} The existing file was left unchanged.");
                return;
            }
            var toSave = imported;
            if (existing is not null)
            {
                var choice = await _dialogs.AskDuplicateIdAsync(imported, existing);
                if (choice != ImportDuplicateIdChoice.ImportAsCopy)
                {
                    return;
                }

                var nonConflictingName = Profiles.Any(p => string.Equals(p.Name, imported.Name, StringComparison.OrdinalIgnoreCase))
                    ? GenerateNonConflictingName(imported.Name)
                    : imported.Name;

                toSave = imported with
                {
                    Id = _newId(),
                    Name = nonConflictingName
                };
            }

            _store.Create(toSave);
            Reload(toSave.Id);
        }
        catch (Exception ex) when (IsHandledStorageOrEditorException(ex))
        {
            await _dialogs.ShowErrorAsync("Could not import the profile.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ExportAsync()
    {
        if (!CanExport || Selected is not { } selected)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var loaded = _store.Load(selected.Id);
            if (loaded is null)
            {
                await _dialogs.ShowErrorAsync($"Profile “{selected.Name}” no longer exists in storage.");
                Reload();
                return;
            }

            var validation = _validator.Validate(loaded);
            if (!validation.IsValid)
            {
                await _dialogs.ShowErrorAsync($"Profile “{selected.Name}” is invalid and cannot be exported.", validation.Errors[0].Message);
                return;
            }

            var suggestedName = SanitizeFileName(loaded.Name) + ".json";
            var destination = await _dialogs.PickExportFileAsync(suggestedName);
            if (string.IsNullOrWhiteSpace(destination))
            {
                return;
            }

            if (!destination.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                destination += ".json";
            }

            if (IsManagedStoreDestination(destination))
            {
                await _dialogs.ShowErrorAsync(
                    "Cannot export to the managed ImportProfiles directory.",
                    "Choose a destination outside UserData/ImportProfiles so managed profile files are not overwritten.");
                return;
            }

            if (File.Exists(destination) && !await _dialogs.ConfirmOverwriteExportAsync(destination))
            {
                return;
            }

            var bytes = _serializer.Serialize(loaded);
            WriteExportAtomically(destination, bytes);
        }
        catch (Exception ex) when (IsHandledStorageOrEditorException(ex))
        {
            await _dialogs.ShowErrorAsync($"Could not export “{selected.Name}”.", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool IsManagedStoreDestination(string destinationPath)
    {
        var candidates = new List<string>();
        var fullDest = Path.GetFullPath(destinationPath);
        candidates.Add(fullDest);

        var destInfo = new FileInfo(fullDest);
        if (destInfo.Exists && (destInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || destInfo.LinkTarget is not null))
        {
            if (destInfo.ResolveLinkTarget(returnFinalTarget: true) is { } resolvedFile)
            {
                candidates.Add(Path.GetFullPath(resolvedFile.FullName));
            }
        }

        var parentDir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(parentDir))
        {
            var parentInfo = new DirectoryInfo(parentDir);
            if (parentInfo.Exists && (parentInfo.Attributes.HasFlag(FileAttributes.ReparsePoint) || parentInfo.LinkTarget is not null))
            {
                if (parentInfo.ResolveLinkTarget(returnFinalTarget: true) is { } resolvedDir)
                {
                    candidates.Add(Path.Combine(Path.GetFullPath(resolvedDir.FullName), Path.GetFileName(fullDest)));
                }
            }
        }

        var storeRoots = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "UserData", "ImportProfiles")
        };
        if (!string.IsNullOrWhiteSpace(_store.StorageDirectoryPath))
        {
            storeRoots.Add(_store.StorageDirectoryPath);
        }

        foreach (var root in storeRoots)
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = normalizedRoot + Path.DirectorySeparatorChar;

            foreach (var candidate in candidates)
            {
                var normalizedCandidate = candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                    || normalizedCandidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void WriteExportAtomically(string destinationPath, byte[] bytes)
    {
        var fullDestination = Path.GetFullPath(destinationPath);
        var directory = Path.GetDirectoryName(fullDestination)
            ?? throw new IOException($"Export destination '{destinationPath}' has no parent directory.");
        var tempPath = Path.Combine(directory, $"{Path.GetFileName(fullDestination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            ExportWritePhaseHook?.Invoke();
            File.Move(tempPath, fullDestination, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private string GenerateNonConflictingName(string baseName)
    {
        var trimmed = baseName.Trim();
        var existingNames = Profiles.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidate = $"{trimmed} (Copy)";
        if (!existingNames.Contains(candidate))
        {
            return candidate;
        }

        for (var i = 2; ; i++)
        {
            candidate = $"{trimmed} (Copy {i})";
            if (!existingNames.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrEmpty(sanitized) ? "import-profile" : sanitized;
    }

    private static string? FormatIssues(IReadOnlyList<ImportProfileLoadIssue> issues)
    {
        if (issues.Count == 0)
        {
            return null;
        }

        const int maxShown = 3;
        var lines = issues.Take(maxShown).Select(i => $"{i.FileName}: {i.Detail}");
        var summary = string.Join(Environment.NewLine, lines);
        if (issues.Count > maxShown)
        {
            summary += $"{Environment.NewLine}...and {issues.Count - maxShown} more issue(s).";
        }

        return $"Some profile files could not be loaded ({issues.Count}):{Environment.NewLine}{summary}";
    }

    private static string FormatFilenameMode(FilenameRuleSet rules)
    {
        if (rules.Mode == FilenameRuleMode.Regex)
        {
            return $"Regex ({rules.AdvancedRegexPattern})";
        }

        var delimiters = string.Join(" ", rules.SegmentDelimiters.Select(d => $"'{d}'"));
        return $"Structured (Delimiters: {delimiters})";
    }

    private static string FormatStructureSummary(DatalogStructureRuleSet rules)
    {
        var delimiter = rules.Delimiter == "\t" ? "Tab (\\t)" : $"'{rules.Delimiter}'";
        var bin = rules.BinColumn.Kind == ColumnSelectorKind.HeaderName
            ? rules.BinColumn.HeaderName
            : $"Index {rules.BinColumn.ColumnIndex}";
        var site = rules.SiteColumn is null
            ? "None"
            : rules.SiteColumn.Kind == ColumnSelectorKind.HeaderName
                ? rules.SiteColumn.HeaderName
                : $"Index {rules.SiteColumn.ColumnIndex}";
        return $"Delimiter: {delimiter} | Bin: {bin} | Site: {site} | Composites: {rules.CompositeFields.Count} | Ignored: {rules.IgnoredColumns.Count}";
    }

    private static string FormatBinRulesSummary(BinRuleSet rules)
    {
        var good = rules.GoodBins.Count == 0 ? "None" : string.Join(", ", rules.GoodBins);
        var fail = rules.NonRetestFailBins.Count == 0 ? "None" : string.Join(", ", rules.NonRetestFailBins);
        return $"Good: [{good}] | Non-Retest Fail: [{fail}]";
    }

    private static string FormatMissingRulesSummary(MissingValueRuleSet rules)
    {
        var markers = string.Join(", ", rules.Markers.Select(m => m.Length == 0 ? "<empty>" : m));
        return $"Policy: {rules.Policy} | Markers: [{markers}]";
    }

    private static bool IsHandledStorageOrEditorException(Exception ex) =>
        ex is ImportProfileStoreException
            or IOException
            or UnauthorizedAccessException
            or SecurityException
            or NotSupportedException;
}
