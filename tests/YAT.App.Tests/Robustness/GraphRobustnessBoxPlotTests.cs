using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// #047: the box plot options over the whole named corpus - with the case's own groups - at the narrowest, default and
// widest box, with the means or the outliers or both unmarked, in the light and the dark theme. Each option is put on
// the drawn box plot the way Edit Box Plot... puts it; then:
//
//   * the options change nothing but the drawing: the very boxes, categories and frame, and a box plot built with
//     them has the same boxes, categories and axes as one built without;
//   * the default draws and exports exactly what a box plot without options draws;
//   * every box plot draws, and exports.
public sealed class GraphRobustnessBoxPlotTests
{
    public static TheoryData<string> Cases => GraphRobustnessNamedTests.UnivariateCases;

    private static readonly BoxPlotOptions[] Options =
    [
        new(BoxPlotOptions.MinimumBoxWidthPercent),
        new(BoxPlotOptions.MaximumBoxWidthPercent),
        new(ShowMean: false),
        new(ShowOutliers: false),
        new(BoxPlotOptions.MinimumBoxWidthPercent, false, false),
        BoxPlotOptions.Default
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryBoxPlotDrawsWithEveryOption(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var token = TestContext.Current.CancellationToken;
        var data = (MultiVariableGraphData)RobustnessGraphs.DataFor(RobustnessGraph.BoxPlot, robustnessCase);
        var built = RobustnessGraphs.Build(RobustnessGraph.BoxPlot, data, cancellationToken: token);
        if (built.Model is not BoxPlotRenderModel model)
        {
            return;
        }

        var labels = new BoxPlotLabels(
            [.. data.Variables.Select(variable => variable.Variable.Name)],
            data.Variables.Select(variable => variable.Group?.Column.Name).FirstOrDefault(group => group is not null));
        var plain = RobustnessGraphs.ExportPng(built, GraphThemes.Light);
        var themes = 0;

        foreach (var options in Options)
        {
            var where = robustnessCase.Describe($"Box Plot {options}");
            var edited = model with { Options = options };
            GraphRobustnessInvariants.That(
                ReferenceEquals(edited.Boxes, model.Boxes) && ReferenceEquals(edited.Frame, model.Frame)
                && ReferenceEquals(edited.Categories, model.Categories) && Equals(edited.Options, options),
                where, "other options changed the boxes or the frame");

            var rebuilt = new BoxPlotRenderModelBuilder().Build(data, labels, options, token)!;
            GraphRobustnessInvariants.That(
                rebuilt.Categories.SequenceEqual(model.Categories)
                && rebuilt.Frame.XAxis.Range == model.Frame.XAxis.Range && rebuilt.Frame.YAxis.Range == model.Frame.YAxis.Range
                && rebuilt.Frame.YAxis.Ticks.SequenceEqual(model.Frame.YAxis.Ticks)
                && rebuilt.OutlierCount == model.OutlierCount && rebuilt.RenderedOutlierCount == model.RenderedOutlierCount
                && rebuilt.Boxes.Select(Values).SequenceEqual(model.Boxes.Select(Values)),
                where, "a box plot built with the options is not the box plot built without them");

            var drawn = built with { Plot = new BoxPlotRenderer(edited) };
            try
            {
                RobustnessGraphs.Render(drawn, themes++ % 2 == 0 ? GraphThemes.Light : GraphThemes.Dark);
                var png = RobustnessGraphs.ExportPng(drawn, GraphThemes.Light);
                if (options == BoxPlotOptions.Default)
                {
                    GraphRobustnessInvariants.That(png.AsSpan().SequenceEqual(plain), where, "the default does not draw the box plot as it was");
                }
            }
            catch (Exception exception)
            {
                Assert.Fail($"{where}\n  rendering threw: {exception}");
            }
        }
    }

    private static (double, double, double, double, double, double, int, int) Values(BoxPlotBoxRenderModel box) =>
        (box.LowerWhisker, box.FirstQuartile, box.Median, box.ThirdQuartile, box.UpperWhisker, box.Mean, box.ObservationCount, box.OutlierCount);
}
