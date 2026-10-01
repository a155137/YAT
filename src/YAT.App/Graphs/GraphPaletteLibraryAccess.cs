using YAT.Application.Abstractions.Settings;
using YAT.Application.Graphs;
using YAT.app.ViewModels;

namespace YAT.app.Graphs;

// The user's palette library as the windows see it (Task #050): choices to pick from and the Palette Manager, over the
// one GraphPaletteLibraryService of the application. A notice about the palette settings - a file recovered, palettes
// left out, a file that cannot be written - is shown beside the palette choices until the user has opened the Palette
// Manager once, where it is always shown; YAT never stops to say it.
public sealed class GraphPaletteLibraryAccess : IGraphPaletteLibraryAccess
{
    private readonly GraphPaletteLibraryService _service;
    private bool _managerOpened;

    public GraphPaletteLibraryAccess(GraphPaletteLibraryService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    // How a new graph setup's appearance begins: a copy of the default palette's colours as they are now, or YAT Default.
    public GraphAppearanceOptions DefaultAppearance => _service.DefaultAppearance;

    public GraphPaletteChoices Choices
    {
        get
        {
            var library = _service.Library;
            return new GraphPaletteChoices(
                [
                    .. library.CustomPalettes.Select(palette =>
                        new GraphPaletteChoice(palette.Name, palette.Palette, palette.Id == library.DefaultPaletteId))
                ],
                library.DefaultPaletteId is null,
                _managerOpened ? null : Notice(_service.LoadWarnings, _service.IsReadOnly));
        }
    }

    public GraphPaletteManagerViewModel CreateManager()
    {
        _managerOpened = true;
        return new GraphPaletteManagerViewModel(_service);
    }

    // What the user should know about how the palette settings were read, in a sentence or two; null when nothing.
    public static string? Notice(IReadOnlyList<GraphPaletteLoadWarning> warnings, bool isReadOnly)
    {
        ArgumentNullException.ThrowIfNull(warnings);

        var sentences = new List<string>();
        foreach (var warning in warnings)
        {
            switch (warning.Kind)
            {
                case GraphPaletteLoadWarningKind.NewerSchema:
                    sentences.Add("The palette settings were saved by a newer version of YAT.");
                    break;
                case GraphPaletteLoadWarningKind.FileUnreadable:
                    sentences.Add("The palette settings could not be read.");
                    break;
                case GraphPaletteLoadWarningKind.FileCorrupt:
                    sentences.Add($"The palette settings were damaged and have been set aside ({warning.Detail}).");
                    break;
                case GraphPaletteLoadWarningKind.FileCorruptNotPreserved:
                    sentences.Add("The palette settings are damaged and could not be set aside.");
                    break;
                case GraphPaletteLoadWarningKind.DefaultPaletteMissing:
                    sentences.Add("The default palette was missing, so YAT Default is the default.");
                    break;
            }
        }

        var skipped = warnings.Count(warning => warning.Kind == GraphPaletteLoadWarningKind.PaletteSkipped);
        if (skipped > 0)
        {
            sentences.Add(skipped == 1
                ? "One saved palette could not be read and was left out."
                : $"{skipped} saved palettes could not be read and were left out.");
        }

        if (isReadOnly)
        {
            sentences.Add("Palettes can be used but not changed in this session, so the settings are not overwritten.");
        }

        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }
}
