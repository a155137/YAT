using SkiaSharp;
using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests.Robustness;

// #046: the appearance over the whole named corpus, on every graph type the case applies to - with the case's own
// groups, and, for the graph types that draw variables together, with the case drawn together with its negation -
// under a legend below the plot, statistics trimmed to N and CJK titles, in representative appearances and in the
// light and the dark theme. Each graph is presented as the application presents it and its appearance put on it; then:
//
//   * the appearance changes nothing but the theme: the very frames, and the graph's legend and panel as they were;
//   * with nothing chosen the theme itself is drawn in; what is chosen is that colour in either theme;
//   * series i takes palette colour i modulo the palette's length, the same every time;
//   * the graph draws, and exports with the chosen graph background around it.
public sealed class GraphRobustnessAppearanceTests
{
    public static TheoryData<string> Cases => GraphRobustnessNamedTests.UnivariateCases;

    public static TheoryData<string> PairedCases => GraphRobustnessNamedTests.PairedCases;

    private static readonly GraphColor Sand = new(0xF0, 0xE0, 0xD0);

    private static readonly GraphColor Navy = new(0x12, 0x34, 0x56);

    private static readonly GraphPalette Short = new([new GraphColor(0xD0, 0x10, 0x10)]);

    private static readonly GraphPalette Long = new([.. Enumerable.Range(0, GraphPalette.MaximumColors).Select(index => new GraphColor((byte)(index * 15), (byte)(240 - (index * 11)), (byte)(index * 7)))]);

    private static readonly (string Name, GraphAppearanceOptions Appearance)[] Appearances =
    [
        ("default", GraphAppearanceOptions.Default),
        ("grid hidden", new GraphAppearanceOptions(GridMode: GraphGridMode.Hide)),
        ("grid colour", new GraphAppearanceOptions(GridMode: GraphGridMode.Show, GridColor: new GraphColor(0xFF, 0, 0xFF))),
        ("plot background", new GraphAppearanceOptions(PlotBackground: Navy)),
        ("graph background", new GraphAppearanceOptions(GraphBackground: Sand)),
        ("short palette", new GraphAppearanceOptions(Short)),
        ("long palette, everything", new GraphAppearanceOptions(Long, GraphGridMode.Hide, new GraphColor(1, 2, 3), Sand, Navy))
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryAppearanceDrawsEveryGraph(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        foreach (var graph in RobustnessGraphs.UnivariateGraphs)
        {
            var data = RobustnessGraphs.DataFor(graph, robustnessCase);
            Check(robustnessCase.Describe($"{RobustnessGraphs.Name(graph)} appearance"), graph, data);
            if (graph != RobustnessGraph.BoxPlot && data is UnivariateGraphData univariate)
            {
                Check(robustnessCase.Describe($"{RobustnessGraphs.Name(graph)} appearance together"), graph, Together(univariate));
            }
        }
    }

    [Theory]
    [MemberData(nameof(PairedCases))]
    public void EveryAppearanceDrawsEveryScatterPlot(string name)
    {
        var robustnessCase = RobustnessCorpus.Named(name);
        Check(robustnessCase.Describe("Scatter Plot appearance"), RobustnessGraph.Scatter, RobustnessGraphs.DataFor(RobustnessGraph.Scatter, robustnessCase));
    }

    private static UnivariateGraphData Together(UnivariateGraphData data) =>
        GraphVariablesTogether.Combine(new MultiVariableGraphData(data.GraphType, data.WorksheetId,
        [
            data,
            new UnivariateGraphData(data.GraphType, data.WorksheetId, new GraphColumnInfo(Guid.NewGuid(), "晶圓 Negated", data.Variable.DataType),
                (double[])[.. data.Values.ToArray().Select(value => -value)], data.Group)
        ]), TestContext.Current.CancellationToken);

    private static void Check(string context, RobustnessGraph graph, GraphData data)
    {
        var built = RobustnessGraphs.Build(graph, data, cancellationToken: TestContext.Current.CancellationToken);
        if (built.Model is null)
        {
            return;
        }

        var definition = GraphTypeDefinitions.For(RobustnessGraphs.TypeOf(graph));
        var configuration = new GraphConfiguration(definition.GraphType, Guid.Empty, [])
        {
            LegendOptions = new GraphLegendOptions(GraphLegendMode.Auto, GraphLegendPosition.Bottom),
            StatisticsOptions = new GraphStatisticsOptions(GraphStatisticsMode.Auto, false, false, true),
            LabelOptions = new GraphLabelOptions(GraphLabelOption.Custom("晶圓 厚度 Wafer"), GraphLabelOption.Auto, GraphLabelOption.Auto)
        };
        var presented = GraphPresentation.Present(RobustnessGraphs.BuilderFrame(built.Model), data, configuration, TestContext.Current.CancellationToken);
        var themes = 0;

        foreach (var (name, appearance) in Appearances)
        {
            var where = $"{context} {name}";
            var styled = presented.WithAppearance(appearance);
            GraphRobustnessInvariants.That(
                ReferenceEquals(styled.Frame, presented.Frame) && ReferenceEquals(styled.BaseFrame, presented.BaseFrame)
                && ReferenceEquals(styled.UnlabelledFrame, presented.UnlabelledFrame) && Equals(styled.AppearanceOptions, appearance),
                where, "the appearance changed a frame");

            foreach (var theme in new[] { GraphThemes.Light, GraphThemes.Dark })
            {
                var resolved = GraphAppearance.Resolve(theme, appearance);
                if (appearance.IsDefault)
                {
                    GraphRobustnessInvariants.That(ReferenceEquals(resolved, theme), where, "nothing chosen is not the theme itself");
                }

                GraphRobustnessInvariants.That(
                    resolved.SeriesPalette.Count > 0
                    && resolved.PlotBackground == (appearance.PlotBackground is { } plot ? GraphAppearance.ToSkia(plot) : theme.PlotBackground)
                    && resolved.Background == (appearance.GraphBackground is { } background ? GraphAppearance.ToSkia(background) : theme.Background)
                    && resolved.ShowGrid == (appearance.GridMode != GraphGridMode.Hide)
                    && resolved.Text == theme.Text && resolved.Axis == theme.Axis,
                    where, "the resolved theme is not the theme with what was chosen");

                // Every series of the graph, in the colour its index takes, the same every time.
                foreach (var entry in styled.Frame.Legend?.Entries ?? [])
                {
                    var expected = appearance.Palette is { } palette
                        ? GraphAppearance.ToSkia(palette.Colors[entry.SeriesIndex % palette.Colors.Count])
                        : theme.SeriesColor(entry.SeriesIndex);
                    GraphRobustnessInvariants.That(
                        resolved.SeriesColor(entry.SeriesIndex) == expected
                        && GraphAppearance.Resolve(theme, appearance).SeriesColor(entry.SeriesIndex) == expected,
                        where, $"series {entry.SeriesIndex} is not in its palette colour");
                }

                try
                {
                    RobustnessGraphs.Render(built with { Frame = styled.Frame }, themes++ % 2 == 0 ? resolved : GraphAppearance.Resolve(GraphThemes.Dark, appearance));
                }
                catch (Exception exception)
                {
                    Assert.Fail($"{where}\n  rendering threw: {exception}");
                }
            }
        }

        // Exported with everything chosen: the graph background all around it.
        var all = Appearances[^1].Appearance;
        var png = RobustnessGraphs.ExportPng(built with { Frame = presented.WithAppearance(all).Frame }, GraphAppearance.Resolve(GraphThemes.Dark, all));
        using var image = SKBitmap.Decode(png);
        GraphRobustnessInvariants.That(
            image.GetPixel(0, 0) == GraphAppearance.ToSkia(all.GraphBackground!.Value)
            && image.GetPixel(image.Width - 1, image.Height - 1) == GraphAppearance.ToSkia(all.GraphBackground!.Value),
            context, "the export is not surrounded by the graph background chosen");
    }
}
