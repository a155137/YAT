namespace YAT.app.Graphs.Rendering;

// The series of a graph drawn in panels, in the order the whole graph has them (Task #058). Each panel is built from its
// own observations, where a group may be missing or first seen later than in the whole graph; built against this order,
// a series keeps the index - and so the colour - the whole graph gives it, in every panel.
//
// A builder without one numbers its series in the order it finds them, as it always did.
public sealed class GraphSeriesOrder
{
    private readonly Dictionary<string, int> _indexes = new(StringComparer.Ordinal);

    // The series labels in the whole graph's order; a repeated label keeps its first place.
    public GraphSeriesOrder(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        foreach (var label in labels)
        {
            _indexes.TryAdd(label, _indexes.Count);
        }
    }

    public int Count => _indexes.Count;

    // The index of a series in the whole graph, or fallback for one the whole graph does not have.
    public int IndexOf(string label, int fallback) => _indexes.TryGetValue(label, out var index) ? index : fallback;

    // A panel's series - or legend entries - in the whole graph's order, so every panel draws its series in the same order,
    // one on top of another the same way, whichever group its own rows show first. Equal indexes keep their order.
    public List<T> Arrange<T>(List<T> items, Func<T, int> seriesIndex) => [.. items.OrderBy(seriesIndex)];
}
