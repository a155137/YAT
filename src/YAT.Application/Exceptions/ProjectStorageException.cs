namespace YAT.Application.Exceptions;

// A failure to create, open, copy or use project storage, classified by Error. The technology-specific exception, if
// any, is preserved as InnerException only; its message is not part of this contract.
public sealed class ProjectStorageException : Exception
{
    public ProjectStorageException(ProjectStorageError error, string message)
        : base(message)
    {
        Error = error;
    }

    public ProjectStorageException(ProjectStorageError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public ProjectStorageError Error { get; }
}
