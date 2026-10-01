using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Graphs;

// One palette of the user's library as an appearance editor offers it (Task #050): its name, its colours as they are
// now, and whether new graphs start with it. A copy: choosing it puts its colours into a graph, never a reference.
public sealed record GraphPaletteChoice(string Name, GraphPalette Palette, bool IsDefault);

// The user's palettes as an appearance editor is shown them, at one moment (Task #050): what can be chosen, whether YAT
// Default is the default, and anything about the palette settings the user should know (a damaged file recovered,
// palettes left out, a file that cannot be written). Nothing in it can reach where the palettes are kept.
public sealed record GraphPaletteChoices(
    IReadOnlyList<GraphPaletteChoice> Palettes,
    bool YatDefaultIsDefault,
    string? Notice = null)
{
    // No library at all: YAT Default alone.
    public static GraphPaletteChoices None { get; } = new([], true);

    // The one palette with exactly these colours, or null when none or several have them - a convenience for showing
    // where a graph's colours came from, never an identity.
    public GraphPaletteChoice? Match(GraphPalette? palette)
    {
        if (palette is null)
        {
            return null;
        }

        var matches = Palettes.Where(choice => choice.Palette.Colors.SequenceEqual(palette.Colors)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }
}

// What an appearance editor - a graph setup's, or a drawn graph's - may know of the user's palettes (Task #050): the
// choices as they are now, and the Palette Manager to change them. It is the only way the windows reach the library,
// and it has no way to the file the palettes are kept in.
public interface IGraphPaletteLibraryAccess
{
    GraphPaletteChoices Choices { get; }

    // A Palette Manager over a working copy of the library: nothing changes until it is saved.
    GraphPaletteManagerViewModel CreateManager();
}
