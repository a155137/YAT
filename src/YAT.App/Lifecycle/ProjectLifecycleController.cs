using YAT.Application.Exceptions;
using YAT.app.Clipboard;
using YAT.app.Composition;
using YAT.app.ViewModels;

namespace YAT.app.Lifecycle;

// The desktop project lifecycle: New, Open, Save, Save As and closing, over one ProjectWorkspace. It owns the current
// project's MainWindowSession and MainWindowViewModel and replaces both whenever the workspace switches to another
// project session. User interaction goes through IProjectLifecycleDialogs, so this class has no Avalonia dependency.
//
// Lock order (always taken in this order, never the reverse):
//   1. this controller's lifecycle gate (one lifecycle operation at a time; overlapping requests are ignored)
//   2. the current MainWindowSession (SuspendAsync: waits for a running paste, delete, rename, ... to finish)
//   3. the ProjectWorkspace gate
//   4. the project database lock (DuckDbProjectDatabase)
// Dialogs are shown while holding only the lifecycle gate. Session operations take only 2 and 4.
public sealed class ProjectLifecycleController
{
    private readonly CompositionRoot _composition;
    private readonly ProjectWorkspace _workspace;
    private readonly IClipboardTextReader _clipboardReader;
    private readonly IClipboardTextWriter _clipboardWriter;
    private readonly IProjectLifecycleDialogs _dialogs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MainWindowSession? _session;
    private bool _shutDown;

    internal ProjectLifecycleController(
        CompositionRoot composition,
        ProjectWorkspace workspace,
        IClipboardTextReader clipboardReader,
        IClipboardTextWriter clipboardWriter,
        IProjectLifecycleDialogs dialogs)
    {
        _composition = composition;
        _workspace = workspace;
        _clipboardReader = clipboardReader;
        _clipboardWriter = clipboardWriter;
        _dialogs = dialogs;
    }

    // The current project's UI session; null before the first project is shown. Retired sessions reject all operations.
    public MainWindowSession? CurrentSession => _session;

    // The current project's view model; null before the first project is shown.
    public MainWindowViewModel? Project { get; private set; }

    // Raised on the calling context after Project was replaced by another project's view model.
    public event EventHandler? ProjectReplaced;

    // Whether the current temporary project has user changes worth saving before it is discarded. Only successful edits
    // count. Not a durability flag: a project file stores every edit as it happens, so it never has unsaved changes.
    public bool HasMeaningfulChanges { get; private set; }

    public bool IsTemporaryProject => _workspace.IsTemporaryProject;

    public string? ProjectFilePath => _workspace.ProjectFilePath;

    // Startup: a new untitled project.
    public Task<bool> StartAsync() =>
        RunLifecycleAsync(() => ReplaceProjectAsync(activating => _workspace.CreateTemporaryProjectAsync(CancellationToken.None, activating), null));

    // File > New Project: after the save prompt (if needed), a new untitled project replaces the current one.
    public Task<bool> NewProjectAsync() =>
        RunLifecycleAsync(async () =>
            await ConfirmDiscardAsync(closing: false)
            && await ReplaceProjectAsync(activating => _workspace.CreateTemporaryProjectAsync(CancellationToken.None, activating), null));

    // File > Open Project: after the save prompt (if needed), the chosen project file replaces the current project.
    // Opening the current project file again changes nothing.
    public Task<bool> OpenProjectAsync() =>
        RunLifecycleAsync(async () =>
        {
            if (!await ConfirmDiscardAsync(closing: false))
            {
                return false;
            }

            var path = await _dialogs.PickProjectToOpenAsync();
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            path = Path.GetFullPath(path);
            if (string.Equals(path, _workspace.ProjectFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return await ReplaceProjectAsync(activating => _workspace.OpenProjectAsync(path, CancellationToken.None, activating), null);
        });

    // File > Save: Save As for a temporary project; a project file only writes its committed changes into the file.
    public Task<bool> SaveAsync() =>
        RunLifecycleAsync(() => _workspace.IsTemporaryProject ? SaveAsCoreAsync() : CheckpointAsync());

    // File > Save As: the project is copied to a new file, which becomes the current project.
    public Task<bool> SaveAsAsync() => RunLifecycleAsync(SaveAsCoreAsync);

    // Window closing (File > Exit and the title bar): after the save prompt (if needed) the project is closed and the
    // workspace disposed, which deletes temporary storage. Returns whether the window may close.
    public Task<bool> CloseAsync() =>
        RunLifecycleAsync(async () =>
        {
            if (!await ConfirmDiscardAsync(closing: true))
            {
                return false;
            }

            await RetireCurrentSessionAsync();
            _workspace.Dispose();
            _shutDown = true;
            return true;
        }, allowAfterShutdown: true);

    private async Task<bool> RunLifecycleAsync(Func<Task<bool>> operation, bool allowAfterShutdown = false)
    {
        if (!_gate.Wait(0))
        {
            return false;
        }

        try
        {
            if (_shutDown)
            {
                return allowAfterShutdown;
            }

            return await operation();
        }
        finally
        {
            _gate.Release();
        }
    }

    // True to continue with the requested operation.
    private async Task<bool> ConfirmDiscardAsync(bool closing)
    {
        if (!_workspace.IsTemporaryProject || !HasMeaningfulChanges)
        {
            return true;
        }

        return await _dialogs.AskSaveChangesAsync(Project?.CurrentProject?.Name ?? ProjectWorkspace.DefaultProjectName, closing) switch
        {
            SaveChangesChoice.Save => await SaveAsCoreAsync(),
            SaveChangesChoice.Discard => true,
            _ => false
        };
    }

    private async Task<bool> SaveAsCoreAsync()
    {
        var suggestedFileName = _workspace.ProjectFilePath is { } currentFile
            ? Path.GetFileName(currentFile)
            : SuggestedFileName(Project?.CurrentProject?.Name);

        var path = await _dialogs.PickSaveLocationAsync(suggestedFileName);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        path = WithProjectExtension(Path.GetFullPath(path));
        if (File.Exists(path) || Directory.Exists(path))
        {
            await _dialogs.ShowErrorAsync(ProjectStorageMessages.DestinationExists);
            return false;
        }

        // The copy shows the same worksheet that is active now.
        var activeWorksheetId = Project?.SelectedWorksheet?.Id;
        return await ReplaceProjectAsync(activating => _workspace.SaveAsAsync(path, CancellationToken.None, activating), activeWorksheetId);
    }

    private async Task<bool> CheckpointAsync()
    {
        try
        {
            await _workspace.CheckpointAsync(CancellationToken.None);
            return true;
        }
        catch (ProjectStorageException exception)
        {
            await _dialogs.ShowErrorAsync(ProjectStorageMessages.For(exception));
            return false;
        }
    }

    // Switches the workspace to another project session and shows it. The current session is suspended first, so no
    // session operation runs during the switch. The new session's view model is created and loaded while the workspace
    // still keeps the previous session (activating); only then does the workspace retire the previous session. On failure
    // the previous session resumes, unchanged, and the error is shown.
    private async Task<bool> ReplaceProjectAsync(
        Func<Func<ProjectSession, Task>, Task<ProjectSession>> switchWorkspace,
        Guid? preferredWorksheetId)
    {
        var previousSession = _session;
        if (previousSession is not null)
        {
            await previousSession.SuspendAsync(CancellationToken.None);
        }

        MainWindowSession? nextSession = null;
        MainWindowViewModel? nextProject = null;
        try
        {
            await switchWorkspace(async projectSession =>
            {
                nextSession = _composition.CreateMainWindowSession(projectSession, _clipboardReader, _clipboardWriter);
                nextProject = _composition.CreateMainWindowViewModel(nextSession);
                await nextProject.LoadProjectAsync(preferredWorksheetId);
            });
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        {
            previousSession?.Resume();
            await _dialogs.ShowErrorAsync(exception is ProjectStorageException storage
                ? ProjectStorageMessages.For(storage)
                : "The project operation could not be completed.");
            return false;
        }
        catch
        {
            previousSession?.Resume();
            throw;
        }

        if (previousSession is not null)
        {
            previousSession.ProjectModified -= OnProjectModified;
            previousSession.Retire();
        }

        // User-level shell state carries over; everything project-specific comes from the new project.
        nextProject!.IsProjectPanelVisible = Project?.IsProjectPanelVisible ?? true;

        _session = nextSession!;
        _session.ProjectModified += OnProjectModified;
        Project = nextProject;
        HasMeaningfulChanges = false;
        ProjectReplaced?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task RetireCurrentSessionAsync()
    {
        if (_session is null)
        {
            return;
        }

        await _session.SuspendAsync(CancellationToken.None);
        _session.ProjectModified -= OnProjectModified;
        _session.Retire();
    }

    private void OnProjectModified(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _session) && _workspace.IsTemporaryProject)
        {
            HasMeaningfulChanges = true;
        }
    }

    private static bool IsExpectedFailure(Exception exception) =>
        exception is ProjectStorageException or ValidationException or EntityNotFoundException or RawDataStorageException
            or IOException or UnauthorizedAccessException;

    // The suggested file name for an untitled project: its name, without characters a file name cannot contain.
    private static string SuggestedFileName(string? projectName)
    {
        var name = string.IsNullOrWhiteSpace(projectName) ? ProjectWorkspace.DefaultProjectName : projectName.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    // "MyProject" becomes "MyProject.yat"; a name already ending in .yat is kept.
    private static string WithProjectExtension(string path) =>
        path.EndsWith(".yat", StringComparison.OrdinalIgnoreCase) ? path : path + ".yat";
}
