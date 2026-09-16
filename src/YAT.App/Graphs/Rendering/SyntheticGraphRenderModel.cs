namespace YAT.app.Graphs.Rendering;

// The sample graph Task #026 renders: a complete render model with no data behind it, used to prove the rendering path
// (window, layout, axes, ticks, labels, theme, resizing) while no graph type computes a model yet.
//
// Task #027 replaces it with the scatter plot's real model; nothing else in the rendering path changes when it does.
public static class SyntheticGraphRenderModel
{
    public const string Title = "Sample Graph";

    public static GraphRenderModel Create()
    {
        var x = new GraphAxisRange(0, 100);
        var y = new GraphAxisRange(0, 500);

        return new GraphRenderModel(
            Title,
            new GraphAxisModel(x, GraphAxisTicks.Evenly(x), "X Axis"),
            new GraphAxisModel(y, GraphAxisTicks.Evenly(y), "Y Axis"));
    }
}
