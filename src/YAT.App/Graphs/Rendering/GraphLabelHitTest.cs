using SkiaSharp;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Where one title of a graph is drawn: the box its line of text takes, and the area of the layout the title belongs to
// (the title band, or the X or Y axis area). Made by SkiaGraphRenderer.LabelGeometry, in the canvas's coordinates.
public sealed record GraphLabelGeometry(GraphLabelField Field, SKRect Text, SKRect Area);

// Which title of a graph a point is on. A title is picked within a few pixels of its text, never beyond the area it
// belongs to, so a point on the plot, a tick label, the legend, the statistics panel or a reference line label never
// picks one. A title the graph does not show has no geometry and is never picked.
public static class GraphLabelHitTest
{
    // How far around its text a title can still be picked.
    public const float Padding = 3f;

    public static GraphLabelField? Find(IReadOnlyList<GraphLabelGeometry> labels, SKPoint point)
    {
        ArgumentNullException.ThrowIfNull(labels);

        foreach (var label in labels)
        {
            if (HitArea(label).Contains(point))
            {
                return label.Field;
            }
        }

        return null;
    }

    // The text box grown by the padding, cut back to the title's own area.
    public static SKRect HitArea(GraphLabelGeometry label)
    {
        ArgumentNullException.ThrowIfNull(label);

        var padded = label.Text;
        padded.Inflate(Padding, Padding);
        return SKRect.Intersect(padded, label.Area);
    }
}
