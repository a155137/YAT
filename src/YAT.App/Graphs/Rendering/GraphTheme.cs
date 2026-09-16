using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Every colour and text size a graph is drawn with, in one place: no renderer holds a literal colour of its own, and a
// graph drawn in the dark theme differs from the light one only by the theme it was given.
public sealed record GraphTheme
{
    public required SKColor Background { get; init; }

    public required SKColor PlotBackground { get; init; }

    public required SKColor Axis { get; init; }

    public required SKColor Grid { get; init; }

    // The graph title and the axis titles.
    public required SKColor Text { get; init; }

    // Tick labels, which sit behind the titles in the visual hierarchy.
    public required SKColor SecondaryText { get; init; }

    public required SKColor LegendBorder { get; init; }

    // Series colours, used by legend entries (and later by the graphs themselves) through their series index.
    public required IReadOnlyList<SKColor> SeriesPalette { get; init; }

    public float TitleFontSize { get; init; } = 16f;

    public float AxisTitleFontSize { get; init; } = 12f;

    public float TickLabelFontSize { get; init; } = 11f;

    public float AxisThickness { get; init; } = 1.25f;

    public float GridThickness { get; init; } = 1f;

    // Series colours repeat once the palette runs out, so any series index has a colour.
    public SKColor SeriesColor(int seriesIndex)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(seriesIndex);
        return SeriesPalette[seriesIndex % SeriesPalette.Count];
    }
}

// The themes YAT draws graphs in. Which one is used is decided by the control from the application's theme variant.
public static class GraphThemes
{
    // Categorical colours, readable on both the light and the dark plot background.
    private static readonly SKColor[] Palette =
    [
        new(0x1F, 0x77, 0xB4),
        new(0xD6, 0x27, 0x28),
        new(0x2C, 0xA0, 0x2C),
        new(0xFF, 0x7F, 0x0E),
        new(0x94, 0x67, 0xBD),
        new(0x8C, 0x56, 0x4B),
        new(0x17, 0xBE, 0xCF),
        new(0xBC, 0xBD, 0x22)
    ];

    public static GraphTheme Light { get; } = new()
    {
        Background = new SKColor(0xFF, 0xFF, 0xFF),
        PlotBackground = new SKColor(0xFF, 0xFF, 0xFF),
        Axis = new SKColor(0x4A, 0x4A, 0x4A),
        Grid = new SKColor(0xE1, 0xE1, 0xE1),
        Text = new SKColor(0x1F, 0x1F, 0x1F),
        SecondaryText = new SKColor(0x50, 0x50, 0x50),
        LegendBorder = new SKColor(0xC8, 0xC8, 0xC8),
        SeriesPalette = Palette
    };

    public static GraphTheme Dark { get; } = new()
    {
        Background = new SKColor(0x20, 0x20, 0x20),
        PlotBackground = new SKColor(0x2A, 0x2A, 0x2A),
        Axis = new SKColor(0xB4, 0xB4, 0xB4),
        Grid = new SKColor(0x3C, 0x3C, 0x3C),
        Text = new SKColor(0xF0, 0xF0, 0xF0),
        SecondaryText = new SKColor(0xC6, 0xC6, 0xC6),
        LegendBorder = new SKColor(0x55, 0x55, 0x55),
        SeriesPalette = Palette
    };
}
