using YAT.Application.Abstractions.Persistence;
using YAT.Application.Features.Projects.CreateProject;
using YAT.Application.Features.Worksheets.CreateWorksheet;

namespace YAT.app.Composition;

// Project lifecycle without UI: holds the one current persistent ProjectSession and creates, opens, copies and closes
// projects. Replacing the current session always builds and validates the new session first; only then is the
// previous one disposed (and its temporary storage deleted). A failed operation leaves the current session current and
// usable.
//
// Lifecycle operations run one at a time. The workspace itself does not know the UI: ProjectLifecycleController suspends
// the MainWindowSession working on the current session around a switch and rebinds the UI through activating.
public sealed class ProjectWorkspace : IDisposable
{
    public const string DefaultProjectName = "Untitled Project";

    public const string DefaultWorksheetName = "Sheet1";

    private readonly CompositionRoot _composition;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal ProjectWorkspace(CompositionRoot composition)
    {
        _composition = composition;
    }

    public ProjectSession? CurrentSession { get; private set; }

    // True while the current project is untitled and kept in temporary storage.
    public bool IsTemporaryProject => CurrentSession?.Database?.IsTemporary ?? false;

    // The current project file, or null for a temporary project or when no project is open. Runtime state only: the
    // path is never stored in the project itself.
    public string? ProjectFilePath => CurrentSession?.Database is { IsTemporary: false } database ? database.FilePath : null;

    // The lifecycle operations below take an optional activating callback, see ReplaceCurrentAsync.

    // A new untitled project in temporary storage, "Untitled Project" with "Sheet1", which becomes the current session.
    public Task<ProjectSession> CreateTemporaryProjectAsync(
        CancellationToken cancellationToken,
        Func<ProjectSession, Task>? activating = null) =>
        ReplaceCurrentAsync(async () =>
        {
            var session = _composition.CreateTemporaryProjectSession();
            await InitializeOrDiscardAsync(session, DefaultProjectName, cancellationToken);
            return session;
        }, activating, cancellationToken);

    // A new project file at filePath (which must not exist) holding a project named after the file, with "Sheet1".
    // It becomes the current session.
    public Task<ProjectSession> CreateProjectAsync(
        string filePath,
        CancellationToken cancellationToken,
        Func<ProjectSession, Task>? activating = null) =>
        ReplaceCurrentAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            var session = _composition.CreateProjectFileSession(filePath);
            var projectName = Path.GetFileNameWithoutExtension(filePath);
            await InitializeOrDiscardAsync(session, string.IsNullOrWhiteSpace(projectName) ? DefaultProjectName : projectName, cancellationToken);
            return session;
        }, activating, cancellationToken);

    // Opens an existing project file as the current session (ProjectStorageException when it cannot be opened, is not a
    // YAT project or was saved by a newer version).
    public Task<ProjectSession> OpenProjectAsync(
        string filePath,
        CancellationToken cancellationToken,
        Func<ProjectSession, Task>? activating = null) =>
        ReplaceCurrentAsync(() =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            return _composition.OpenProjectFileSessionAsync(filePath, cancellationToken);
        }, activating, cancellationToken);

    // Writes the current project's committed changes into its database file (File > Save of a project file).
    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await CurrentDatabase().CheckpointAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    // Save As: copies the current project to destinationPath (a new file), opens the copy and makes it the current
    // session. Order: create and validate the copy → open it → activating → switch → dispose the previous session →
    // delete the previous temporary storage. If creating or opening the copy (or activating) fails, the previous session
    // stays current.
    public Task<ProjectSession> SaveAsAsync(
        string destinationPath,
        CancellationToken cancellationToken,
        Func<ProjectSession, Task>? activating = null) =>
        ReplaceCurrentAsync(async () =>
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
            await CurrentDatabase().SaveCopyAsync(destinationPath, cancellationToken);
            return await _composition.OpenProjectFileSessionAsync(destinationPath, cancellationToken);
        }, activating, cancellationToken);

    // Closes the current project: its database is closed and temporary storage is deleted.
    public async Task CloseProjectAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Retire(CurrentSession);
            CurrentSession = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Retire(CurrentSession);
            CurrentSession = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // activating (optional) runs once the new session exists, before it becomes current: e.g. to bind and load the UI for
    // it. The previous session is still current and untouched meanwhile. If activating fails, the new session is
    // discarded (and its temporary storage deleted) and the previous session stays current.
    private async Task<ProjectSession> ReplaceCurrentAsync(
        Func<Task<ProjectSession>> createSession,
        Func<ProjectSession, Task>? activating,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var next = await createSession();
            if (activating is not null)
            {
                try
                {
                    await activating(next);
                }
                catch
                {
                    Retire(next);
                    throw;
                }
            }

            var previous = CurrentSession;
            CurrentSession = next;
            Retire(previous);
            return next;
        }
        finally
        {
            _gate.Release();
        }
    }

    // Stores the default project and worksheet through the Application handlers; a session that fails to initialize is
    // closed and its storage removed.
    private async Task InitializeOrDiscardAsync(ProjectSession session, string projectName, CancellationToken cancellationToken)
    {
        try
        {
            var project = await new CreateProjectHandler(session.Projects, _composition.TimeProvider)
                .HandleAsync(new CreateProjectCommand(projectName, null), cancellationToken);
            await new CreateWorksheetHandler(session.Projects, session.Worksheets, _composition.TimeProvider)
                .HandleAsync(new CreateWorksheetCommand(project.Id, DefaultWorksheetName), cancellationToken);
            session.ProjectId = project.Id;
        }
        catch
        {
            var database = session.Database!;
            Retire(session);
            if (!database.IsTemporary)
            {
                TryDeleteProjectFile(database.FilePath);
            }

            throw;
        }
    }

    private IProjectDatabase CurrentDatabase() =>
        CurrentSession?.Database ?? throw new InvalidOperationException("No project is open.");

    // Disposes a session that is no longer current, then deletes its storage if it was temporary. Cleanup failures of
    // temporary storage are ignored: the project is already closed and nothing references its files any more.
    private static void Retire(ProjectSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.Dispose();
        if (session.Database is { IsTemporary: true } database)
        {
            try
            {
                database.DeleteTemporaryStorage();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDeleteProjectFile(string filePath)
    {
        foreach (var path in new[] { filePath, filePath + ".wal" })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
