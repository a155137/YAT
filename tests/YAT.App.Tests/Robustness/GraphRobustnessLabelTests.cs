using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// #040: the graph labels over the whole named corpus, every graph a case applies to, with no specification and with one
// that widens the X axis. All Auto keeps the very frame; Custom and Hidden change the three strings and nothing else -
// ranges, ticks, legend, statistics panel and lines stay the frame's own - give Hidden labels' room to the plot, and
// draw in both themes, Chinese, Japanese and Korean labels (#040.1) included.
public sealed class GraphRobustnessLabelTests
{
    private static readonly GraphLabelOptions AllCustom = new(
        GraphLabelOption.Custom("Wafer thickness by site"),
        GraphLabelOption.Custom("Thickness (um)"),
        GraphLabelOption.Custom("Wafers"));

    private static readonly GraphLabelOptions AllHidden = new(GraphLabelOption.Hidden, GraphLabelOption.Hidden, GraphLabelOption.Hidden);

    // A title far wider than any canvas and an axis title with line breaks, next to a label left on Auto.
    private static readonly GraphLabelOptions Mixed = new(
        GraphLabelOption.Custom(string.Join(" ", Enumerable.Repeat("Very long custom title", 30))),
        GraphLabelOption.Hidden,
        GraphLabelOption.Custom(" Thickness\r\n(um)\t"));

    // #040.1: labels the graph font has no glyphs for - Chinese, Japanese, Korean, full width and a symbol - next to
    // characters it has, drawn through the fallback fonts.
    private static readonly GraphLabelOptions Cjk = new(
        GraphLabelOption.Custom("晶圓厚度 直方圖 / Lot 12"),
        GraphLabelOption.Custom("PS 感度 (µA) ⌀ ＡＢ"),
        GraphLabelOption.Custom("片數 ロット 로트"));

    public static TheoryData<string> Cases =>
        [.. RobustnessCorpus.Univariate.Concat(RobustnessCorpus.Paired).Select(robustnessCase => robustnessCase.Name)];

    [Theory]
    [MemberData(nameof(Cases))]
    public void LabelsChangeOnlyTheirOwnStrings(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        var widening = RobustnessCorpus.Specifications(robustnessCase).FirstOrDefault(item => item.Name == "outside-both").Specification;

        foreach (var graph in RobustnessGraphs.For(robustnessCase))
        {
            var definition = GraphTypeDefinitions.For(RobustnessGraphs.TypeOf(graph));
            var data = RobustnessGraphs.DataFor(graph, robustnessCase);

            foreach (var specification in new[] { Specification.None, widening ?? Specification.None })
            {
                var built = RobustnessGraphs.Build(graph, data, cancellationToken: TestContext.Current.CancellationToken, specification: specification);
                if (built.Frame is not { } frame)
                {
                    continue;
                }

                var context = $"{robustnessCase.Describe(RobustnessGraphs.Name(graph))} specification={specification}";

                Assert.True(ReferenceEquals(frame, GraphLabelsBuilder.Attach(frame, definition, GraphLabelOptions.Default)),
                    $"{context}\n  all Auto did not keep the frame");

                foreach (var (labelsName, labels, theme) in new[]
                         {
                             ("custom", AllCustom, GraphThemes.Light),
                             ("hidden", AllHidden, GraphThemes.Dark),
                             ("mixed", Mixed, GraphThemes.Light),
                             ("cjk", Cjk, GraphThemes.Dark)
                         })
                {
                    var where = $"{context} labels={labelsName}";
                    var labelled = GraphLabelsBuilder.Attach(frame, definition, labels);

                    Assert.True(labelled.Title == GraphLabelRules.Resolve(labels.Title, frame.Title), $"{where}\n  title '{labelled.Title}'");
                    Assert.True(labelled.XAxis.Title == GraphLabelRules.Resolve(labels.XAxisTitle, frame.XAxis.Title), $"{where}\n  X title '{labelled.XAxis.Title}'");
                    Assert.True(labelled.YAxis.Title == GraphLabelRules.Resolve(labels.YAxisTitle, frame.YAxis.Title), $"{where}\n  Y title '{labelled.YAxis.Title}'");

                    Assert.True(labelled.XAxis.Range == frame.XAxis.Range && ReferenceEquals(labelled.XAxis.Ticks, frame.XAxis.Ticks),
                        $"{where}\n  the X axis changed beyond its title");
                    Assert.True(labelled.YAxis.Range == frame.YAxis.Range && ReferenceEquals(labelled.YAxis.Ticks, frame.YAxis.Ticks),
                        $"{where}\n  the Y axis changed beyond its title");
                    Assert.True(ReferenceEquals(labelled.Legend, frame.Legend), $"{where}\n  the legend changed");
                    Assert.True(ReferenceEquals(labelled.StatisticsPanel, frame.StatisticsPanel), $"{where}\n  the statistics panel changed");
                    Assert.True(ReferenceEquals(labelled.ReferenceLines, frame.ReferenceLines), $"{where}\n  the reference lines changed");

                    // What the application applies gives the same strings as the step on its own.
                    var applied = GraphPresentation.Apply(
                        RobustnessGraphs.BuilderFrame(built.Model!),
                        data,
                        new GraphConfiguration(definition.GraphType, Guid.Empty, []) { Specification = specification, LabelOptions = labels },
                        TestContext.Current.CancellationToken);
                    Assert.True(
                        (applied.Title, applied.XAxis.Title, applied.YAxis.Title) == (labelled.Title, labelled.XAxis.Title, labelled.YAxis.Title)
                        && applied.XAxis.Range == labelled.XAxis.Range,
                        $"{where}\n  the presentation gave other labels than the labels step");

                    // Hidden labels only ever give the plot more room; nothing a label does can take room from it.
                    if (labels == AllHidden)
                    {
                        var bounds = new SkiaSharp.SKRect(0, 0, RobustnessGraphs.RenderWidth, RobustnessGraphs.RenderHeight);
                        var before = SkiaGraphRenderer.Layout(frame, bounds, theme).PlotArea;
                        var after = SkiaGraphRenderer.Layout(labelled, bounds, theme).PlotArea;
                        Assert.True(
                            before.IsEmpty || (after.Left <= before.Left && after.Top <= before.Top && after.Right >= before.Right && after.Bottom >= before.Bottom),
                            $"{where}\n  hiding the labels shrank the plot from {before} to {after}");
                    }

                    RobustnessGraphs.Render(built with { Frame = labelled }, theme);
                }
            }
        }
    }
}
