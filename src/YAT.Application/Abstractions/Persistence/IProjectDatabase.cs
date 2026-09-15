namespace YAT.Application.Abstractions.Persistence;

// The storage of one open project: a single database file holding the project's metadata and raw data. Repositories
// and the raw data store of a project session all work against it. Disposing it closes the file; the file stays.
public interface IProjectDatabase : IDisposable
{
    // Full path of the database file. For a temporary project it lies in private temporary storage, never shown to users.
    string FilePath { get; }

    // True for an untitled project kept in temporary storage; false for a project file (.yat) chosen by the user.
    bool IsTemporary { get; }

    // Writes all committed changes into the database file itself (the future File > Save).
    Task CheckpointAsync(CancellationToken cancellationToken);

    // Creates a complete, validated copy of this project at destinationPath, which must not exist yet. This database
    // stays open, unchanged and usable, whether the copy succeeds or fails (ProjectStorageException).
    Task SaveCopyAsync(string destinationPath, CancellationToken cancellationToken);

    // Deletes the storage of a temporary project once it has been disposed. Not allowed for project files or while open.
    void DeleteTemporaryStorage();
}
