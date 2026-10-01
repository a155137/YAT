namespace YAT.Application.Graphs;

// One palette of the user's own (Task #050): a stable identity, a name and its colours. The id is what refers to it - a
// rename or new colours keep the id - and the name is how it is shown. YAT Default is not one of these: it is no
// palette of the library at all (see GraphPaletteLibrary.DefaultPaletteId).
public sealed record GraphPaletteDefinition(Guid Id, string Name, GraphPalette Palette);

// Why a change to the palette library was refused.
public enum GraphPaletteLibraryProblem
{
    // The name is blank once trimmed.
    NameMissing,

    // The name is YAT Default's, in any case.
    NameReserved,

    // Another palette already has the name, in any case.
    NameTaken,

    // The palette has fewer than GraphPalette.MinimumColors or more than GraphPalette.MaximumColors colours.
    PaletteInvalid,

    // No palette of the library has the id.
    PaletteNotFound,

    // A palette of the library already has the id.
    IdTaken
}

// What a change to the palette library gave: the library after it - the same library when the change was refused -
// the problem that refused it, and the palette it was about (a palette added or duplicated has a new id).
public sealed record GraphPaletteLibraryResult(GraphPaletteLibrary Library, GraphPaletteLibraryProblem? Problem, Guid? PaletteId)
{
    public bool Succeeded => Problem is null;
}

// The user's graph palettes (Task #050): the palettes of their own, in the order they were made, and which palette a new
// graph starts with. YAT Default - the graph theme's own colours, which a graph draws when its appearance chooses no
// palette - is always there and is not a palette of the library: DefaultPaletteId null means it, and it has no id, no
// entry and nothing that could be edited, renamed or deleted.
//
// The library is a value: every change gives a new library and leaves this one as it was, so whatever took a palette's
// colours from it (a graph setup's snapshot) keeps them whatever the library does next.
//
// Rules, for every palette: one to sixteen colours, a name that is not blank once trimmed (it is kept trimmed), not
// YAT Default's and no other palette's (names compare ignoring case), and an id of its own.
public sealed class GraphPaletteLibrary
{
    // YAT Default's name: reserved, in any case.
    public const string YatDefaultName = "YAT Default";

    // The library of a user who has made nothing: no palettes of their own, YAT Default for new graphs.
    public static GraphPaletteLibrary Empty { get; } = new([], null);

    // palettes: in order. defaultPaletteId: one of them, or null for YAT Default. Throws ArgumentException when they
    // break a rule; a stored library that might is checked palette by palette first (see Check).
    public GraphPaletteLibrary(IReadOnlyList<GraphPaletteDefinition> palettes, Guid? defaultPaletteId)
    {
        ArgumentNullException.ThrowIfNull(palettes);

        var copy = palettes.ToArray();
        for (var index = 0; index < copy.Length; index++)
        {
            var palette = copy[index] ?? throw new ArgumentException("A palette must not be null.", nameof(palettes));
            if (Check(copy.Take(index), palette) is { } problem)
            {
                throw new ArgumentException($"The palette '{palette.Name}' cannot be in the library: {problem}.", nameof(palettes));
            }
        }

        if (defaultPaletteId is { } id && !copy.Any(palette => palette.Id == id))
        {
            throw new ArgumentException("The default palette must be one of the library's palettes.", nameof(defaultPaletteId));
        }

        CustomPalettes = Array.AsReadOnly(copy);
        DefaultPaletteId = defaultPaletteId;
    }

    // The user's own palettes, in order. Never YAT Default.
    public IReadOnlyList<GraphPaletteDefinition> CustomPalettes { get; }

    // The palette a new graph starts with; null for YAT Default.
    public Guid? DefaultPaletteId { get; }

    // The default palette, or null when it is YAT Default.
    public GraphPaletteDefinition? DefaultPalette => DefaultPaletteId is { } id ? Find(id) : null;

    // How a new graph's appearance starts: the theme's own look under YAT Default - GraphAppearanceOptions.Default
    // itself, no palette chosen - and otherwise the default palette's colours as they are now. The colours are the
    // graph's from then on: nothing in it refers back to the library.
    public GraphAppearanceOptions DefaultAppearance =>
        DefaultPalette is { } palette
            ? GraphAppearanceOptions.Default with { Palette = new GraphPalette(palette.Palette.Colors) }
            : GraphAppearanceOptions.Default;

    public GraphPaletteDefinition? Find(Guid id) => CustomPalettes.FirstOrDefault(palette => palette.Id == id);

    // The name as it is kept: without surrounding blanks.
    public static string Normalize(string? name) => name?.Trim() ?? string.Empty;

    // Why a palette cannot join these others, or null when it can. The name is checked as given: a library keeps names
    // normalized, so give it one.
    public static GraphPaletteLibraryProblem? Check(IEnumerable<GraphPaletteDefinition> others, GraphPaletteDefinition palette)
    {
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(palette);

        var list = others.ToList();
        if (palette.Palette is not { IsValid: true })
        {
            return GraphPaletteLibraryProblem.PaletteInvalid;
        }

        return NameProblem(list, palette.Name, except: null) ?? (list.Any(other => other.Id == palette.Id)
            ? GraphPaletteLibraryProblem.IdTaken
            : null);
    }

    // A new palette at the end, with an id of its own.
    public GraphPaletteLibraryResult Add(string name, GraphPalette palette) => Add(Guid.NewGuid(), name, palette);

    public GraphPaletteLibraryResult Add(Guid id, string name, GraphPalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);

        var added = new GraphPaletteDefinition(id, Normalize(name), new GraphPalette(palette.Colors));
        return Check(CustomPalettes, added) is { } problem
            ? Refused(problem, id)
            : Changed([.. CustomPalettes, added], DefaultPaletteId, id);
    }

    // A copy of one of the library's palettes under another name, at the end. (YAT Default is copied by adding its
    // colours: the library does not hold them.)
    public GraphPaletteLibraryResult Duplicate(Guid sourceId, string name) =>
        Find(sourceId) is { } source ? Add(name, source.Palette) : Refused(GraphPaletteLibraryProblem.PaletteNotFound, sourceId);

    public GraphPaletteLibraryResult Rename(Guid id, string name)
    {
        if (Find(id) is not { } palette)
        {
            return Refused(GraphPaletteLibraryProblem.PaletteNotFound, id);
        }

        var normalized = Normalize(name);
        return NameProblem(CustomPalettes, normalized, except: id) is { } problem
            ? Refused(problem, id)
            : Changed(Replace(palette with { Name = normalized }), DefaultPaletteId, id);
    }

    public GraphPaletteLibraryResult EditColors(Guid id, GraphPalette colors)
    {
        ArgumentNullException.ThrowIfNull(colors);

        if (Find(id) is not { } palette)
        {
            return Refused(GraphPaletteLibraryProblem.PaletteNotFound, id);
        }

        return colors.IsValid
            ? Changed(Replace(palette with { Palette = new GraphPalette(colors.Colors) }), DefaultPaletteId, id)
            : Refused(GraphPaletteLibraryProblem.PaletteInvalid, id);
    }

    // Deleting the default palette makes YAT Default the default again.
    public GraphPaletteLibraryResult Delete(Guid id) =>
        Find(id) is null
            ? Refused(GraphPaletteLibraryProblem.PaletteNotFound, id)
            : Changed([.. CustomPalettes.Where(palette => palette.Id != id)], DefaultPaletteId == id ? null : DefaultPaletteId, id);

    // id: one of the library's palettes, or null for YAT Default.
    public GraphPaletteLibraryResult SetDefault(Guid? id) =>
        id is { } paletteId && Find(paletteId) is null
            ? Refused(GraphPaletteLibraryProblem.PaletteNotFound, paletteId)
            : Changed(CustomPalettes, id, id);

    // YAT Default for new graphs again. The palettes themselves stay.
    public GraphPaletteLibraryResult ResetDefault() => SetDefault(null);

    private static GraphPaletteLibraryProblem? NameProblem(IEnumerable<GraphPaletteDefinition> palettes, string name, Guid? except)
    {
        if (string.IsNullOrWhiteSpace(name) || name != Normalize(name))
        {
            return GraphPaletteLibraryProblem.NameMissing;
        }

        if (string.Equals(name, YatDefaultName, StringComparison.OrdinalIgnoreCase))
        {
            return GraphPaletteLibraryProblem.NameReserved;
        }

        return palettes.Any(other => other.Id != except && string.Equals(other.Name, name, StringComparison.OrdinalIgnoreCase))
            ? GraphPaletteLibraryProblem.NameTaken
            : null;
    }

    private GraphPaletteDefinition[] Replace(GraphPaletteDefinition changed) =>
        [.. CustomPalettes.Select(palette => palette.Id == changed.Id ? changed : palette)];

    private GraphPaletteLibraryResult Changed(IReadOnlyList<GraphPaletteDefinition> palettes, Guid? defaultPaletteId, Guid? id) =>
        new(new GraphPaletteLibrary(palettes, defaultPaletteId), null, id);

    private GraphPaletteLibraryResult Refused(GraphPaletteLibraryProblem problem, Guid? id) => new(this, problem, id);
}
