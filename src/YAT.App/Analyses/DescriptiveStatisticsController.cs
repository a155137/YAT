using YAT.Application.Analyses;
using YAT.Application.Exceptions;
using YAT.Application.Filtering;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.Domain.Entities;
using YAT.Domain.Enums;

namespace YAT.app.Analyses;

// The Descriptive Statistics command: read the active worksheet's column metadata through the current session, show
// the analysis setup, and turn the configuration the user confirmed into a result table on screen.
//
// It owns the order of the steps, not the work: the data comes from the session, the statistics from the builder, and
// the window from the presenter. It never touches a repository or a raw data store.
public sealed class DescriptiveStatisticsController
{
    public const string SetupTitle = "Descriptive Statistics";

    private const string NoWorksheetMessage = "Select a worksheet before running an analysis.";
    private const string NoColumnsMessage = "This worksheet has no numeric columns to analyse.";
    private const string ColumnsUnreadableMessage = "The worksheet columns could not be read.";
    private const string NoDataMessage = "This analysis has no data to summarise.";

    // The analysis's row filter kept no row (Task #053): said apart from rows that have nothing to summarise.
    public const string NoMatchingRowsMessage = "No rows match the filter.";

    private readonly IAnalysisSetupDialogs _dialogs;
    private readonly IAnalysisResultPresenter _results;
    private readonly DescriptiveStatisticsBuilder _builder;

    internal DescriptiveStatisticsController(
        IAnalysisSetupDialogs dialogs,
        IAnalysisResultPresenter results,
        DescriptiveStatisticsBuilder builder)
    {
        _dialogs = dialogs;
        _results = results;
        _builder = builder;
    }

    // The last configuration a user confirmed, kept for tests and debugging until analyses become documents.
    public AnalysisConfiguration? LastConfiguration { get; private set; }

    public async Task<AnalysisConfiguration?> ConfigureAsync(
        MainWindowSession? session,
        Worksheet? worksheet,
        CancellationToken cancellationToken)
    {
        if (session is null || worksheet is null)
        {
            await _dialogs.ShowErrorAsync(NoWorksheetMessage);
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
            await _dialogs.ShowErrorAsync(ColumnsUnreadableMessage);
            return null;
        }

        // Descriptive statistics summarise measured values, so a worksheet without a Numeric column has nothing to
        // offer the setup.
        if (!columns.Any(column => column.DataType == WorksheetDataType.Numeric))
        {
            await _dialogs.ShowErrorAsync(NoColumnsMessage);
            return null;
        }

        var configuration = await _dialogs.ShowSetupAsync(new AnalysisSetupViewModel(
            SetupTitle,
            worksheet,
            columns,
            // The Filter dialog reads a column's distinct values through the session, never its rows (Task #053).
            (columnId, token) => session.LoadFilterValuesAsync(worksheet.Id, columnId, token)));
        if (configuration is not null)
        {
            LastConfiguration = configuration;
            await ShowResultAsync(configuration, session, cancellationToken);
        }

        return configuration;
    }

    // Reads the analysis's rows through the session, summarises them and opens the result window. A window appears
    // only for an analysis that has something to show: nothing is shown for a cancelled request, a failed read or a
    // worksheet without rows.
    private async Task ShowResultAsync(
        AnalysisConfiguration configuration,
        MainWindowSession session,
        CancellationToken cancellationToken)
    {
        AnalysisData data;
        try
        {
            data = await session.LoadAnalysisDataAsync(configuration, cancellationToken);
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
        catch (AnalysisDataException exception)
        {
            // The message of an AnalysisDataException is written for the user; the storage error stays inside it.
            await _dialogs.ShowErrorAsync(exception.Message);
            return;
        }

        if (data.RowCount == 0 && configuration.Filter is not null)
        {
            await _dialogs.ShowErrorAsync(NoMatchingRowsMessage);
            return;
        }

        AnalysisResultTable table;
        try
        {
            // Summarising a million rows is real work: it runs off the UI thread and can be cancelled, so a cancelled
            // request never leaves a half-built table behind.
            table = await Task.Run(() => _builder.Build(data, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (table.Rows.Count == 0)
        {
            await _dialogs.ShowErrorAsync(NoDataMessage);
            return;
        }

        // A filtered result says so (Task #053); a result over every row is shown exactly as before.
        if (configuration.Filter is { } filter)
        {
            table = table with { Note = $"Filter: {RowFilter.Describe(filter)}" };
        }

        _results.ShowResult(table);
    }
}
