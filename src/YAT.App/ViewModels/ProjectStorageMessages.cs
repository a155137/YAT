using YAT.Application.Exceptions;

namespace YAT.app.ViewModels;

// User-facing text for project storage failures. The storage technology's own messages are never shown.
public static class ProjectStorageMessages
{
    public const string DestinationExists = "A file with that name already exists. Please choose another file name.";

    public static string For(ProjectStorageException exception) => exception.Error switch
    {
        ProjectStorageError.NotAYatProject => "The selected file is not a valid YAT project.",
        ProjectStorageError.UnsupportedSchemaVersion => "This project was created by a newer version of YAT.",
        ProjectStorageError.CannotOpen => "The project could not be opened.",
        ProjectStorageError.DestinationExists => DestinationExists,
        ProjectStorageError.DestinationInUse => "The destination file is currently in use by another process and could not be replaced.",
        ProjectStorageError.DestinationUncertain => "The selected destination cannot be safely overwritten because it is a folder, a symbolic link, or has an unrecovered journal (.wal).",
        ProjectStorageError.DestinationRestoreFailed => exception.RecoveryBackupPath is { Length: > 0 } backupPath
            ? $"Replacing the destination failed and the original file could not be restored automatically. Do not delete the preserved backup file:\n{backupPath}"
            : "Replacing the destination failed and the original file could not be restored automatically. Do not delete any preserved .bak backup file.",
        ProjectStorageError.CopyFailed => "The project could not be saved to the selected location.",
        _ => "The project operation could not be completed."
    };
}
