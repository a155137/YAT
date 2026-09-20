using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.app.Composition;
using YAT.app.Graphs.Rendering;
using YAT.app.ViewModels;
using YAT.Domain.Entities;

namespace YAT.app.Graphs;

// The Graph menu commands: read the active worksheet's column metadata through the current session, show the graph
// setup for the chosen graph type, and turn the configuration the user confirmed into a graph window.
//
// It owns the order of the steps, not the work: the data comes from the session, the render model from the graph type's
// own builder, and the window from the presenter. It never touches a repository or a raw data store.
//
// Scatter plots are drawn from Task #027 on, histograms from Task #028 and normal probability plots from Task #030;
// the empirical CDF says so until its own task implements it.
public sealed class GraphSetupController
{
    private const string NotImplementedMessage = "This graph type is not implemented yet.";
    private const string NoDataMessage = "This graph has no data to plot.";

    private readonly IGraphSetupDialogs _dialogs;
    private readonly IGraphWindowPresenter _windows;
    private readonly ScatterRenderModelBuilder _scatter;
    private readonly HistogramRenderModelBuilder _histogram;
    private readonly ProbabilityPlotRenderModelBuilder _probabilityPlot;

    internal GraphSetupController(
        IGraphSetupDialogs dialogs,
        IGraphWindowPresenter windows,
        ScatterRenderModelBuilder scatter,
        HistogramRenderModelBuilder histogram,
        ProbabilityPlotRenderModelBuilder probabilityPlot)
    {
        _dialogs = dialogs;
        _windows = windows;
        _scatter = scatter;
        _histogram = histogram;
        _probabilityPlot = probabilityPlot;
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

        var configuration = await _dialogs.ShowSetupAsync(
            new GraphSetupViewModel(GraphTypeDefinitions.For(graphType), worksheet, columns));

        if (configuration is not null)
        {
            LastConfiguration = configuration;
            await ShowGraphAsync(configuration, session, cancellationToken);
        }

        return configuration;
    }

    // Reads the graph's observations through the session, prepares them and opens the window. A graph window appears
    // only for a graph that can actually be drawn: nothing is shown for a cancelled request, a failed read or a
    // configuration that leaves no observations.
    private async Task ShowGraphAsync(GraphConfiguration configuration, MainWindowSession session, CancellationToken cancellationToken)
    {
        if (configuration.GraphType is not (GraphType.ScatterPlot or GraphType.Histogram or GraphType.ProbabilityPlot))
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

        (GraphRenderModel Frame, IGraphPlotRenderer Plot)? graph;
        try
        {
            // Preparing up to a million observations is real work: it runs off the UI thread and can be cancelled, so a
            // cancelled request never leaves a half-prepared graph behind.
            graph = await Task.Run(() => Prepare(data, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (graph is not { } prepared)
        {
            await _dialogs.ShowErrorAsync(NoDataMessage);
            return;
        }

        _windows.ShowGraph(prepared.Frame, prepared.Plot);
    }

    // The graph type's own preparation, which is the only place that turns graph data into something drawable. Null
    // means the configuration was valid but left nothing to draw.
    private (GraphRenderModel Frame, IGraphPlotRenderer Plot)? Prepare(GraphData data, CancellationToken cancellationToken)
    {
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
                    new HistogramPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name),
                    cancellationToken);
                return histogramModel is null ? null : (histogramModel.Frame, new HistogramRenderer(histogramModel));

            case UnivariateGraphData univariate when univariate.GraphType == GraphType.ProbabilityPlot:
                var probabilityModel = _probabilityPlot.Build(
                    univariate,
                    new ProbabilityPlotLabels(univariate.Variable.Name, univariate.Group?.Column.Name),
                    cancellationToken);
                return probabilityModel is null ? null : (probabilityModel.Frame, new ProbabilityPlotRenderer(probabilityModel));

            default:
                return null;
        }
    }
}
