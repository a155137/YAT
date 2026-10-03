namespace YAT.Application.Exceptions;

public enum RowFilterError
{
    // The column is no longer part of the worksheet (e.g. it was deleted).
    ColumnUnavailable,

    // The column cannot filter rows: it is neither Numeric nor String.
    ColumnCannotFilter,

    // The column's values could not be read.
    DataReadFailed
}

// What a row filter needs from a worksheet could not be produced (Task #053) - for graphs and analyses alike. The storage
// technology's own exception, if any, is preserved as InnerException only; its message is not part of this contract.
public sealed class RowFilterException : Exception
{
    public RowFilterException(RowFilterError error, string message)
        : base(message)
    {
        Error = error;
    }

    public RowFilterException(RowFilterError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public RowFilterError Error { get; }
}
