using System.Text;
using SkiaSharp;
using YAT.app.Graphs.Rendering;

namespace YAT.App.Tests;

// Graph text fallback (Task #040.1): text the graph font covers is drawn and measured by Skia exactly as before; text
// with characters the graph font lacks is split into runs, the missing characters drawn from a system font that has
// them, on one baseline, aligned and measured as one piece of text.
//
// The segmentation and the cache are checked with stand-in fonts, so they hold on any machine. The tests that draw
// Chinese, Japanese and Korean need those fonts installed (Windows has them) and are skipped where they are not.
public class GraphTextFallbackTests
{
    private static readonly SKTypeface Primary = SKTypeface.Default;

    private static SKFont Font(float size = 12f) => new() { Size = size, Edging = SKFontEdging.Antialias, Subpixel = true };

    private static readonly FontFallbackCache Shared = FontFallbackCache.Shared;

    private static bool HasCjkFonts => Shared.Find(Primary, '厚') is not null;

    private static void RequireCjkFonts() =>
        Assert.SkipUnless(HasCjkFonts, "No installed font has Chinese glyphs on this machine.");

    // ---- Segmentation (stand-in fonts) ----

    // Stand-ins: the graph font has everything below the CJK blocks except the combining mark U+1AB0; "Han" has CJK,
    // "Hangul" has Korean, nothing has U+E000. Two distinct typefaces stand in for the two fallback fonts.
    private static readonly SKTypeface Han = SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyle.Bold);
    private static readonly SKTypeface Hangul = SKTypeface.FromFamilyName(SKTypeface.Default.FamilyName, SKFontStyle.Italic);

    private static bool StandInHasGlyph(int codePoint) => codePoint < 0x2E80 && codePoint != 0x1AB0;

    private static SKTypeface? StandInFallback(int codePoint) => codePoint switch
    {
        >= 0xAC00 and <= 0xD7AF => Hangul,
        0xE000 => null,
        _ => Han
    };

    private static IReadOnlyList<(string Text, SKTypeface? Typeface)> Segment(string text) =>
        [.. GraphTextFallback.Segment(text, StandInHasGlyph, StandInFallback).Select(run => (text.Substring(run.Start, run.Length), run.Typeface))];

    [Theory]
    [InlineData("Wafer thickness")]
    [InlineData("µ ± σ ° Ω ≤ Å é")]
    [InlineData("")]
    public void TextTheGraphFontCoversIsOneRunInIt(string text)
    {
        var runs = Segment(text);

        if (text.Length == 0)
        {
            Assert.Empty(runs);
            return;
        }

        Assert.Equal([(text, (SKTypeface?)null)], runs);
    }

    [Fact]
    public void OnlyTheMissingCharactersFallBack()
    {
        Assert.Equal([("Wafer ", null), ("厚度", Han)], Segment("Wafer 厚度"));
        Assert.Equal([("Site A ", null), ("平均值", Han)], Segment("Site A 平均值"));
        Assert.Equal([("PS ", null), ("感度", Han), (" (µA)", null)], Segment("PS 感度 (µA)"));
        Assert.Equal([("產品", Han), ("A / Lot 12", null)], Segment("產品A / Lot 12"));
        Assert.Equal([("厚度平均值", Han)], Segment("厚度平均值"));
        Assert.Equal([("中文", Han), (" µ", null)], Segment("中文 µ"));
    }

    [Fact]
    public void EachFallbackFontHasItsOwnRun() =>
        Assert.Equal([("Lot ", null), ("가나", Hangul), ("批次", Han)], Segment("Lot 가나批次"));

    [Fact]
    public void ASurrogatePairIsNeverSplit()
    {
        var text = "Lot " + char.ConvertFromUtf32(0x20BB7) + char.ConvertFromUtf32(0x20BB8) + "A";

        var runs = GraphTextFallback.Segment(text, StandInHasGlyph, StandInFallback);

        Assert.Equal([new GraphTextRun(0, 4, null), new GraphTextRun(4, 4, Han), new GraphTextRun(8, 1, null)], runs);
        Assert.All(runs, run => Assert.False(char.IsLowSurrogate(text[run.Start])));
    }

    [Fact]
    public void CombiningMarksJoinersAndSelectorsStayWithTheCharacterBefore()
    {
        // A mark the graph font lacks after a character it has, and a mark after a fallback character.
        Assert.Equal([("e\u1AB0\u0301x", null)], Segment("e\u1AB0\u0301x"));
        Assert.Equal([("A", null), ("厚\u1AB0\u200D\uFE0F", Han), ("B", null)], Segment("A厚\u1AB0\u200D\uFE0FB"));
    }

    [Fact]
    public void ControlCharactersStayInTheGraphFontWithoutALookup()
    {
        var asked = new List<int>();
        var runs = GraphTextFallback.Segment("a\tb\u0001", _ => false, codePoint =>
        {
            asked.Add(codePoint);
            return Han;
        });

        Assert.Equal([new GraphTextRun(0, 1, Han), new GraphTextRun(1, 1, null), new GraphTextRun(2, 1, Han), new GraphTextRun(3, 1, null)], runs);
        Assert.Equal(['a', 'b'], asked);
    }

    [Fact]
    public void ACharacterNoFontHasStaysInTheGraphFont() =>
        Assert.Equal([("x\uE000y", null)], Segment("x\uE000y"));

    // ---- Cache ----

    [Fact]
    public void EachCharacterIsLookedUpOnceAndEveryCharacterOfAFamilySharesOneTypeface()
    {
        var calls = 0;
        var cache = new FontFallbackCache((_, codePoint) =>
        {
            Interlocked.Increment(ref calls);
            return codePoint == 0xE000 ? null : Han;
        });

        var first = cache.Find(Primary, '厚');
        var again = cache.Find(Primary, '厚');
        var other = cache.Find(Primary, '度');
        Assert.Null(cache.Find(Primary, 0xE000));
        Assert.Null(cache.Find(Primary, 0xE000));

        Assert.NotNull(first);
        Assert.Same(first, again);
        Assert.Same(first, other);
        Assert.Equal(3, calls);
        Assert.Equal(3, cache.CharacterCount);
    }

    [Fact]
    public void TheCacheCanBeUsedFromManyThreadsAtOnce()
    {
        var cache = new FontFallbackCache((_, codePoint) => codePoint % 2 == 0 ? Han : Hangul);
        var found = new System.Collections.Concurrent.ConcurrentBag<(int, SKTypeface?)>();

        Parallel.For(0, 4000, index =>
        {
            var codePoint = 0x4E00 + (index % 64);
            found.Add((codePoint, cache.Find(Primary, codePoint)));
        });

        Assert.Equal(64, cache.CharacterCount);
        Assert.All(found.GroupBy(item => item.Item1), group => Assert.Single(group.Select(item => item.Item2).Distinct()));
        Assert.Equal(2, found.Select(item => item.Item2).Distinct().Count());
    }

    // ---- Fast path: text the graph font covers is Skia's own ----

    public static TheoryData<string> CoveredTexts =>
    [
        "Histogram of Reg1", "Frequency", "0.051164035", "… 20 more", "Lot with a rather long name 01", "µ ± σ ° Ω ≤ ≥ × √ ∑ ‰ ² ³", "Å é ü ñ"
    ];

    [Theory]
    [MemberData(nameof(CoveredTexts))]
    public void CoveredTextIsMeasuredAndDrawnByTheGraphFontItself(string text)
    {
        using var font = Font();
        Assert.True(font.ContainsGlyphs(text));
        Assert.Equal(font.MeasureText(text), GraphTextFallback.MeasureText(font, text));

        foreach (var align in new[] { SKTextAlign.Left, SKTextAlign.Center, SKTextAlign.Right })
        {
            using var expected = Draw(canvas => canvas.DrawText(text, 150, 30, align, font, Paint));
            using var actual = Draw(canvas => GraphTextFallback.DrawText(canvas, text, 150, 30, align, font, Paint));
            Assert.Equal(expected.Bytes, actual.Bytes);
        }
    }

    // ---- System fonts: Chinese, Japanese, Korean, full width, symbols ----

    public static TheoryData<string> FallbackTexts =>
    [
        "Wafer 厚度", "Site A 平均值", "PS 感度 (µA)", "產品A / Lot 12", "厚度平均值標準差", "中文 µm", "批次 12 / 站點 3",
        "あいう カタカナ", "가나다 Lot", "ＡＢＣ（１２）", "⌀ 5 mm", "厚度 ⌀ 가"
    ];

    [Theory]
    [MemberData(nameof(FallbackTexts))]
    public void EveryRunHasEveryGlyphItDraws(string text)
    {
        RequireCjkFonts();
        using var font = Font();
        Assert.False(font.ContainsGlyphs(text));

        foreach (var run in GraphTextFallback.Runs(font, text, Shared))
        {
            var segment = text.Substring(run.Start, run.Length);
            using var runFont = run.Typeface is null ? null : new SKFont(run.Typeface, font.Size);
            var drawnWith = runFont ?? font;
            Assert.True(
                segment.EnumerateRunes().All(rune => Rune.IsWhiteSpace(rune) || drawnWith.GetGlyph(rune.Value) != 0),
                $"'{segment}' in {(run.Typeface?.FamilyName ?? "the graph font")} has a missing glyph");
        }
    }

    [Fact]
    public void CharactersTheGraphFontHasStayInIt()
    {
        RequireCjkFonts();
        using var font = Font();

        var runs = GraphTextFallback.Runs(font, "PS 感度 (µA)", Shared);

        Assert.Equal(3, runs.Count);
        Assert.Null(runs[0].Typeface);
        Assert.NotNull(runs[1].Typeface);
        Assert.Null(runs[2].Typeface);
        Assert.Equal(" (µA)", "PS 感度 (µA)".Substring(runs[2].Start, runs[2].Length));
    }

    [Fact]
    public void HanCharactersAreDrawnInTraditionalChineseForms()
    {
        RequireCjkFonts();
        using var traditional = SKFontManager.Default.MatchCharacter(Primary.FamilyName, Primary.FontStyle, ["zh-TW"], '厚');

        Assert.Equal(traditional!.FamilyName, Shared.Find(Primary, '厚')!.FamilyName);
        Assert.Equal(traditional.FamilyName, Shared.Find(Primary, '值')!.FamilyName);
    }

    [Theory]
    [MemberData(nameof(FallbackTexts))]
    public void FallbackTextIsNeverDrawnAsEmptyBoxes(string text)
    {
        RequireCjkFonts();
        using var font = Font(16);

        using var boxes = Draw(canvas => canvas.DrawText(text, 10, 40, SKTextAlign.Left, font, Paint));
        using var drawn = Draw(canvas => GraphTextFallback.DrawText(canvas, text, 10, 40, SKTextAlign.Left, font, Paint));
        using var expected = Draw(canvas =>
        {
            var left = 10f;
            foreach (var run in GraphTextFallback.Runs(font, text, Shared))
            {
                var segment = text.Substring(run.Start, run.Length);
                using var runFont = run.Typeface is null ? null : new SKFont(run.Typeface, font.Size) { Edging = font.Edging, Subpixel = true };
                canvas.DrawText(segment, left, 40, SKTextAlign.Left, runFont ?? font, Paint);
                left += (runFont ?? font).MeasureText(segment);
            }
        });

        Assert.Equal(expected.Bytes, drawn.Bytes);
        Assert.NotEqual(boxes.Bytes, drawn.Bytes);
    }

    // Measuring and drawing agree: the text drawn right-aligned at x + width, or centred at x + width / 2, lands on
    // exactly the pixels it covers left-aligned at x.
    [Theory]
    [MemberData(nameof(FallbackTexts))]
    public void AlignmentUsesTheWidthTheTextIsMeasuredAt(string text)
    {
        RequireCjkFonts();
        using var font = Font();
        var width = GraphTextFallback.MeasureText(font, text);

        using var left = Draw(canvas => GraphTextFallback.DrawText(canvas, text, 20, 30, SKTextAlign.Left, font, Paint));
        using var right = Draw(canvas => GraphTextFallback.DrawText(canvas, text, 20 + width, 30, SKTextAlign.Right, font, Paint));
        using var centre = Draw(canvas => GraphTextFallback.DrawText(canvas, text, 20 + (width / 2f), 30, SKTextAlign.Center, font, Paint));

        Assert.Equal(left.Bytes, right.Bytes);
        Assert.Equal(left.Bytes, centre.Bytes);

        // Everything drawn lies within the measured advance (a pixel of antialiasing either side).
        var (inkLeft, inkRight) = InkColumns(left);
        Assert.True(inkLeft >= 19 && inkRight <= 20 + width + 1, $"ink {inkLeft}..{inkRight} outside 20..{20 + width}");
    }

    [Fact]
    public void FallbackTextIsMeasuredAtTheWidthOfItsRealGlyphs()
    {
        RequireCjkFonts();
        using var font = Font();

        // The empty boxes of the graph font are narrower than Chinese characters.
        Assert.True(GraphTextFallback.MeasureText(font, "Wafer 厚度") > font.MeasureText("Wafer 厚度"));
        Assert.Equal(font.MeasureText("Wafer "), GraphTextFallback.MeasureText(font, "Wafer "));
    }

    // Line height stays the graph font's: at every size the theme uses, Chinese glyphs sit inside the line box the
    // layout makes for the graph font, so they are not clipped.
    [Theory]
    [InlineData(11f)]
    [InlineData(12f)]
    [InlineData(16f)]
    public void FallbackGlyphsFitTheGraphFontsLine(float size)
    {
        RequireCjkFonts();
        using var font = Font(size);
        const string Text = "厚度平均值感產品標準差批次站點龘あ가ＡＢ（）";

        foreach (var run in GraphTextFallback.Runs(font, Text, Shared).Where(run => run.Typeface is not null))
        {
            using var runFont = new SKFont(run.Typeface, size);
            var glyphs = runFont.GetGlyphs(Text.Substring(run.Start, run.Length));
            var bounds = new SKRect[glyphs.Length];
            runFont.GetGlyphWidths(glyphs, null, bounds);

            Assert.True(bounds.Min(b => b.Top) >= font.Metrics.Ascent, $"{run.Typeface!.FamilyName} rises above the line at {size}");
            Assert.True(bounds.Max(b => b.Bottom) <= font.Metrics.Descent, $"{run.Typeface!.FamilyName} drops below the line at {size}");
        }
    }

    [Fact]
    public void RotatedTextIsDrawnAlongTheRotatedBaseline()
    {
        RequireCjkFonts();
        using var font = Font();
        const string Text = "厚度 (µm)";
        var width = GraphTextFallback.MeasureText(font, Text);

        using var bitmap = Draw(canvas =>
        {
            canvas.Translate(40, 100);
            canvas.RotateDegrees(-90);
            GraphTextFallback.DrawText(canvas, Text, 0, 0, SKTextAlign.Center, font, Paint);
        });

        // Turned a quarter turn: the text runs up the column left of x = 40, centred on y = 100.
        var rows = Enumerable.Range(0, bitmap.Height).Where(y => Enumerable.Range(0, bitmap.Width).Any(x => bitmap.GetPixel(x, y).Alpha > 0)).ToList();
        var columns = Enumerable.Range(0, bitmap.Width).Where(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).Alpha > 0)).ToList();
        Assert.True(rows.Min() >= 100 - (width / 2f) - 1 && rows.Max() <= 100 + (width / 2f) + 1);
        Assert.True(columns.Max() <= 40 + font.Metrics.Descent + 1, $"ink reaches x = {columns.Max()}");
    }

    [Fact]
    public void AMissingCharacterNoFontHasIsStillDrawnWithoutFailing()
    {
        using var font = Font();
        var none = new FontFallbackCache((_, _) => null);

        using var drawn = Draw(canvas => GraphTextFallback.DrawText(canvas, "Lot 厚", 10, 30, SKTextAlign.Left, font, Paint, none));
        using var boxes = Draw(canvas => canvas.DrawText("Lot 厚", 10, 30, SKTextAlign.Left, font, Paint));

        Assert.Equal(boxes.Bytes, drawn.Bytes);
        Assert.Equal(font.MeasureText("Lot 厚"), GraphTextFallback.MeasureText(font, "Lot 厚", none));
    }

    // ---- Truncation ----

    [Fact]
    public void TruncationNeverCutsASurrogatePair()
    {
        using var font = Font();
        var text = "Lot " + string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x20BB7), 6));

        for (var width = 0f; width <= GraphTextFallback.MeasureText(font, text) + 2; width += 0.5f)
        {
            var cut = SkiaGraphRenderer.Ellipsize(text, font, width);
            var body = cut.EndsWith('…') ? cut[..^1] : cut;
            Assert.False(body.Length > 0 && char.IsHighSurrogate(body[^1]), $"cut at {width} leaves half a character: {cut}");
            Assert.True(cut.Length == 0 || GraphTextFallback.MeasureText(font, cut) <= width || cut == "…");
        }
    }

    // What Ellipsize returned before #040.1, for text the graph font covers.
    private static string EllipsizeAsBefore(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width)
        {
            return text;
        }

        for (var length = text.Length - 1; length > 0; length--)
        {
            var candidate = text[..length] + "…";
            if (font.MeasureText(candidate) <= width)
            {
                return candidate;
            }
        }

        return font.MeasureText("…") <= width ? "…" : string.Empty;
    }

    [Theory]
    [InlineData("Lot with a rather long name 01")]
    [InlineData("Fabrication lot")]
    [InlineData("Statistics")]
    [InlineData("µA ± σ")]
    public void TruncatingCoveredTextIsUnchanged(string text)
    {
        using var font = Font(11);
        for (var width = 0f; width <= font.MeasureText(text) + 2; width += 0.25f)
        {
            Assert.Equal(EllipsizeAsBefore(text, font, width), SkiaGraphRenderer.Ellipsize(text, font, width));
        }
    }

    [Fact]
    public void ChineseTextIsTruncatedByTheWidthItIsDrawnAt()
    {
        RequireCjkFonts();
        using var font = Font(11);
        const string Text = "批次 產品 A 標準差 平均值";

        for (var width = 10f; width <= 200f; width += 3f)
        {
            var cut = SkiaGraphRenderer.Ellipsize(Text, font, width);
            Assert.True(cut.Length == 0 || GraphTextFallback.MeasureText(font, cut) <= width, $"'{cut}' is wider than {width}");
        }
    }

    // ---- Helpers ----

    private static readonly SKPaint Paint = new() { IsAntialias = true, Color = SKColors.Black };

    private static SKBitmap Draw(Action<SKCanvas> draw)
    {
        var bitmap = new SKBitmap(new SKImageInfo(320, 200, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        draw(canvas);
        return bitmap;
    }

    private static (int Left, int Right) InkColumns(SKBitmap bitmap)
    {
        var columns = Enumerable.Range(0, bitmap.Width).Where(x => Enumerable.Range(0, bitmap.Height).Any(y => bitmap.GetPixel(x, y).Alpha > 0)).ToList();
        return (columns.Min(), columns.Max());
    }
}
