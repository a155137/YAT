using System.Globalization;
using SkiaSharp;
using YAT.App.Tests.TestDoubles;
using YAT.Application.Abstractions.Persistence;
using YAT.Application.Graphs;
using YAT.Application.Specifications;
using YAT.app.Composition;
using YAT.app.Graphs.Export;
using YAT.app.Graphs.Rendering;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.App.Tests.Robustness;

public enum RobustnessGraph
{
    Histogram,
    BoxPlot,
    ProbabilityPlot,
    EmpiricalCdf,
    Scatter
}

// One graph built from one dataset: the graph data it was built from, and what the graph type's own builder made of
// it. Model, Frame and Plot are null when the builder had nothing to draw. Frame is the frame the application shows -
// the builder's, with the presentation applied - and Specification the one it was prepared with (#036).
internal sealed record BuiltGraph(RobustnessGraph Graph, GraphData Data, object? Model, GraphRenderModel? Frame, IGraphPlotRenderer? Plot)
{
    public Specification Specification { get; init; } = Specification.None;

    // The probability plot options it was built with (#037); every other graph type ignores them.
    public ProbabilityPlotOptions ProbabilityPlotOptions { get; init; } = ProbabilityPlotOptions.Default;
}

// How the harness turns a robustness case into graphs: the graph data a case becomes (the way the data pipeline
// compacts worksheet rows), the production builder of each graph type, and the production renderers and exporter.
// Nothing here draws or computes on its own - it only calls what the application calls.
internal static class RobustnessGraphs
{
    public const string VariableName = "Reg1";
    public const string PairedName = "Reg2";
    public const string GroupName = "SITE";

    // The size the renderer tests draw at; export uses its own fixed size.
    public const int RenderWidth = 640;
    public const int RenderHeight = 480;

    public static readonly IReadOnlyList<RobustnessGraph> UnivariateGraphs =
        [RobustnessGraph.Histogram, RobustnessGraph.BoxPlot, RobustnessGraph.ProbabilityPlot, RobustnessGraph.EmpiricalCdf];

    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    public static string Name(RobustnessGraph graph) => graph switch
    {
        RobustnessGraph.Histogram => "Histogram",
        RobustnessGraph.BoxPlot => "Box Plot",
        RobustnessGraph.ProbabilityPlot => "Probability Plot",
        RobustnessGraph.EmpiricalCdf => "Empirical CDF",
        _ => "Scatter Plot"
    };

    public static GraphType TypeOf(RobustnessGraph graph) => graph switch
    {
        RobustnessGraph.Histogram => GraphType.Histogram,
        RobustnessGraph.BoxPlot => GraphType.BoxPlot,
        RobustnessGraph.ProbabilityPlot => GraphType.ProbabilityPlot,
        RobustnessGraph.EmpiricalCdf => GraphType.EmpiricalCdf,
        _ => GraphType.ScatterPlot
    };

    // The graphs a case applies to: univariate graphs for every case, a scatter plot only for paired cases.
    public static IEnumerable<RobustnessGraph> For(RobustnessCase robustnessCase) =>
        robustnessCase.IsPaired ? [.. UnivariateGraphs, RobustnessGraph.Scatter] : UnivariateGraphs;

    // ---- Builder level: a case as the graph data pipeline would hand it over ----

    public static GraphData DataFor(RobustnessGraph graph, RobustnessCase robustnessCase)
    {
        if (graph == RobustnessGraph.Scatter)
        {
            return ScatterData(robustnessCase);
        }

        var univariate = Univariate(TypeOf(graph), robustnessCase);
        return graph == RobustnessGraph.BoxPlot
            ? new MultiVariableGraphData(GraphType.BoxPlot, univariate.WorksheetId, [univariate])
            : univariate;
    }

    // The rows with a value, with the group value of their own row: the graph pipeline's univariate null rule.
    private static UnivariateGraphData Univariate(GraphType type, RobustnessCase robustnessCase)
    {
        var values = new List<double>();
        var groups = new List<string?>();
        for (var row = 0; row < robustnessCase.RowCount; row++)
        {
            if (robustnessCase.Values[row] is not { } value)
            {
                continue;
            }

            values.Add(value);
            groups.Add(robustnessCase.Groups?[row]);
        }

        return new UnivariateGraphData(
            type, Guid.NewGuid(), Column(VariableName), values.ToArray(), GroupData(robustnessCase, groups));
    }

    // The rows with both values: the graph pipeline's scatter null rule.
    private static ScatterGraphData ScatterData(RobustnessCase robustnessCase)
    {
        var x = new List<double>();
        var y = new List<double>();
        var groups = new List<string?>();
        for (var row = 0; row < robustnessCase.RowCount; row++)
        {
            if (robustnessCase.Values[row] is not { } xValue || robustnessCase.PairedY![row] is not { } yValue)
            {
                continue;
            }

            x.Add(xValue);
            y.Add(yValue);
            groups.Add(robustnessCase.Groups?[row]);
        }

        return new ScatterGraphData(
            Guid.NewGuid(), Column(VariableName), Column(PairedName), x.ToArray(), y.ToArray(), GroupData(robustnessCase, groups));
    }

    private static GraphGroupData? GroupData(RobustnessCase robustnessCase, List<string?> groups)
    {
        if (!robustnessCase.IsGrouped)
        {
            return null;
        }

        return robustnessCase.NumericGroups
            ? new NumericGroupData(Column(GroupName), groups.Select(ParseGroup).ToArray())
            : new StringGroupData(Column(GroupName, WorksheetDataType.String), groups.ToArray());
    }

    private static double? ParseGroup(string? label) =>
        label is null ? null : double.Parse(label, NumberStyles.Float, CultureInfo.InvariantCulture);

    private static GraphColumnInfo Column(string name, WorksheetDataType dataType = WorksheetDataType.Numeric) =>
        new(Guid.NewGuid(), name, dataType);

    // ---- The production builders, renderers and exporter ----

    // The graph type's own builder over this data. maximumRenderedPoints is the display budget (the histogram has none).
    public static BuiltGraph Build(
        RobustnessGraph graph,
        GraphData data,
        int maximumRenderedPoints = DisplaySampling.DefaultMaximumRenderedPoints,
        CancellationToken cancellationToken = default,
        Specification? specification = null,
        ProbabilityPlotOptions? probabilityPlotOptions = null)
    {
        specification ??= Specification.None;
        probabilityPlotOptions ??= ProbabilityPlotOptions.Default;
        switch (graph)
        {
            case RobustnessGraph.Histogram:
            {
                var univariate = (UnivariateGraphData)data;
                var model = new HistogramRenderModelBuilder().Build(
                    univariate, new HistogramPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name), cancellationToken);
                return Built(graph, data, model, model?.Frame, model is null ? null : new HistogramRenderer(model), cancellationToken, specification);
            }

            case RobustnessGraph.BoxPlot:
            {
                var multi = (MultiVariableGraphData)data;
                var model = new BoxPlotRenderModelBuilder(maximumRenderedPoints).Build(
                    multi,
                    new BoxPlotLabels(
                        [.. multi.Variables.Select(variable => variable.Variable.Name)],
                        multi.Variables.Select(variable => variable.Group?.Column.Name).FirstOrDefault(name => name is not null)),
                    cancellationToken);
                return Built(graph, data, model, model?.Frame, model is null ? null : new BoxPlotRenderer(model), cancellationToken, specification);
            }

            case RobustnessGraph.ProbabilityPlot:
            {
                var univariate = (UnivariateGraphData)data;
                var model = new ProbabilityPlotRenderModelBuilder(maximumRenderedPoints).Build(
                    univariate,
                    new ProbabilityPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name),
                    probabilityPlotOptions,
                    cancellationToken);
                return Built(graph, data, model, model?.Frame, model is null ? null : new ProbabilityPlotRenderer(model), cancellationToken, specification)
                    with { ProbabilityPlotOptions = probabilityPlotOptions };
            }

            case RobustnessGraph.EmpiricalCdf:
            {
                var univariate = (UnivariateGraphData)data;
                var model = new EmpiricalCdfRenderModelBuilder(maximumRenderedPoints).Build(
                    univariate, new EmpiricalCdfLabels(univariate.Variable.Name, univariate.Group?.Column.Name), cancellationToken);
                return Built(graph, data, model, model?.Frame, model is null ? null : new EmpiricalCdfRenderer(model), cancellationToken, specification);
            }

            default:
            {
                var scatter = (ScatterGraphData)data;
                var model = new ScatterRenderModelBuilder(maximumRenderedPoints).Build(
                    scatter, new ScatterPlotLabels(scatter.X.Name, scatter.Y.Name, scatter.Group?.Column.Name), cancellationToken);
                return Built(graph, data, model, model?.Frame, model is null ? null : new ScatterRenderer(model), cancellationToken, specification);
            }
        }
    }

    // The graph as the application prepares it: the builder's frame, with its presentation applied through the one
    // pipeline the graph preparation uses - the default options (statistics shown) and the given specification.
    private static BuiltGraph Built(
        RobustnessGraph graph,
        GraphData data,
        object? model,
        GraphRenderModel? frame,
        IGraphPlotRenderer? plot,
        CancellationToken cancellationToken,
        Specification specification) =>
        new(graph, data, model, frame is null ? null : Present(graph, data, frame, specification, cancellationToken), plot)
        {
            Specification = specification
        };

    public static GraphRenderModel Present(
        RobustnessGraph graph,
        GraphData data,
        GraphRenderModel builderFrame,
        Specification specification,
        CancellationToken cancellationToken = default) =>
        GraphPresentation.Apply(
            builderFrame, data, new GraphConfiguration(TypeOf(graph), Guid.Empty, []) { Specification = specification }, cancellationToken);

    // The frame the graph type's own builder made, before anything was applied to it.
    public static GraphRenderModel BuilderFrame(object model) => model switch
    {
        HistogramRenderModel histogram => histogram.Frame,
        BoxPlotRenderModel boxPlot => boxPlot.Frame,
        ProbabilityPlotRenderModel probability => probability.Frame,
        EmpiricalCdfRenderModel empirical => empirical.Frame,
        ScatterRenderModel scatter => scatter.Frame,
        _ => throw new ArgumentException($"Unknown render model {model.GetType().Name}.", nameof(model))
    };

    // Draws the graph off screen through the frame renderer the graph window uses.
    public static void Render(BuiltGraph built, GraphTheme theme)
    {
        using var bitmap = new SKBitmap(RenderWidth, RenderHeight);
        using var canvas = new SKCanvas(bitmap);
        new SkiaGraphRenderer().Render(canvas, built.Frame!, new SKRect(0, 0, RenderWidth, RenderHeight), theme, built.Plot);
    }

    // Exports the graph through the shared snapshot path, exactly as a graph window's File menu does.
    public static byte[] ExportPng(BuiltGraph built, GraphTheme theme) =>
        new GraphExportService().RenderPng(new GraphExportSnapshot(built.Frame!, built.Plot, theme));

    // ---- End to end: a case written to a real project and read back by the graph data query ----

    // extraVariables: further Numeric columns (for a multi-variable box plot), in the order they are added.
    public static async Task<GraphData> LoadThroughProjectAsync(
        RobustnessGraph graph,
        RobustnessCase robustnessCase,
        IReadOnlyList<(string Name, double?[] Values)> extraVariables,
        CancellationToken cancellationToken)
    {
        using var session = new CompositionRoot(new FixedTimeProvider(Now)).CreateProjectSession(":memory:");

        var worksheet = new Worksheet { Id = Guid.NewGuid(), Name = "Robustness" };
        await session.Worksheets.AddAsync(worksheet, cancellationToken);

        var columns = new List<RawDataColumn>();
        var assignments = new List<GraphColumnAssignment>();

        async Task<WorksheetColumn> AddColumnAsync(string name, WorksheetDataType dataType)
        {
            var column = new WorksheetColumn
            {
                Id = Guid.NewGuid(),
                WorksheetId = worksheet.Id,
                Index = columns.Count,
                Name = name,
                DataType = dataType
            };
            await session.WorksheetColumns.AddAsync(column, cancellationToken);
            return column;
        }

        var variable = await AddColumnAsync(VariableName, WorksheetDataType.Numeric);
        columns.Add(new NumericRawDataColumn(variable.Id, robustnessCase.Values));

        if (graph == RobustnessGraph.Scatter)
        {
            var paired = await AddColumnAsync(PairedName, WorksheetDataType.Numeric);
            columns.Add(new NumericRawDataColumn(paired.Id, robustnessCase.PairedY!));
            assignments.Add(new GraphColumnAssignment(GraphVariableRole.X, variable.Id));
            assignments.Add(new GraphColumnAssignment(GraphVariableRole.Y, paired.Id));
        }
        else
        {
            assignments.Add(new GraphColumnAssignment(GraphVariableRole.Variable, variable.Id));
            foreach (var (name, values) in extraVariables)
            {
                var extra = await AddColumnAsync(name, WorksheetDataType.Numeric);
                columns.Add(new NumericRawDataColumn(extra.Id, values));
                assignments.Add(new GraphColumnAssignment(GraphVariableRole.Variable, extra.Id));
            }
        }

        if (robustnessCase.Groups is { } groups)
        {
            var group = await AddColumnAsync(GroupName, robustnessCase.NumericGroups ? WorksheetDataType.Numeric : WorksheetDataType.String);
            columns.Add(robustnessCase.NumericGroups
                ? new NumericRawDataColumn(group.Id, groups.Select(ParseGroup).ToArray())
                : new StringRawDataColumn(group.Id, groups));
            assignments.Add(new GraphColumnAssignment(GraphVariableRole.Group, group.Id));
        }

        await session.RawDataStore.WriteColumnsAsync(worksheet.Id, new RawDataBlock(columns), cancellationToken);

        var query = new GraphDataQueryService(session.Worksheets, session.WorksheetColumns, session.RawDataStore);
        return await query.LoadAsync(new GraphConfiguration(TypeOf(graph), worksheet.Id, assignments), cancellationToken);
    }
}
