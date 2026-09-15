using YAT.Application.Abstractions.Persistence;
using YAT.Application.Ingestion;

namespace YAT.app.Composition;

// Owns the services of one project scope: its metadata repositories, its raw data store and the paste execution
// service wired to exactly those. It is a container only: no open, save, close or switching (see ProjectWorkspace).
// Only Application types are exposed, so consumers never see Infrastructure types.
//
// A persistent session (from ProjectWorkspace) keeps everything in one project database (Database). The legacy
// composition from CompositionRoot.CreateProjectSession keeps metadata in memory and has no Database or ProjectId;
// it is test composition only, not the production project model.
public sealed class ProjectSession : IDisposable
{
    internal ProjectSession(
        IProjectRepository projects,
        IWorksheetRepository worksheets,
        IWorksheetColumnRepository worksheetColumns,
        IWorksheetRawDataStore rawDataStore,
        PasteExecutionService pasteExecution,
        IProjectDatabase? database)
    {
        Projects = projects;
        Worksheets = worksheets;
        WorksheetColumns = worksheetColumns;
        RawDataStore = rawDataStore;
        PasteExecution = pasteExecution;
        Database = database;
    }

    public IProjectRepository Projects { get; }

    public IWorksheetRepository Worksheets { get; }

    public IWorksheetColumnRepository WorksheetColumns { get; }

    public IWorksheetRawDataStore RawDataStore { get; }

    public PasteExecutionService PasteExecution { get; }

    // The project database of a persistent session; null for the legacy in-memory metadata composition.
    public IProjectDatabase? Database { get; }

    // The project stored in Database (one per project file); null for the legacy composition.
    public Guid? ProjectId { get; internal set; }

    // The session owns its storage: the project database, or the raw data store of the legacy composition. Disposing
    // closes the database file (and releases its file lock); deleting temporary storage is up to ProjectWorkspace.
    public void Dispose()
    {
        if (Database is not null)
        {
            Database.Dispose();
        }
        else
        {
            (RawDataStore as IDisposable)?.Dispose();
        }
    }
}
