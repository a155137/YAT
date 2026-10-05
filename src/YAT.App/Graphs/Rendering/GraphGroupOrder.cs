namespace YAT.app.Graphs.Rendering;

// The order a graph's groups are drawn in (Task #059): one order for its legend, its colours and which series is drawn
// over which, since all three follow a series' place. Groups of a text column keep the order they are first seen in the
// rows; groups of a numeric column go from the smallest value up, whatever order the rows hold them in, with the
// "(Missing)" group of rows without a value last. Every builder that splits observations by group puts the groups it
// found in this order, so the graph types, the statistics panel and the panels of a graph (Task #058) agree.
//
// Only the groups found are ordered - a group is still one with observations the graph keeps - and the order is the same
// for the same groups however the rows were filtered or sorted.
public static class GraphGroupOrder
{
    // The groups found, in the order they are drawn. byNumber holds each number's group, and is null unless the groups
    // were numbers; missing is the group of rows without a value, if there was one. Without numbers nothing moves.
    public static List<T> Arrange<T>(List<T> found, IReadOnlyDictionary<double, T>? byNumber, T? missing)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(found);
        if (byNumber is null)
        {
            return found;
        }

        List<T> ordered = [.. byNumber.OrderBy(entry => entry.Key).Select(entry => entry.Value)];
        if (missing is not null)
        {
            ordered.Add(missing);
        }

        return ordered;
    }

    // The same for groups found as places 0..count-1 (byNumber: each number's place, missing: the missing group's place
    // or -1): the places in the order they are drawn.
    public static int[] Order(int count, IReadOnlyDictionary<double, int>? byNumber, int missing)
    {
        if (byNumber is null)
        {
            return [.. Enumerable.Range(0, count)];
        }

        int[] ordered = [.. byNumber.OrderBy(entry => entry.Key).Select(entry => entry.Value), .. missing >= 0 ? [missing] : Array.Empty<int>()];
        return ordered;
    }
}
