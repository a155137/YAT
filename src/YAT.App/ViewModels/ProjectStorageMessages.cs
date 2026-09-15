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
        ProjectStorageError.CopyFailed => "The project could not be saved to the selected location.",
        _ => "The project operation could not be completed."
    };
}
