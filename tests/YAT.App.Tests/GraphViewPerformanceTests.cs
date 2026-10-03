using System.Diagnostics;
using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Enums;

namespace YAT.App.Tests;

// What one step of zooming or panning costs on the largest scatter plot a graph draws (Task #055): a hundred thousand
// points (DisplaySampling.DefaultMaximumRenderedPoints), three groups. A step is the new view put on the presented graph
// (GraphViewController) and the frame drawn again at a graph window's size. Explicit - a normal run skips it:
//
//     YAT.App.Tests.exe -explicit only -class YAT.App.Tests.GraphViewPerformanceTests
//
// Timings are written to the test output for information; nothing here passes or fails on time.
public sealed class GraphViewPerformanceTests
{
    private const int Points = DisplaySampling.DefaultMaximumRenderedPoints;
    private const int Steps = 40;

    private static readonly SKRect Bounds = new(0, 0, 760, 488);

    private static void Report(string message)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        Console.WriteLine(message);
    }

    [Fact(Explicit = true)]
    public void ZoomAndPanStepsOnAHundredThousandPoints()
    {
        GraphColumnInfo Column(string name, WorksheetDataType type = WorksheetDataType.Numeric) => new(Guid.Empty, name, type);
        var random = new Random(55);
        var x = Enumerable.Range(0, Points).Select(_ => 15 + ((random.NextDouble() - 0.5) * 0.4)).ToArray();
        var y = Enumerable.Range(0, Points).Select(_ => 14.9 + ((random.NextDouble() - 0.5) * 0.4)).ToArray();
        var lots = new StringGroupData(Column("Lot", WorksheetDataType.String), Enumerable.Range(0, Points).Select(i => (string?)$"Lot {i % 3}").ToArray());
        var data = new ScatterGraphData(Guid.Empty, Column("Reg1"), Column("Reg2"), x, y, lots);
        var model = new ScatterRenderModelBuilder().Build(data, new ScatterPlotLabels("Reg1", "Reg2", "Lot"), TestContext.Current.CancellationToken)!;
        var plot = new ScatterRenderer(model);
        var graph = GraphPresentation.Present(model.Frame, data, new GraphConfiguration(GraphType.ScatterPlot, Guid.Empty, []), TestContext.Current.CancellationToken);
        var controller = new GraphViewController(graph);
        var renderer = new SkiaGraphRenderer();
        using var bitmap = new SKBitmap((int)Bounds.Width, (int)Bounds.Height);
        using var canvas = new SKCanvas(bitmap);
        var plotArea = SkiaGraphRenderer.Layout(graph.Frame, Bounds, GraphThemes.Light).PlotArea;

        double Draw()
        {
            var watch = Stopwatch.StartNew();
            renderer.Render(canvas, controller.Graph.Frame, Bounds, GraphThemes.Light, plot);
            return watch.Elapsed.TotalMilliseconds;
        }

        Draw();
        var view = new List<double>();
        var draw = new List<double>();
        for (var step = 0; step < Steps; step++)
        {
            var watch = Stopwatch.StartNew();
            if (step < Steps / 2)
            {
                controller.ZoomAt(null, plotArea, new SKPoint(plotArea.MidX + step, plotArea.MidY), step % 2 == 0 ? 1 : -0.5);
            }
            else
            {
                if (step == Steps / 2)
                {
                    controller.BeginPan(plotArea, new SKPoint(plotArea.MidX, plotArea.MidY));
                }

                controller.PanTo(new SKPoint(plotArea.MidX + (step * 3), plotArea.MidY - step));
            }

            view.Add(watch.Elapsed.TotalMilliseconds);
            draw.Add(Draw());
        }

        Assert.False(controller.Graph.ViewOptions.IsDefault);
        double Median(List<double> values) => values.Order().ElementAt(values.Count / 2);
        Report($"Points: {Points:N0}; {Steps} zoom/pan steps at {Bounds.Width:F0} x {Bounds.Height:F0}.");
        Report($"New view (presentation only): median {Median(view):F2} ms, max {view.Max():F2} ms");
        Report($"Redraw (whole frame and plot): median {Median(draw):F1} ms, max {draw.Max():F1} ms");
    }
}
