using Avalonia.Styling;
using SkiaSharp;

namespace YAT.app.Views.WorksheetGrid;

internal sealed record WorksheetTheme(
    SKColor Background,
    SKColor HeaderBackground,
    SKColor RowNumberBackground,
    SKColor GridLine,
    SKColor Text,
    SKColor HeaderText,
    SKColor RowNumberText,
    SKColor SelectedColumnTint,
    SKColor ActiveColumnTint,
    SKColor ActiveAccentBar)
{
    public static WorksheetTheme Light { get; } = new(
        Background: new SKColor(0xFF, 0xFF, 0xFF),
        HeaderBackground: new SKColor(0xF3, 0xF3, 0xF3),
        RowNumberBackground: new SKColor(0xF8, 0xF8, 0xF8),
        GridLine: new SKColor(0xE0, 0xE0, 0xE0),
        Text: new SKColor(0x1B, 0x1B, 0x1B),
        HeaderText: new SKColor(0x1B, 0x1B, 0x1B),
        RowNumberText: new SKColor(0x61, 0x61, 0x61),
        SelectedColumnTint: new SKColor(0x00, 0x78, 0xD4, 0x1A),
        ActiveColumnTint: new SKColor(0x00, 0x78, 0xD4, 0x4D),
        ActiveAccentBar: new SKColor(0x00, 0x78, 0xD4));

    public static WorksheetTheme Dark { get; } = new(
        Background: new SKColor(0x1F, 0x1F, 0x1F),
        HeaderBackground: new SKColor(0x2B, 0x2B, 0x2B),
        RowNumberBackground: new SKColor(0x25, 0x25, 0x25),
        GridLine: new SKColor(0x3A, 0x3A, 0x3A),
        Text: new SKColor(0xF3, 0xF3, 0xF3),
        HeaderText: new SKColor(0xF3, 0xF3, 0xF3),
        RowNumberText: new SKColor(0xA6, 0xA6, 0xA6),
        SelectedColumnTint: new SKColor(0x4C, 0xC2, 0xFF, 0x1F),
        ActiveColumnTint: new SKColor(0x4C, 0xC2, 0xFF, 0x4D),
        ActiveAccentBar: new SKColor(0x4C, 0xC2, 0xFF));

    public static WorksheetTheme For(ThemeVariant? variant) =>
        variant == ThemeVariant.Dark ? Dark : Light;
}
