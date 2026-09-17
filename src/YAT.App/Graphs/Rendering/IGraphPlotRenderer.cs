using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// What one graph type draws inside the plot area. The shared frame - background, grid, axes, ticks, labels, titles and
// legend - is drawn by SkiaGraphRenderer for every graph type; only this part differs between a scatter plot, a
// histogram, a probability plot and an empirical CDF.
//
// The implementation is given the canvas already clipped to the plot area and the transform of the axes it was laid out
// with, so it only converts its own values to coordinates and draws them.
public interface IGraphPlotRenderer
{
    void RenderPlot(SKCanvas canvas, GraphCoordinateTransform transform, GraphTheme theme);
}
