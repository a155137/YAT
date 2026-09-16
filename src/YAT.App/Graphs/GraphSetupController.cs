using YAT.Application.Exceptions;
using YAT.Application.Graphs;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;

namespace YAT.app.Graphs;

// The Graph menu commands: read the active worksheet's column metadata through the current session, show the graph
// setup for the chosen graph type, and return the configuration the user confirmed. It renders nothing; Task #025 adds
// the graph data pipeline.
public sealed class GraphSetupController
{
    private readonly IGraphSetupDialogs _dialogs;

    internal GraphSetupController(IGraphSetupDialogs dialogs)
    {
        _dialogs = dialogs;
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
        catch (ProjectSessionClosedException)
        {
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
        }

        return configuration;
    }
}
