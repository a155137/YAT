using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using SkiaSharp;

namespace YAT.app.Views.WorksheetGrid;

// Self-contained text renderer for the virtualized Skia worksheet grid.
// Keeps existing GraphTextFallback ownership untouched, caches fallback SKFont/SKTypeface entries,
// and supports fast ASCII span rendering + ellipsis truncation within cell bounds.
internal sealed class WorksheetTextRenderer : IDisposable
{
    private static readonly string[] LocaleHint = ["zh-TW"];
    private const string Ellipsis = "…";

    [ThreadStatic]
    private static List<Run>? t_scratchRuns;

    private readonly Func<SKTypeface, int, SKTypeface?> _matchCharacter;
    private readonly ConcurrentDictionary<(Style Key, int CodePoint), SKFont?> _byCodePoint = new();
    private readonly ConcurrentDictionary<Style, FallbackFontEntry> _byFamily = new();
    private int _disposed;

    public WorksheetTextRenderer()
        : this((primary, codePoint) =>
            SKFontManager.Default.MatchCharacter(primary.FamilyName, primary.FontStyle, LocaleHint, codePoint))
    {
    }

    internal WorksheetTextRenderer(Func<SKTypeface, int, SKTypeface?> matchCharacter)
    {
        _matchCharacter = matchCharacter ?? throw new ArgumentNullException(nameof(matchCharacter));
    }

    internal int CachedFamilyCount => _byFamily.Count;

    public void DrawClippedText(
        SKCanvas canvas,
        ReadOnlySpan<char> text,
        float x,
        float baselineY,
        float maxWidth,
        SKTextAlign align,
        SKFont primaryFont,
        SKPaint paint)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (text.IsEmpty || maxWidth <= 1f)
        {
            return;
        }

        if (IsAllAscii(text))
        {
            DrawAsciiClipped(canvas, text, x, baselineY, maxWidth, align, primaryFont, paint);
            return;
        }

        var textString = text.ToString();
        if (primaryFont.ContainsGlyphs(textString))
        {
            DrawSingleFontClipped(canvas, textString, x, baselineY, maxWidth, align, primaryFont, paint);
            return;
        }

        DrawFallbackRunsClipped(canvas, textString, x, baselineY, maxWidth, align, primaryFont, paint);
    }

    private static bool IsAllAscii(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    private static void DrawAsciiClipped(
        SKCanvas canvas,
        ReadOnlySpan<char> text,
        float x,
        float baselineY,
        float maxWidth,
        SKTextAlign align,
        SKFont font,
        SKPaint paint)
    {
        var fullWidth = font.MeasureText(text);
        if (fullWidth <= maxWidth)
        {
            canvas.DrawText(text.ToString(), x, baselineY, align, font, paint);
            return;
        }

        var ellipsisWidth = font.MeasureText(Ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            return;
        }

        var targetWidth = maxWidth - ellipsisWidth;
        var fitCount = FitPrefixLength(text, targetWidth, c => font.MeasureText(c));
        if (fitCount <= 0)
        {
            canvas.DrawText(Ellipsis, x, baselineY, align, font, paint);
            return;
        }

        var truncated = string.Concat(text[..fitCount], Ellipsis);
        canvas.DrawText(truncated, x, baselineY, align, font, paint);
    }

    private static void DrawSingleFontClipped(
        SKCanvas canvas,
        string text,
        float x,
        float baselineY,
        float maxWidth,
        SKTextAlign align,
        SKFont font,
        SKPaint paint)
    {
        var fullWidth = font.MeasureText(text);
        if (fullWidth <= maxWidth)
        {
            canvas.DrawText(text, x, baselineY, align, font, paint);
            return;
        }

        var ellipsisWidth = font.MeasureText(Ellipsis);
        if (ellipsisWidth >= maxWidth)
        {
            return;
        }

        var fitCount = FitPrefixLength(text.AsSpan(), maxWidth - ellipsisWidth, slice => font.MeasureText(slice));
        var truncated = fitCount > 0 ? string.Concat(text.AsSpan(0, fitCount), Ellipsis) : Ellipsis;
        canvas.DrawText(truncated, x, baselineY, align, font, paint);
    }

    private void DrawFallbackRunsClipped(
        SKCanvas canvas,
        string text,
        float x,
        float baselineY,
        float maxWidth,
        SKTextAlign align,
        SKFont primaryFont,
        SKPaint paint)
    {
        var totalWidth = MeasureWithFallback(primaryFont, text);
        string toDraw;
        if (totalWidth <= maxWidth)
        {
            toDraw = text;
        }
        else
        {
            var ellipsisWidth = primaryFont.MeasureText(Ellipsis);
            if (ellipsisWidth >= maxWidth)
            {
                return;
            }

            var targetWidth = maxWidth - ellipsisWidth;
            var fitChars = 0;
            var acc = 0f;
            foreach (var rune in text.EnumerateRunes())
            {
                var len = rune.Utf16SequenceLength;
                var seg = text.AsSpan(fitChars, len);
                var w = MeasureRune(primaryFont, rune, seg);
                if (acc + w > targetWidth)
                {
                    break;
                }

                acc += w;
                fitChars += len;
            }

            toDraw = fitChars > 0 ? string.Concat(text.AsSpan(0, fitChars), Ellipsis) : Ellipsis;
            totalWidth = acc + ellipsisWidth;
        }

        var scratchRuns = t_scratchRuns ??= [];
        BuildRuns(primaryFont, toDraw, scratchRuns);
        var left = align switch
        {
            SKTextAlign.Center => x - (totalWidth / 2f),
            SKTextAlign.Right => x - totalWidth,
            _ => x
        };

        for (var i = 0; i < scratchRuns.Count; i++)
        {
            var run = scratchRuns[i];
            var segment = toDraw.Substring(run.Start, run.Length);
            var font = run.Font ?? primaryFont;
            var width = font.MeasureText(segment);
            canvas.DrawText(segment, left, baselineY, SKTextAlign.Left, font, paint);
            left += width;
        }
    }

    private float MeasureWithFallback(SKFont primaryFont, string text)
    {
        var scratchRuns = t_scratchRuns ??= [];
        BuildRuns(primaryFont, text, scratchRuns);
        var total = 0f;
        for (var i = 0; i < scratchRuns.Count; i++)
        {
            var run = scratchRuns[i];
            var font = run.Font ?? primaryFont;
            total += font.MeasureText(text.AsSpan(run.Start, run.Length));
        }

        return total;
    }

    private float MeasureRune(SKFont primaryFont, Rune rune, ReadOnlySpan<char> segment)
    {
        if (Rune.IsControl(rune) || primaryFont.GetGlyph(rune.Value) != 0)
        {
            return primaryFont.MeasureText(segment);
        }

        var fallback = GetFallbackFont(primaryFont, rune.Value) ?? primaryFont;
        return fallback.MeasureText(segment);
    }

    private void BuildRuns(SKFont primaryFont, string text, List<Run> runs)
    {
        runs.Clear();
        var index = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var length = rune.Utf16SequenceLength;
            SKFont? font;
            if (runs.Count > 0 && BelongsToPrevious(rune))
            {
                font = runs[^1].Font;
            }
            else if (Rune.IsControl(rune) || primaryFont.GetGlyph(rune.Value) != 0)
            {
                font = null;
            }
            else
            {
                font = GetFallbackFont(primaryFont, rune.Value);
            }

            if (runs.Count > 0 && ReferenceEquals(runs[^1].Font, font))
            {
                runs[^1] = runs[^1] with { Length = runs[^1].Length + length };
            }
            else
            {
                runs.Add(new Run(index, length, font));
            }

            index += length;
        }
    }

    private SKFont? GetFallbackFont(SKFont primaryFont, int codePoint)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return null;
        }

        var primary = primaryFont.Typeface ?? SKTypeface.Default;
        var key = Style.Of(primary, primaryFont.Size);
        return _byCodePoint.GetOrAdd((key, codePoint), k =>
        {
            var matched = _matchCharacter(primary, k.CodePoint);
            if (matched is null)
            {
                return null;
            }

            var matchedStyle = Style.Of(matched, k.Key.Size);
            if (_byFamily.TryGetValue(matchedStyle, out var existing))
            {
                if (!ReferenceEquals(matched, SKTypeface.Default) && !ReferenceEquals(matched, existing.Typeface))
                {
                    matched.Dispose();
                }

                return existing.Font;
            }

            var created = new FallbackFontEntry(matched, new SKFont(matched, k.Key.Size));
            var actual = _byFamily.GetOrAdd(matchedStyle, created);
            if (!ReferenceEquals(actual, created))
            {
                created.Font.Dispose();
                if (!ReferenceEquals(created.Typeface, SKTypeface.Default) && !ReferenceEquals(created.Typeface, actual.Typeface))
                {
                    created.Typeface.Dispose();
                }
            }

            return actual.Font;
        });
    }

    private delegate float MeasureSpan(ReadOnlySpan<char> slice);

    private static int FitPrefixLength(ReadOnlySpan<char> text, float maxWidth, MeasureSpan measure)
    {
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (measure(text[..mid]) <= maxWidth)
            {
                low = mid;
            }
            else
            {
                high = mid - 1;
            }
        }

        return low;
    }

    private static bool BelongsToPrevious(Rune rune) =>
        Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
        || rune.Value is 0x200C or 0x200D
        || rune.Value is >= 0xFE00 and <= 0xFE0F
        || rune.Value is >= 0xE0100 and <= 0xE01EF;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var ownedTypefaces = new HashSet<SKTypeface>(ReferenceEqualityComparer.Instance);
        foreach (var entry in _byFamily.Values)
        {
            entry.Font.Dispose();
            if (!ReferenceEquals(entry.Typeface, SKTypeface.Default))
            {
                ownedTypefaces.Add(entry.Typeface);
            }
        }

        foreach (var typeface in ownedTypefaces)
        {
            typeface.Dispose();
        }

        _byFamily.Clear();
        _byCodePoint.Clear();
    }

    private sealed record FallbackFontEntry(SKTypeface Typeface, SKFont Font);

    private readonly record struct Run(int Start, int Length, SKFont? Font);

    private readonly record struct Style(string Family, int Weight, int Width, SKFontStyleSlant Slant, float Size)
    {
        public static Style Of(SKTypeface typeface, float size) =>
            new(typeface.FamilyName, typeface.FontWeight, typeface.FontWidth, typeface.FontSlant, size);
    }
}
