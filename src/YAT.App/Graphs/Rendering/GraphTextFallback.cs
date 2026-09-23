using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace YAT.app.Graphs.Rendering;

// Draws and measures graph text so that characters the graph font has no glyph for - Chinese above all, but any
// missing character - are drawn from a system font that has one, instead of as empty boxes.
//
// Text the graph font covers entirely goes straight to Skia's own DrawText and MeasureText, exactly as before, so a
// graph whose text needs no fallback is drawn to the same pixels. Only text with a missing glyph is split into runs:
// the characters the graph font has stay in it, each run of missing ones is drawn in the font Windows offers for it
// (Traditional Chinese forms for Han characters), all on one baseline. Measuring goes through the same runs, so the
// layout makes room for what is actually drawn.
//
// Which font has a character is asked once per character and remembered for the life of the process; the system's
// fonts are never enumerated. A character no installed font has keeps the graph font's empty glyph.
internal static class GraphTextFallback
{
    public static float MeasureText(SKFont font, string text) => MeasureText(font, text, FontFallbackCache.Shared);

    internal static float MeasureText(SKFont font, string text, FontFallbackCache cache)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(cache);

        if (font.ContainsGlyphs(text))
        {
            return font.MeasureText(text);
        }

        var width = 0f;
        foreach (var run in Runs(font, text, cache))
        {
            width += MeasureRun(font, text, run);
        }

        return width;
    }

    public static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign align,
        SKFont font,
        SKPaint paint) =>
        DrawText(canvas, text, x, y, align, font, paint, FontFallbackCache.Shared);

    internal static void DrawText(
        SKCanvas canvas,
        string text,
        float x,
        float y,
        SKTextAlign align,
        SKFont font,
        SKPaint paint,
        FontFallbackCache cache)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(paint);
        ArgumentNullException.ThrowIfNull(cache);

        if (font.ContainsGlyphs(text))
        {
            canvas.DrawText(text, x, y, align, font, paint);
            return;
        }

        var runs = Runs(font, text, cache);
        var widths = new float[runs.Count];
        var total = 0f;
        for (var index = 0; index < runs.Count; index++)
        {
            widths[index] = MeasureRun(font, text, runs[index]);
            total += widths[index];
        }

        // Aligned as one piece of text: the runs follow each other from where the whole text starts.
        var left = align switch
        {
            SKTextAlign.Center => x - (total / 2f),
            SKTextAlign.Right => x - total,
            _ => x
        };

        for (var index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            var segment = text.Substring(run.Start, run.Length);
            if (run.Typeface is null)
            {
                canvas.DrawText(segment, left, y, SKTextAlign.Left, font, paint);
            }
            else
            {
                using var fallback = FallbackFont(font, run.Typeface);
                canvas.DrawText(segment, left, y, SKTextAlign.Left, fallback, paint);
            }

            left += widths[index];
        }
    }

    // The runs of text in the graph font, and in the fonts the system offers for the characters it has no glyph for.
    internal static IReadOnlyList<GraphTextRun> Runs(SKFont font, string text, FontFallbackCache cache)
    {
        var primary = font.Typeface ?? SKTypeface.Default;
        return Segment(text, codePoint => font.GetGlyph(codePoint) != 0, codePoint => cache.Find(primary, codePoint));
    }

    // Splits text into runs by the font each character is drawn in: null for the graph font, which keeps every
    // character it has a glyph for (and every control character), a fallback typeface for the others. Characters that
    // belong to the one before them - combining marks, joiners, variation selectors - stay in its run, and a surrogate
    // pair is never split. A character no font has stays in the graph font. Neighbouring runs of one font are one run.
    internal static IReadOnlyList<GraphTextRun> Segment(
        string text,
        Func<int, bool> hasGlyph,
        Func<int, SKTypeface?> fallback)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(hasGlyph);
        ArgumentNullException.ThrowIfNull(fallback);

        var runs = new List<GraphTextRun>();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.Utf16SequenceLength;
            SKTypeface? typeface;
            if (runs.Count > 0 && BelongsToPrevious(rune))
            {
                typeface = runs[^1].Typeface;
            }
            else if (Rune.IsControl(rune) || hasGlyph(rune.Value))
            {
                typeface = null;
            }
            else
            {
                typeface = fallback(rune.Value);
            }

            if (runs.Count > 0 && ReferenceEquals(runs[^1].Typeface, typeface))
            {
                runs[^1] = runs[^1] with { Length = runs[^1].Length + length };
            }
            else
            {
                runs.Add(new GraphTextRun(index, length, typeface));
            }

            index += length;
        }

        return runs;
    }

    private static bool BelongsToPrevious(Rune rune) =>
        Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        || rune.Value is 0x200C or 0x200D
        || rune.Value is >= 0xFE00 and <= 0xFE0F
        || rune.Value is >= 0xE0100 and <= 0xE01EF;

    private static float MeasureRun(SKFont font, string text, GraphTextRun run)
    {
        var segment = text.Substring(run.Start, run.Length);
        if (run.Typeface is null)
        {
            return font.MeasureText(segment);
        }

        using var fallback = FallbackFont(font, run.Typeface);
        return fallback.MeasureText(segment);
    }

    // The graph font's size and rendering settings over another typeface.
    private static SKFont FallbackFont(SKFont font, SKTypeface typeface) =>
        new(typeface, font.Size, font.ScaleX, font.SkewX)
        {
            Edging = font.Edging,
            Subpixel = font.Subpixel,
            Hinting = font.Hinting,
            LinearMetrics = font.LinearMetrics,
            EmbeddedBitmaps = font.EmbeddedBitmaps,
            ForceAutoHinting = font.ForceAutoHinting,
            BaselineSnap = font.BaselineSnap
        };
}

// A stretch of text drawn in one font: the graph font when Typeface is null.
internal readonly record struct GraphTextRun(int Start, int Length, SKTypeface? Typeface);

// Which typeface draws a character the graph font has no glyph for, asked once per font and character and remembered.
// Safe to use from the UI thread and from export work at the same time. Every character of one fallback family shares
// one typeface, so the cache holds a handful of typefaces however many characters it has seen.
internal sealed class FontFallbackCache
{
    // Han characters in Traditional Chinese forms (PM decision, Task #040.1); scripts a Traditional Chinese font does
    // not have (Hangul, symbols) still get the font Windows has for them.
    private static readonly string[] LocaleHint = ["zh-TW"];

    private readonly Func<SKTypeface, int, SKTypeface?> _match;
    private readonly ConcurrentDictionary<(Style Style, int CodePoint), SKTypeface?> _byCharacter = new();
    private readonly ConcurrentDictionary<Style, SKTypeface> _byFamily = new();

    public FontFallbackCache(Func<SKTypeface, int, SKTypeface?> match)
    {
        ArgumentNullException.ThrowIfNull(match);
        _match = match;
    }

    // The system's font manager, asked the way the graph font would ask it.
    public static FontFallbackCache Shared { get; } = new(
        (primary, codePoint) =>
            SKFontManager.Default.MatchCharacter(primary.FamilyName, primary.FontStyle, LocaleHint, codePoint));

    public int CharacterCount => _byCharacter.Count;

    public SKTypeface? Find(SKTypeface primary, int codePoint)
    {
        ArgumentNullException.ThrowIfNull(primary);

        return _byCharacter.GetOrAdd((Style.Of(primary), codePoint), _ => Canonical(_match(primary, codePoint)));
    }

    // One typeface per fallback family and style, whichever character found it first.
    private SKTypeface? Canonical(SKTypeface? typeface) =>
        typeface is null ? null : _byFamily.GetOrAdd(Style.Of(typeface), typeface);

    // A typeface as the cache tells typefaces apart: its family and style.
    private readonly record struct Style(string Family, int Weight, int Width, SKFontStyleSlant Slant)
    {
        public static Style Of(SKTypeface typeface) =>
            new(typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant);
    }
}
