using YAT.Application.Graphs;

namespace YAT.app.Graphs;

// Whether the values of a graph span a range a graph can be drawn over (Task #063.1). Every value is finite, but the
// distance between the smallest and the largest can still be beyond a double (-1e308 and 1e308): no axis can cover it
// and no bin can divide it. Such a graph is refused with a message the user can act on, before any builder works on
// it, instead of failing inside one.
//
// The values one axis covers are checked together: all the variables of a graph drawn together (they share the value
// axis), and the X and the Y of a scatter plot each on their own.
public static class GraphDataRange
{
    public const string TooLargeMessage = "Data range is too large to draw this graph.";

    // Throws GraphPreparationException when the values of one axis of this graph span more than a double holds.
    public static void EnsureDrawable(GraphData data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var drawable = data switch
        {
            ScatterGraphData scatter => Fits([scatter.XValues]) && Fits([scatter.YValues]),
            MultiVariableGraphData several => Fits([.. several.Variables.Select(variable => variable.Values)]),
            UnivariateGraphData univariate => Fits([univariate.Values]),
            _ => true
        };

        if (!drawable)
        {
            throw new GraphPreparationException(TooLargeMessage);
        }
    }

    // Values that are not finite are skipped, as every graph skips them.
    private static bool Fits(IReadOnlyList<ReadOnlyMemory<double>> columns)
    {
        var minimum = double.PositiveInfinity;
        var maximum = double.NegativeInfinity;
        foreach (var column in columns)
        {
            foreach (var value in column.Span)
            {
                if (double.IsFinite(value))
                {
                    minimum = Math.Min(minimum, value);
                    maximum = Math.Max(maximum, value);
                }
            }
        }

        return minimum > maximum || double.IsFinite(maximum - minimum);
    }
}
