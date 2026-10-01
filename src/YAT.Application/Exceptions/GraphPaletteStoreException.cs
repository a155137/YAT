namespace YAT.Application.Exceptions;

// The user's graph palettes could not be saved (Task #050). The file system's own exception is preserved as
// InnerException. What was kept before is still there, as it was.
public sealed class GraphPaletteStoreException : Exception
{
    public GraphPaletteStoreException(string message)
        : base(message)
    {
    }

    public GraphPaletteStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
