namespace YAT.app.Graphs;

// A graph that cannot be prepared for a reason the user can act on, known only once the data is there - a histogram's
// bin width that would need too many bins for this data, for example. The message is written for the user and is shown
// as it is; the graph preparation reports it instead of the generic "could not be drawn", and opens no window.
//
// Only for expected, explained refusals. A defect in a builder is still any other exception, contained and traced as
// before.
public sealed class GraphPreparationException : Exception
{
    public GraphPreparationException(string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
    }
}
