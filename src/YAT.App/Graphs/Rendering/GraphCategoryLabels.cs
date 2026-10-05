namespace YAT.app.Graphs.Rendering;

// The categories of a categorical axis - a box plot's variables and groups - that are labelled where the axis is drawn
// (Task #060). Every category keeps its slot and its box; only the labels, with their tick marks and grid lines, are
// thinned where the axis is too short to show them all apart: every second, third, ... category from the first, the
// smallest step at which each label stands clear of the next. Where they all fit, all are shown.
//
// Presentation only, decided each time the graph is drawn from the room it is drawn in - on screen, after a resize, in
// a copy or an export - and never stored: the model, its categories and the graph's settings stay as they are. Any
// other axis is drawn with all of its ticks, as it always was.
internal static class GraphCategoryLabels
{
    // The ticks to draw. place: a tick's position on screen; width: how wide its label is drawn; gap: the least room
    // between two labels.
    public static IReadOnlyList<GraphAxisTick> Shown(GraphAxisModel axis, Func<double, float> place, Func<string, float> width, float gap)
    {
        ArgumentNullException.ThrowIfNull(axis);
        ArgumentNullException.ThrowIfNull(place);
        ArgumentNullException.ThrowIfNull(width);

        var ticks = axis.Ticks;
        if (axis.Scale != GraphAxisScale.Categorical || ticks.Count < 2)
        {
            return ticks;
        }

        var positions = ticks.Select(tick => place(tick.Value)).ToArray();
        var widths = ticks.Select(tick => width(tick.Label)).ToArray();
        for (var step = 1; step < ticks.Count; step++)
        {
            if (Fits(positions, widths, step, gap))
            {
                return step == 1 ? ticks : [.. ticks.Where((_, index) => index % step == 0)];
            }
        }

        return [ticks[0]];
    }

    // Whether every step-th label, from the first, stands at least gap clear of the next one kept.
    private static bool Fits(float[] positions, float[] widths, int step, float gap)
    {
        for (var index = step; index < positions.Length; index += step)
        {
            var previous = index - step;
            if (Math.Abs(positions[index] - positions[previous]) < ((widths[index] + widths[previous]) / 2) + gap)
            {
                return false;
            }
        }

        return true;
    }
}
