namespace YAT.Application.Exceptions;

public enum GraphDataError
{
    // The configuration does not satisfy its graph specification (a required role is missing, a role is unsupported,
    // a column has the wrong data type, ...).
    InvalidConfiguration,

    // The graph type is not one this version of YAT knows.
    UnsupportedGraphType,

    // The worksheet of the configuration no longer exists.
    WorksheetUnavailable,

    // An assigned column is no longer part of the worksheet (e.g. it was deleted).
    ColumnUnavailable,

    // The worksheet values could not be read.
    DataReadFailed
}

// A graph's data could not be produced. The storage technology's own exception, if any, is preserved as InnerException
// only; its message is not part of this contract.
public sealed class GraphDataException : Exception
{
    public GraphDataException(GraphDataError error, string message)
        : base(message)
    {
        Error = error;
    }

    public GraphDataException(GraphDataError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public GraphDataError Error { get; }
}
