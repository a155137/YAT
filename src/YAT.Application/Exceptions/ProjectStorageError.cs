namespace YAT.Application.Exceptions;

public enum ProjectStorageError
{
    // The file is not a YAT project: not a database, or a database without valid YAT project metadata.
    NotAYatProject,

    // A YAT project written by a newer version of YAT, with a schema this version does not support.
    UnsupportedSchemaVersion,

    // The project file could not be opened or created, e.g. it does not exist, its folder is missing or it is locked.
    CannotOpen,

    // The destination of a new project or project copy already exists; it is never overwritten.
    DestinationExists,

    // The destination file is currently in use or locked by another process.
    DestinationInUse,

    // The destination target cannot be safely overwritten (e.g. directory, reparse point, or unrecovered WAL journal).
    DestinationUncertain,

    // Replacing the destination failed and automatic restoration from the preserved backup also failed.
    DestinationRestoreFailed,

    // Copying a project to a new file failed; the source project is unchanged.
    CopyFailed,

    // A project file holds exactly one project; adding a different one was rejected.
    ProjectAlreadyExists,

    // Any other failure of the project storage technology.
    StorageFailure
}
