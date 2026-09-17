using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs.Export;

// Everything an export draws, fixed at the moment the user asked for it: the graph frame, the plot renderer of its
// graph type and the theme the graph was being shown in.
//
// The snapshot is taken on the UI thread and is immutable, so the export work that follows never reads the window, the
// canvas or the current theme variant again: a theme switch or a resize while a file is being written cannot change
// what was exported. Nothing here can reach worksheet data either - the graph was already prepared - so exporting
// re-draws the graph rather than re-reading, re-sampling or re-counting anything.
public sealed record GraphExportSnapshot
{
    public GraphExportSnapshot(GraphRenderModel frame, IGraphPlotRenderer? plot, GraphTheme theme)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(theme);
        Frame = frame;
        Plot = plot;
        Theme = theme;
    }

    public GraphRenderModel Frame { get; }

    // What the graph type draws inside the plot area; null for a graph frame with nothing in it.
    public IGraphPlotRenderer? Plot { get; }

    public GraphTheme Theme { get; }
}
