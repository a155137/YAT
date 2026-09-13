namespace YAT.Application.Exceptions;

// Unexpected failure of the raw data storage technology. The technology-specific exception, if any,
// is preserved as InnerException so storage types never cross the Infrastructure boundary.
public sealed class RawDataStorageException : Exception
{
    public RawDataStorageException(string message)
        : base(message)
    {
    }

    public RawDataStorageException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
