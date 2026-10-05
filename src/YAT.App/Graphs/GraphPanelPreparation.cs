using YAT.Application.Graphs;
using YAT.app.Graphs.Rendering;

namespace YAT.app.Graphs;

// Prepares a graph drawn in panels (Task #058) - a scatter plot, histogram, probability plot or empirical CDF with a
// Panel column - out of the graph types' own builders, composed:
//
//     1. the whole graph is built once from every observation, as it would be without panels: its title, its legend,
//        the order of its series and, for a histogram, its bins;
//     2. each panel is built from its own observations with the same builder, against that series order - so a group
//        has the same colour in every panel, whether or not it is in it - and those bins, so every panel counts into the
//        same intervals; the display sample is shared out, so the panels together draw what one graph would;
//     3. the graph's axes are the union of what the panels' axes reach, so every panel is drawn over the same scales.
//
// The result is one frame - the whole graph's, over the shared axes - presented once (GraphPresentation), so the
// configured ranges, ticks, view, labels, legend and appearance are the graph's, and the panels: each a title and what
// its graph type draws. Its statistics (Task #062) are the panels' own: a row for each panel - or panel and series -
// worked out from that panel's observations alone, never over every panel together, in one statistics panel of the
// whole graph.
public static class GraphPanelPreparation
{
    public sealed record Prepared(GraphRenderModel Frame, IReadOnlyList<GraphPanel> Panels, GraphData Whole);

    private sealed record Built(GraphRenderModel Frame, IGraphPlotRenderer Plot, IReadOnlyList<string> SeriesLabels, IReadOnlyList<HistogramBin>? Bins);

    // The graph type of a graph drawn in panels: everything it always offers - since Task #062 its statistics panel
    // too, which holds the panels' statistics.
    public static GraphTypeDefinition Definition(GraphType graphType) => GraphTypeDefinitions.For(graphType);

    // The graph in panels, or null when nothing in it can be drawn. Throws GraphPreparationException for a panel column
    // with too many values, and for what the graph type's own builder refuses.
    public static Prepared? Prepare(GraphData data, GraphConfiguration configuration, string? axisTitle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(configuration);

        var panels = GraphPanelSplit.Split(data, cancellationToken);
        if (panels.Count == 0)
        {
            return null;
        }

        var whole = Combined(data, cancellationToken);
        if (Build(whole, configuration, axisTitle, null, null, DisplaySampling.DefaultMaximumRenderedPoints, cancellationToken) is not { } global)
        {
            return null;
        }

        var order = new GraphSeriesOrder(global.SeriesLabels);
        var budget = Math.Max(1, DisplaySampling.DefaultMaximumRenderedPoints / panels.Count);
        var built = panels
            .Select(panel => (panel.Title, Model: Build(Combined(panel.Data, cancellationToken), configuration, axisTitle, order, global.Bins, budget, cancellationToken)))
            .ToList();

        var frames = built.Where(panel => panel.Model is not null).Select(panel => panel.Model!.Frame).ToList();
        var frame = frames.Count == 0
            ? global.Frame
            : global.Frame.WithXAxis(Union(global.Frame.XAxis, [.. frames.Select(item => item.XAxis)]))
                .WithYAxis(Union(global.Frame.YAxis, [.. frames.Select(item => item.YAxis)]));
        frame = WithStatistics(frame, data, panels, order, cancellationToken);

        return new Prepared(frame, [.. built.Select(panel => new GraphPanel(panel.Title, panel.Model?.Plot))], whole);
    }

    // The statistics of a graph in panels (Task #062), for a graph type with a statistics panel: every panel's own,
    // worked out from its observations alone - a row per panel ("Site 1"), or per panel and series ("Site 1 / Lot A",
    // or with variables together "Site 1 / Reg1 / Lot A") - in the order of the panels and, within one, of the whole
    // graph's series, in their colours. Nothing is worked out over every panel together.
    private static GraphRenderModel WithStatistics(
        GraphRenderModel frame,
        GraphData data,
        IReadOnlyList<GraphPanelData> panels,
        GraphSeriesOrder order,
        CancellationToken cancellationToken)
    {
        if (!GraphTypeDefinitions.For(data.GraphType).Supports(GraphCapability.StatisticsPanel))
        {
            return frame;
        }

        var rows = new List<GraphStatisticsRow>();
        string? seriesHeader = null;
        foreach (var panel in panels)
        {
            if (Combined(panel.Data, cancellationToken) is not UnivariateGraphData observations
                || GraphStatisticsPanelBuilder.Build(observations, cancellationToken) is not { } statistics)
            {
                continue;
            }

            if (statistics.GroupHeader is null)
            {
                rows.Add(statistics.Rows[0].Relabelled(panel.Label, null));
                continue;
            }

            seriesHeader ??= statistics.GroupHeader;
            rows.AddRange(statistics.Rows
                .OrderBy(row => order.IndexOf(row.Label, int.MaxValue))
                .Select(row => row.Relabelled(
                    $"{panel.Label} / {row.Label}", order.IndexOf(row.Label, row.SeriesIndex ?? 0))));
        }

        if (rows.Count == 0)
        {
            return frame;
        }

        var column = PanelColumn(data);
        var header = seriesHeader is null ? column : $"{column} / {seriesHeader}";
        return frame.WithStatisticsPanel(new GraphStatisticsPanel(GraphStatisticsPanelBuilder.Title, header, rows));
    }

    // The name of the graph's panel column.
    private static string PanelColumn(GraphData data)
    {
        var panel = data is MultiVariableGraphData several
            ? several.Variables.Select(variable => variable.Panel).First(panel => panel is not null)
            : data.Panel;
        return panel!.Column.Name;
    }

    // Several variables in one panel are drawn together, as they are without panels.
    private static GraphData Combined(GraphData data, CancellationToken cancellationToken) =>
        data is MultiVariableGraphData several ? GraphVariablesTogether.Combine(several, cancellationToken) : data;

    // The axis every panel is drawn over: the whole graph's title and scale, over the union of the panels' ranges. Where
    // one panel's axis already reaches that far both ways, its ticks are kept, so a graph of one panel - or panels that
    // agree - keeps exactly the ticks its graph type chose. Where several do, the coarsest ticks are kept (on a count axis,
    // the tallest panel's), so the axis does not depend on the order the panels are drawn in.
    private static GraphAxisModel Union(GraphAxisModel whole, IReadOnlyList<GraphAxisModel> panels)
    {
        var range = new GraphAxisRange(panels.Min(axis => axis.Range.Minimum), panels.Max(axis => axis.Range.Maximum));
        var ticks = panels.Where(axis => axis.Range == range).MinBy(axis => axis.Ticks.Count)?.Ticks ?? GraphAxisViewportBuilder.Ticks(whole.Scale, range);
        return new GraphAxisModel(range, ticks, whole.Title) { Scale = whole.Scale };
    }

    private static Built? Build(
        GraphData data,
        GraphConfiguration configuration,
        string? axisTitle,
        GraphSeriesOrder? order,
        IReadOnlyList<HistogramBin>? bins,
        int points,
        CancellationToken cancellationToken)
    {
        switch (data)
        {
            case ScatterGraphData scatter:
                var scatterModel = new ScatterRenderModelBuilder(points, order).Build(
                    scatter, new ScatterPlotLabels(scatter.X.Name, scatter.Y.Name, scatter.Group?.Column.Name), cancellationToken);
                return scatterModel is null
                    ? null
                    : new Built(scatterModel.Frame, new ScatterRenderer(scatterModel), Labels(scatterModel.Series.Select(series => (series.SeriesIndex, series.Label))), null);

            case UnivariateGraphData univariate when univariate.GraphType == GraphType.Histogram:
                var histogramModel = new HistogramRenderModelBuilder(order, bins).Build(
                    univariate,
                    new HistogramPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name) { AxisTitle = axisTitle },
                    configuration.HistogramOptions,
                    cancellationToken);
                return histogramModel is null
                    ? null
                    : new Built(histogramModel.Frame, new HistogramRenderer(histogramModel), Labels(histogramModel.Series.Select(series => (series.SeriesIndex, series.Label))), histogramModel.Bins);

            case UnivariateGraphData univariate when univariate.GraphType == GraphType.ProbabilityPlot:
                var probabilityModel = new ProbabilityPlotRenderModelBuilder(points, order).Build(
                    univariate,
                    new ProbabilityPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name) { AxisTitle = axisTitle },
                    configuration.ProbabilityPlotOptions,
                    cancellationToken);
                return probabilityModel is null
                    ? null
                    : new Built(probabilityModel.Frame, new ProbabilityPlotRenderer(probabilityModel), Labels(probabilityModel.Series.Select(series => (series.SeriesIndex, series.Label))), null);

            case UnivariateGraphData univariate when univariate.GraphType == GraphType.EmpiricalCdf:
                var empiricalCdfModel = new EmpiricalCdfRenderModelBuilder(points, order).Build(
                    univariate,
                    new EmpiricalCdfLabels(univariate.Variable.Name, univariate.Group?.Column.Name) { AxisTitle = axisTitle },
                    cancellationToken);
                return empiricalCdfModel is null
                    ? null
                    : new Built(empiricalCdfModel.Frame, new EmpiricalCdfRenderer(empiricalCdfModel), Labels(empiricalCdfModel.Series.Select(series => (series.SeriesIndex, series.Label))), null);

            default:
                throw new ArgumentException($"A {data.GraphType} graph cannot be drawn in panels.", nameof(data));
        }
    }

    private static IReadOnlyList<string> Labels(IEnumerable<(int Index, string Label)> series) =>
        [.. series.OrderBy(item => item.Index).Select(item => item.Label)];
}
