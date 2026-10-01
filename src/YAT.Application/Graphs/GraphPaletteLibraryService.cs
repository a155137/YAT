using YAT.Application.Abstractions.Settings;
using YAT.Application.Exceptions;

namespace YAT.Application.Graphs;

// Whether a change to the palette library was kept for the next run.
public enum GraphPaletteSaveStatus
{
    Saved,

    // Not written: what is kept may not be overwritten this session (see GraphPaletteLibraryLoadResult.IsReadOnly).
    ReadOnly,

    // Writing failed; what was kept before is still there.
    Failed
}

// Whether a library was saved, and why not when it was not.
public sealed record GraphPaletteSaveResult(GraphPaletteSaveStatus Status, string? Error = null);

// What a change did: the library's own result - refused or not - and, for one that was made, whether it was saved.
public sealed record GraphPaletteChange(GraphPaletteLibraryResult Result, GraphPaletteSaveStatus? Save, string? SaveError = null)
{
    public bool Succeeded => Result.Succeeded;
}

// The user's graph palettes for one run of YAT (Task #050): loaded once when it is made, changed through it, and saved
// after every change that was made. A change is made in memory whether or not it can be saved, so the palettes work this
// session even when the file cannot be written; the change says whether it was saved.
//
// Its one use outside palette editing is the start of a new graph: DefaultAppearance gives the appearance a graph setup
// begins with, a copy of the default palette's colours as they are now (or YAT Default, which is no palette). Nothing
// that draws a graph knows this service.
public sealed class GraphPaletteLibraryService
{
    private readonly IGraphPaletteLibraryStore _store;
    private readonly Lock _gate = new();

    // yatDefault: YAT Default's colours, which the library itself does not hold, for duplicating it.
    public GraphPaletteLibraryService(IGraphPaletteLibraryStore store, GraphPalette yatDefault)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(yatDefault);

        _store = store;
        YatDefault = new GraphPalette(yatDefault.Colors);

        GraphPaletteLibraryLoadResult loaded;
        try
        {
            loaded = store.Load();
        }
        catch (Exception exception)
        {
            // A store never throws from Load; if one does, YAT still starts - with YAT Default, and without overwriting
            // whatever it failed to read.
            loaded = new GraphPaletteLibraryLoadResult(
                GraphPaletteLibrary.Empty,
                true,
                [new GraphPaletteLoadWarning(GraphPaletteLoadWarningKind.FileUnreadable, exception.Message)]);
        }

        Library = loaded.Library;
        IsReadOnly = loaded.IsReadOnly;
        LoadWarnings = loaded.Warnings;
    }

    public GraphPaletteLibrary Library { get; private set; }

    // Whether changes are kept in memory only this session.
    public bool IsReadOnly { get; }

    // What was wrong with what was kept, for whoever wants to say so.
    public IReadOnlyList<GraphPaletteLoadWarning> LoadWarnings { get; }

    // YAT Default's colours.
    public GraphPalette YatDefault { get; }

    // Raised after a change was made (saved or not).
    public event EventHandler? Changed;

    // The appearance a new graph setup begins with: GraphAppearanceOptions.Default under YAT Default, otherwise the
    // default palette's colours as they are now.
    public GraphAppearanceOptions DefaultAppearance => Library.DefaultAppearance;

    public GraphPaletteChange Add(string name, GraphPalette palette) => Apply(library => library.Add(name, palette));

    // sourceId: one of the library's palettes, or null for YAT Default.
    public GraphPaletteChange Duplicate(Guid? sourceId, string name) =>
        Apply(library => sourceId is { } id ? library.Duplicate(id, name) : library.Add(name, YatDefault));

    public GraphPaletteChange Rename(Guid id, string name) => Apply(library => library.Rename(id, name));

    public GraphPaletteChange EditColors(Guid id, GraphPalette colors) => Apply(library => library.EditColors(id, colors));

    public GraphPaletteChange Delete(Guid id) => Apply(library => library.Delete(id));

    public GraphPaletteChange SetDefault(Guid? id) => Apply(library => library.SetDefault(id));

    public GraphPaletteChange ResetDefault() => Apply(library => library.ResetDefault());

    // The whole library at once (the Palette Manager's Save): what the user edited in a working copy, made and saved in
    // one step - one save, whatever was edited. The library is made in memory whether or not it can be saved; the
    // result says whether it was.
    public GraphPaletteSaveResult Replace(GraphPaletteLibrary library)
    {
        ArgumentNullException.ThrowIfNull(library);

        GraphPaletteSaveResult saved;
        lock (_gate)
        {
            Library = library;
            var (status, error) = Save(library);
            saved = new GraphPaletteSaveResult(status, error);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    private GraphPaletteChange Apply(Func<GraphPaletteLibrary, GraphPaletteLibraryResult> change)
    {
        GraphPaletteChange outcome;
        lock (_gate)
        {
            var result = change(Library);
            if (!result.Succeeded)
            {
                return new GraphPaletteChange(result, null);
            }

            Library = result.Library;
            var (status, error) = Save(result.Library);
            outcome = new GraphPaletteChange(result, status, error);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return outcome;
    }

    private (GraphPaletteSaveStatus Status, string? Error) Save(GraphPaletteLibrary library)
    {
        if (IsReadOnly)
        {
            return (GraphPaletteSaveStatus.ReadOnly, null);
        }

        try
        {
            _store.Save(library);
            return (GraphPaletteSaveStatus.Saved, null);
        }
        catch (GraphPaletteStoreException exception)
        {
            return (GraphPaletteSaveStatus.Failed, exception.Message);
        }
    }
}
