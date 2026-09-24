using SkiaSharp;
using YAT.Application.Graphs;

namespace YAT.app.Graphs.Rendering;

// Puts a graph's appearance on the theme it is drawn in (Task #046): the one place the user's colours and grid choice
// become the colours a graph is drawn with. A graph's frame never changes for them - they are a matter of drawing, so
// they change the theme the renderer is handed, and everything drawn from the theme follows: the series colours of
// every graph type, the legend's and the statistics panel's swatches, the grid, the plot background (and so the normal
// fit's halo), and the background around the plot, which a PNG and a slide are filled with too.
//
// What the appearance chooses is that colour whatever the theme; what it leaves unchosen is the theme's, so it follows
// the light and the dark theme as always. With nothing chosen the theme itself comes back - the very instance - so a
// graph looks exactly as it always did.
public static class GraphAppearance
{
    public static GraphTheme Resolve(GraphTheme theme, GraphAppearanceOptions options)
    {
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(options);

        if (options.IsDefault)
        {
            return theme;
        }

        if (!options.IsValid)
        {
            throw new ArgumentException(
                "The appearance is not valid; validate the configuration first.", nameof(options));
        }

        return theme with
        {
            SeriesPalette = options.Palette is { } palette ? [.. palette.Colors.Select(ToSkia)] : theme.SeriesPalette,
            ShowGrid = options.GridMode != GraphGridMode.Hide && theme.ShowGrid,
            Grid = options.GridColor is { } grid ? ToSkia(grid) : theme.Grid,
            PlotBackground = options.PlotBackground is { } plot ? ToSkia(plot) : theme.PlotBackground,
            Background = options.GraphBackground is { } background ? ToSkia(background) : theme.Background
        };
    }

    public static SKColor ToSkia(GraphColor color) => new(color.R, color.G, color.B);

    public static GraphColor FromSkia(SKColor color) => new(color.Red, color.Green, color.Blue);
}
