namespace YAT.app.Composition;

// An operation was requested on a MainWindowSession whose project has been closed (replaced by New, Open or Save As, or
// closed on exit). Nothing was written to any project.
public sealed class ProjectSessionClosedException : InvalidOperationException
{
    public ProjectSessionClosedException()
        : base("The project has been closed.")
    {
    }
}
