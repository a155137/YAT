using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Puts the user's statistics options on a presented frame (Task #045): the first of the steps a graph window can
// change afterwards, before the legend, the axis ranges and the labels.
//
// It decides two things and nothing else: whether the frame keeps the statistics panel its graph was given, and which
// of the panel's statistics are shown. Hide gives a frame without a panel, so the layout gives its room back to the
// plot; Auto and Show keep the panel - Show never makes one up for a frame that has none. The panel's rows, their
// numbers and texts, their order and their series indexes are the ones worked out from the data, never again: only
// which of them are drawn changes. The plot model, the legend, the lines and the axes are never touched.
//
// With every statistic shown - the default - it returns the very frame it was given, and so it does for a panel hidden
// on a frame that has none.
public static class GraphStatisticsPresentationBuilder
{
    public static GraphRenderModel Attach(
        GraphRenderModel frame,
        GraphTypeDefinition definition,
        GraphStatisticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(options);

        if (!definition.Supports(GraphCapability.StatisticsPanel))
        {
            return frame;
        }

        if (!options.IsValid)
        {
            throw new ArgumentException(
                "The statistics options are not valid; validate the configuration first.", nameof(options));
        }

        if (frame.StatisticsPanel is not { } panel)
        {
            return frame;
        }

        if (options.Mode == GraphStatisticsMode.Hide)
        {
            return frame.WithStatisticsPanel(null);
        }

        var items = options.Items;
        return panel.Items.SequenceEqual(items) ? frame : frame.WithStatisticsPanel(panel.WithItems(items));
    }
}
