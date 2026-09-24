using System.Globalization;

namespace YAT.Application.Graphs;

// One colour a user chose for a graph (Task #046): red, green and blue, always opaque. Written and read as "#RRGGBB".
// It knows nothing of how a graph is drawn; the renderer turns it into a colour of its own.
public readonly record struct GraphColor(byte R, byte G, byte B)
{
    // "#RRGGBB", upper case: the one way a colour is written.
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";

    // "#RRGGBB" - or "RRGGBB" - in either case, with surrounding blanks ignored. Anything else is not a colour.
    public static bool TryParse(string? text, out GraphColor color)
    {
        color = default;
        var value = text?.Trim() ?? string.Empty;
        if (value.StartsWith('#'))
        {
            value = value[1..];
        }

        if (value.Length != 6 || !value.All(char.IsAsciiHexDigit)
            || !uint.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgb))
        {
            return false;
        }

        color = new GraphColor((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}

// The colours a graph's series are drawn in, in order (Task #046): series i takes colour i modulo their number, exactly
// as it takes the graph theme's own palette. Palettes compare by their colours, in order.
public sealed record GraphPalette
{
    public const int MinimumColors = 1;

    public const int MaximumColors = 16;

    public GraphPalette(IReadOnlyList<GraphColor> colors)
    {
        ArgumentNullException.ThrowIfNull(colors);
        Colors = [.. colors];
    }

    public IReadOnlyList<GraphColor> Colors { get; }

    // Between one and sixteen colours.
    public bool IsValid => Colors.Count is >= MinimumColors and <= MaximumColors;

    public bool Equals(GraphPalette? other) => other is not null && Colors.SequenceEqual(other.Colors);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var color in Colors)
        {
            hash.Add(color);
        }

        return hash.ToHashCode();
    }
}

// Whether a graph's grid is drawn.
public enum GraphGridMode
{
    // The grid as the graph type draws it: for every graph so far, drawn.
    Auto,

    // Drawn. For now exactly what Auto draws.
    Show,

    // Not drawn.
    Hide
}

// How a graph looks (Task #046): the colours of its series, whether and in which colour its grid is drawn, and the
// colours behind its plot and around it. Part of the graph's configuration, and a setting of the graph itself, which a
// graph window can change afterwards. It changes only how the graph is drawn - never its data, its statistics, its axes
// or its series - so nothing is read or worked out again for it. Whatever is not chosen (null) is the graph theme's, in
// the light and the dark theme alike; whatever is chosen stays that colour in both.
public sealed record GraphAppearanceOptions(
    GraphPalette? Palette = null,
    GraphGridMode GridMode = GraphGridMode.Auto,
    GraphColor? GridColor = null,
    GraphColor? PlotBackground = null,
    GraphColor? GraphBackground = null)
{
    // The graph as the theme draws it: nothing chosen.
    public static GraphAppearanceOptions Default { get; } = new();

    // Nothing chosen: the theme alone decides how the graph looks.
    public bool IsDefault => Equals(Default);

    // A defined grid mode, and a palette - when there is one - of one to sixteen colours.
    public bool IsValid => Enum.IsDefined(GridMode) && (Palette is null || Palette.IsValid);
}
