using System.Diagnostics;
using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.app.Composition;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using YAT.Domain.Entities;

namespace YAT.app.Graphs;

// The Graph menu commands: read the active worksheet's column metadata through the current session, show the graph
// setup for the chosen graph type, and turn what the user confirmed into graph windows: one, or - for several variables
// drawn separately (Task #041) - one for each variable.
//
// It owns the order of the steps, not the work: the data comes from the session, the render model from the graph type's
// own builder, and the window from the presenter. It never touches a repository or a raw data store.
//
// Scatter plots are drawn from Task #027 on, histograms from Task #028, normal probability plots from Task #030 and
// empirical CDFs from Task #031.
public sealed class GraphSetupController
{
    private const string NotImplementedMessage = "This graph type is not implemented yet.";
    private const string NoDataMessage = "This graph has no data to plot.";
    public const string PreparationFailedMessage = "This graph could not be drawn.";

    // What the user is told about the graphs of a request that could not be drawn, one line each after this.
    public const string PartialFailureHeading = "These graphs could not be drawn:";

    private readonly IGraphSetupDialogs _dialogs;
    private readonly IGraphWindowPresenter _windows;
    private readonly ScatterRenderModelBuilder _scatter;
    private readonly HistogramRenderModelBuilder _histogram;
    private readonly ProbabilityPlotRenderModelBuilder _probabilityPlot;
    private readonly EmpiricalCdfRenderModelBuilder _empiricalCdf;
    private readonly BoxPlotRenderModelBuilder _boxPlot;
    private readonly Func<GraphData, GraphConfiguration, CancellationToken, (GraphRenderModel Frame, IGraphPlotRenderer Plot)?> _prepare;

    // prepare: replaces the graph types' own preparation. Only tests pass one, to prove that a preparation that fails
    // is contained; the application always uses Prepare.
    internal GraphSetupController(
        IGraphSetupDialogs dialogs,
        IGraphWindowPresenter windows,
        ScatterRenderModelBuilder scatter,
        HistogramRenderModelBuilder histogram,
        ProbabilityPlotRenderModelBuilder probabilityPlot,
        EmpiricalCdfRenderModelBuilder empiricalCdf,
        BoxPlotRenderModelBuilder boxPlot,
        Func<GraphData, GraphConfiguration, CancellationToken, (GraphRenderModel Frame, IGraphPlotRenderer Plot)?>? prepare = null)
    {
        _dialogs = dialogs;
        _windows = windows;
        _scatter = scatter;
        _histogram = histogram;
        _probabilityPlot = probabilityPlot;
        _empiricalCdf = empiricalCdf;
        _boxPlot = boxPlot;
        _prepare = prepare ?? Prepare;
    }

    // The last configuration a user confirmed, kept for tests and debugging until graphs become documents.
    public GraphConfiguration? LastConfiguration { get; private set; }

    public async Task<GraphConfiguration?> ConfigureAsync(
        GraphType graphType,
        MainWindowSession? session,
        Worksheet? worksheet,
        CancellationToken cancellationToken)
    {
        if (session is null || worksheet is null)
        {
            await _dialogs.ShowErrorAsync("Select a worksheet before creating a graph.");
            return null;
        }

        IReadOnlyList<WorksheetColumn> columns;
        try
        {
            // Metadata only: Id, Index, Name and DataType of the worksheet's columns, read fresh for the active project.
            columns = await session.LoadWorksheetColumnsAsync(worksheet.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is ProjectSessionClosedException or OperationCanceledException)
        {
            // The project was closed, replaced or the request was cancelled: there is nothing to set up any more.
            return null;
        }
        catch (Exception exception) when (exception is ProjectStorageException or RawDataStorageException or EntityNotFoundException)
        {
            await _dialogs.ShowErrorAsync("The worksheet columns could not be read.");
            return null;
        }

        if (columns.Count == 0)
        {
            await _dialogs.ShowErrorAsync("This worksheet has no columns to graph.");
            return null;
        }

        var request = await _dialogs.ShowSetupAsync(
            new GraphSetupViewModel(GraphTypeDefinitions.For(graphType), worksheet, columns));

        if (request is not null)
        {
            LastConfiguration = request.Configuration;
            await ShowGraphsAsync(request, session, cancellationToken);
        }

        return request?.Configuration;
    }

    // Reads the graphs' observations through the session - once, however many graphs they make - prepares them and
    // opens their windows. A graph window appears only for a graph that can actually be drawn: nothing is shown for a
    // cancelled request or a failed read, and a graph whose preparation leaves nothing to draw or fails is reported
    // instead of shown. When several graphs were asked for, the others still open, and the ones that could not be drawn
    // are reported together in one message.
    private async Task ShowGraphsAsync(
        GraphSetupRequest request,
        MainWindowSession session,
        CancellationToken cancellationToken)
    {
        var configuration = request.Configuration;

        // Every graph type this version knows is drawn; the guard is what a graph type added to the enum without a
        // builder would meet.
        if (configuration.GraphType is not (GraphType.ScatterPlot or GraphType.Histogram
            or GraphType.ProbabilityPlot or GraphType.EmpiricalCdf or GraphType.BoxPlot))
        {
            await _dialogs.ShowErrorAsync(NotImplementedMessage);
            return;
        }

        GraphData data;
        try
        {
            data = await session.LoadGraphDataAsync(configuration, cancellationToken);
        }
        catch (ProjectSessionClosedException)
        {
            // The project was closed or replaced while the data was being read: there is nothing left to show it for.
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (GraphDataException exception)
        {
            // The message of a GraphDataException is written for the user; the storage error stays inside it.
            await _dialogs.ShowErrorAsync(exception.Message);
            return;
        }

        IReadOnlyList<PreparedGraph> graphs;
        try
        {
            // Preparing up to a million observations is real work: it runs off the UI thread and can be cancelled, so a
            // cancelled request never leaves a half-prepared graph behind - and never some of its graphs.
            graphs = await Task.Run(() => PrepareAll(request, data, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            // Each graph is contained on its own (PrepareOne); this is the boundary for what the request shares, such
            // as drawing its variables together. The user is told, nothing opens, and the exception is traced.
            Trace.TraceError($"Preparing a {configuration.GraphType} graph failed: {exception}");
            await _dialogs.ShowErrorAsync(PreparationFailedMessage);
            return;
        }

        var cascade = 0;
        foreach (var graph in graphs.Where(graph => graph.Presentation is not null))
        {
            _windows.ShowGraph(graph.Presentation!, graph.Plot, cascade++);
        }

        var failures = graphs.Where(graph => graph.Failure is not null).ToList();
        if (failures.Count == 1 && graphs.Count == 1)
        {
            // One graph asked for: its own message, as it always was.
            await _dialogs.ShowErrorAsync(failures[0].Failure!);
        }
        else if (failures.Count > 0)
        {
            await _dialogs.ShowErrorAsync(PartialFailureMessage(failures));
        }
    }

    private static string PartialFailureMessage(IEnumerable<PreparedGraph> failures) =>
        string.Join(
            Environment.NewLine,
            [PartialFailureHeading, .. failures.Select(failure => $"{failure.Name}: {failure.Failure}")]);

    // One graph of a request, prepared: what the window shows, or why there is no window.
    private sealed record PreparedGraph(
        string Name,
        GraphPresentationState? Presentation,
        IGraphPlotRenderer? Plot,
        string? Failure);

    // The graphs a request asks for, each prepared on its own so that one that fails does not take the others with it.
    //
    // A graph type that reads several variables (every one but the scatter plot) gets them in one read. With one
    // variable, or with several drawn separately, each variable's graph is drawn from that variable's observations
    // exactly as a single-variable graph always was; drawn together, the variables become one graph of series.
    private IReadOnlyList<PreparedGraph> PrepareAll(
        GraphSetupRequest request,
        GraphData data,
        CancellationToken cancellationToken)
    {
        var configuration = request.Configuration;
        var separate = request.Layout == GraphVariableLayout.Separate;
        switch (data)
        {
            case MultiVariableGraphData boxPlot
                when boxPlot.GraphType == GraphType.BoxPlot && separate && boxPlot.Variables.Count > 1:
                return
                [
                    .. boxPlot.Variables.Select(variable => PrepareOne(
                        variable.Variable.Name,
                        new MultiVariableGraphData(boxPlot.GraphType, boxPlot.WorksheetId, [variable]),
                        GraphSetupRequest.ForVariable(configuration, variable.Variable.ColumnId),
                        cancellationToken))
                ];

            case MultiVariableGraphData boxPlot when boxPlot.GraphType == GraphType.BoxPlot:
                return [PrepareOne(configuration.GraphType.ToString(), boxPlot, configuration, cancellationToken)];

            case MultiVariableGraphData { Variables.Count: 1 } single:
                var only = single.Variables[0];
                return [PrepareOne(only.Variable.Name, only, configuration, cancellationToken)];

            case MultiVariableGraphData several when separate:
                return
                [
                    .. several.Variables.Select(variable => PrepareOne(
                        variable.Variable.Name,
                        variable,
                        GraphSetupRequest.ForVariable(configuration, variable.Variable.ColumnId),
                        cancellationToken))
                ];

            case MultiVariableGraphData several:
                var together = GraphVariablesTogether.Combine(several, cancellationToken);
                return [PrepareOne(together.Variable.Name, together, configuration, cancellationToken)];

            default:
                return [PrepareOne(configuration.GraphType.ToString(), data, configuration, cancellationToken)];
        }
    }

    // One graph: its preparation and what the graph shows besides its plot - the statistics panel, the specification
    // lines, the labels - applied in one place: the graph type says what it offers, the configuration what is wanted.
    // The graph window keeps the presentation, so its labels can be changed without the data.
    private PreparedGraph PrepareOne(
        string name,
        GraphData data,
        GraphConfiguration configuration,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_prepare(data, configuration, cancellationToken) is not { } built)
            {
                return new PreparedGraph(name, null, null, NoDataMessage);
            }

            var presentation = GraphPresentation.Present(built.Frame, data, configuration, cancellationToken);
            return new PreparedGraph(name, presentation, built.Plot, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (GraphPreparationException exception)
        {
            // An expected refusal the user can act on (a histogram bin width that does not suit the data): its message
            // is written for the user. No window opens for it, and nothing went wrong that needs tracing.
            return new PreparedGraph(name, null, null, exception.Message);
        }
        catch (Exception exception)
        {
            // Preparation is computation over data that was read successfully, so a failure here is a defect in one
            // graph type's preparation - not a reason to end the application. The boundary is only this graph: the user
            // is told it could not be drawn, no window opens for it, and the exception itself is not swallowed but
            // written to the trace output the application already logs to.
            Trace.TraceError($"Preparing a {configuration.GraphType} graph failed: {exception}");
            return new PreparedGraph(name, null, null, PreparationFailedMessage);
        }
    }

    // The graph type's own preparation, which is the only place that turns graph data into something drawable. Null
    // means the configuration was valid but left nothing to draw. The configuration is there for the options a graph
    // type has of its own; each builder is handed only its own.
    private (GraphRenderModel Frame, IGraphPlotRenderer Plot)? Prepare(
        GraphData data,
        GraphConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // A single-variable graph configured with several variables is those variables drawn together
        // (GraphVariablesTogether): its X axis is their shared "Data" axis rather than one variable's.
        var axisTitle = configuration.FindColumnIds(GraphVariableRole.Variable).Count > 1
            ? GraphVariablesTogether.AxisTitle
            : null;

        switch (data)
        {
            case ScatterGraphData scatter:
                var scatterModel = _scatter.Build(
                    scatter,
                    new ScatterPlotLabels(scatter.X.Name, scatter.Y.Name, scatter.Group?.Column.Name),
                    cancellationToken);
                return scatterModel is null ? null : (scatterModel.Frame, new ScatterRenderer(scatterModel));

            case UnivariateGraphData univariate when univariate.GraphType == GraphType.Histogram:
                var histogramModel = _histogram.Build(
                    univariate,
                    new HistogramPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name)
                    {
                        AxisTitle = axisTitle
                    },
                    configuration.HistogramOptions,
                    cancellationToken);
                return histogramModel is null ? null : (histogramModel.Frame, new HistogramRenderer(histogramModel));

            case UnivariateGraphData univariate when univariate.GraphType == GraphType.ProbabilityPlot:
                // The graph types with options of their own are given those and nothing else.
                var probabilityModel = _probabilityPlot.Build(
                    univariate,
                    new ProbabilityPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name)
                    {
                        AxisTitle = axisTitle
                    },
                    configuration.ProbabilityPlotOptions,
                    cancellationToken);
                return probabilityModel is null ? null : (probabilityModel.Frame, new ProbabilityPlotRenderer(probabilityModel));

            case MultiVariableGraphData boxPlot when boxPlot.GraphType == GraphType.BoxPlot:
                var boxPlotModel = _boxPlot.Build(
                    boxPlot,
                    new BoxPlotLabels(
                        [.. boxPlot.Variables.Select(variable => variable.Variable.Name)],
                        boxPlot.Variables.Select(variable => variable.Group?.Column.Name).FirstOrDefault(name => name is not null)),
                    cancellationToken);
                return boxPlotModel is null ? null : (boxPlotModel.Frame, new BoxPlotRenderer(boxPlotModel));

            case UnivariateGraphData univariate when univariate.GraphType == GraphType.EmpiricalCdf:
                var empiricalCdfModel = _empiricalCdf.Build(
                    univariate,
                    new EmpiricalCdfLabels(univariate.Variable.Name, univariate.Group?.Column.Name)
                    {
                        AxisTitle = axisTitle
                    },
                    cancellationToken);
                return empiricalCdfModel is null ? null : (empiricalCdfModel.Frame, new EmpiricalCdfRenderer(empiricalCdfModel));

            default:
                return null;
        }
    }
}
