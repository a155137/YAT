using YAT.Application.Abstractions.Persistence;
using YAT.Application.Ingestion;

namespace YAT.app.Composition;

// Owns the services of one project scope: its worksheet metadata repositories, its raw data store and the
// paste execution service wired to exactly those. It is a container only: no open, save, close or switching.
// Only Application types are exposed, so consumers never see Infrastructure types.
public sealed class ProjectSession : IDisposable
{
    internal ProjectSession(
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository worksheetColumns,
        IWorksheetRawDataStore rawDataStore,
        PasteExecutionService pasteExecution)
    {
        Worksheets = worksheets;
        WorksheetColumns = worksheetColumns;
        RawDataStore = rawDataStore;
        PasteExecution = pasteExecution;
    }

    public IWorksheetRepository Worksheets { get; }

    public IWorksheetColumnRepository WorksheetColumns { get; }

    public IWorksheetRawDataStore RawDataStore { get; }

    public PasteExecutionService PasteExecution { get; }

    // The session owns its raw data store; the DuckDB store holds the project database open until disposed.
    public void Dispose() => (RawDataStore as IDisposable)?.Dispose();
}
