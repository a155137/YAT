using YAT.Application.Graphs;

namespace YAT.Application.Abstractions.Settings;

// Where the user's graph palettes are kept between runs (Task #050). It holds a user's preferences, not a project's
// data: it belongs to the user, not to any project, and nothing of it ever reaches a graph except the colours a new
// graph is given (see GraphPaletteLibrary.DefaultAppearance).
//
// Loading never fails: whatever cannot be read is left out and said in the result's warnings, and the worst case is
// the empty library - YAT Default and nothing else. Nothing is written by loading.
public interface IGraphPaletteLibraryStore
{
    GraphPaletteLibraryLoadResult Load();

    // Replaces what is kept with this library, whole. Throws GraphPaletteStoreException when it cannot; what was kept
    // before is then still there, as it was.
    void Save(GraphPaletteLibrary library);
}

// What loading found: the library to use, whether it may be written back this session, and what was wrong.
//
// IsReadOnly protects what is kept from being overwritten by a library that is not all of it: the file could not be
// read, a damaged file could not be set aside, or it was written by a newer YAT whose palettes this one may not
// understand in full.
public sealed record GraphPaletteLibraryLoadResult(
    GraphPaletteLibrary Library,
    bool IsReadOnly,
    IReadOnlyList<GraphPaletteLoadWarning> Warnings)
{
    public static GraphPaletteLibraryLoadResult Empty { get; } = new(GraphPaletteLibrary.Empty, false, []);
}

public enum GraphPaletteLoadWarningKind
{
    // The file is there but could not be read. Read-only.
    FileUnreadable,

    // The file is not a palette library at all (not JSON, or not its shape); it was kept aside under Detail's name.
    FileCorrupt,

    // The file is not a palette library and could not be kept aside. Read-only, so it is not overwritten.
    FileCorruptNotPreserved,

    // Written by a newer YAT (Detail: its schema version). What this YAT understands is used. Read-only.
    NewerSchema,

    // One palette was left out (Detail: its name or position, and why).
    PaletteSkipped,

    // The default palette is not among the palettes read: YAT Default is the default.
    DefaultPaletteMissing
}

public sealed record GraphPaletteLoadWarning(GraphPaletteLoadWarningKind Kind, string Detail);
