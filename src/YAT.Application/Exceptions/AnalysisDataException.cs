namespace YAT.Application.Exceptions;

public enum AnalysisDataError
{
    // The configuration cannot be used (no variable, a duplicate variable, a column with the wrong data type, ...).
    InvalidConfiguration,

    // The worksheet of the configuration no longer exists.
    WorksheetUnavailable,

    // A selected column is no longer part of the worksheet (e.g. it was deleted).
    ColumnUnavailable,

    // The worksheet has more rows than one analysis can hold in memory.
    WorksheetTooLarge,

    // The worksheet values could not be read.
    DataReadFailed
}

// An analysis's data could not be produced. The storage technology's own exception, if any, is preserved as
// InnerException only; its message is not part of this contract.
public sealed class AnalysisDataException : Exception
{
    public AnalysisDataException(AnalysisDataError error, string message)
        : base(message)
    {
        Error = error;
    }

    public AnalysisDataException(AnalysisDataError error, string message, Exception innerException)
        : base(message, innerException)
    {
        Error = error;
    }

    public AnalysisDataError Error { get; }
}
