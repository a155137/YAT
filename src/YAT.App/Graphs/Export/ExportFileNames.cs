using System.Text;

namespace YAT.app.Graphs.Export;

// The file name an export suggests, made from the graph's title. The title itself is never changed: a graph called
// "Scatterplot of I/V vs T" keeps its name and is offered as "Scatterplot of I_V vs T.png".
public static class ExportFileNames
{
    // What a graph with no usable title is called.
    public const string Fallback = "Graph";

    // Long enough for any real graph title, short enough to leave room for a folder path.
    public const int MaximumLength = 100;

    private const char Replacement = '_';

    // Names Windows refuses to give a file, whatever the extension is.
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    ];

    // "Histogram of Reg1" + "png" -> "Histogram of Reg1.png".
    public static string Suggest(string? title, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        return $"{Sanitize(title)}.{extension.TrimStart('.')}";
    }

    // The title as a file name: characters a file name cannot hold become underscores, runs of them collapse, and a
    // title that is nothing but such characters falls back to a name that always works.
    public static string Sanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Fallback;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(title.Length);
        foreach (var character in title)
        {
            if (invalid.Contains(character) || char.IsControl(character))
            {
                if (builder.Length > 0 && builder[^1] == Replacement)
                {
                    continue;
                }

                builder.Append(Replacement);
            }
            else
            {
                builder.Append(character);
            }
        }

        // Windows drops trailing dots and spaces from file names, so they are not part of one here either.
        var name = builder.ToString().Trim().Trim('.', ' ', Replacement).Trim();

        if (name.Length > MaximumLength)
        {
            name = name[..MaximumLength].TrimEnd('.', ' ', Replacement).TrimEnd();
        }

        if (name.Length == 0)
        {
            return Fallback;
        }

        return ReservedNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? $"{Replacement}{name}" : name;
    }
}
