using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using YAT.app.Composition;
using YAT.Domain.Enums;

namespace YAT.app.Views;

// Presentation rules for the worksheet grid: initial column widths, cell alignment and column header elements.
// Deterministic and data-independent (only the column name and data type are used); users can still resize.
public static class WorksheetGridLayout
{
    public const double RowNumberColumnWidth = 48;

    // Class on every header element; the selected paste-target column's header also carries PasteTargetClass.
    public const string HeaderClass = "yat-grid-header";
    public const string PasteTargetClass = "paste-target";

    // Approximate width of one header character at the grid font size, plus the header's horizontal padding.
    private const double CharacterWidth = 7.5;
    private const double HeaderChrome = 20;

    private const double NumericMinWidth = 88;
    private const double NumericMaxWidth = 160;
    private const double TextMinWidth = 96;
    private const double TextMaxWidth = 240;

    // Theme resources, bound dynamically so headers follow light/dark theme changes.
    private const string HeaderBackgroundKey = "SystemControlBackgroundChromeMediumLowBrush";
    private const string HeaderSeparatorKey = "SystemControlForegroundBaseLowBrush";
    private const string AccentBrushKey = "SystemControlHighlightAccentBrush";
    private const string AccentColorKey = "SystemAccentColor";
    private const double PasteTargetTintOpacity = 0.18;

    // Wide enough for the header name, within a range per data type: numbers are compact, text gets more room.
    public static double GetColumnWidth(WorksheetGridColumn column)
    {
        ArgumentNullException.ThrowIfNull(column);

        var (minimum, maximum) = column.DataType == WorksheetDataType.Numeric
            ? (NumericMinWidth, NumericMaxWidth)
            : (TextMinWidth, TextMaxWidth);

        return Math.Clamp(Math.Ceiling(column.Name.Length * CharacterWidth + HeaderChrome), minimum, maximum);
    }

    // Numbers align right so digits line up; text (and metadata-only non-numeric types) aligns left.
    public static HorizontalAlignment GetCellAlignment(WorksheetDataType dataType) =>
        dataType == WorksheetDataType.Numeric ? HorizontalAlignment.Right : HorizontalAlignment.Left;

    // Header content is styled here rather than by window styles: TableView hosts it outside the window's logical
    // tree, where those styles do not reach. Pass no column for the (empty) row-number header.
    public static Border CreateHeader(WorksheetGridColumn? column)
    {
        var header = new Border
        {
            Padding = new Thickness(6, 3),
            BorderThickness = new Thickness(0, 0, 0, 2),
            Child = new TextBlock
            {
                Text = column?.Name ?? string.Empty,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        header.Classes.Add(HeaderClass);
        SetPasteTarget(header, false);
        return header;
    }

    // The paste target stands out with an accent tint, an accent underline bar and semibold text.
    public static void SetPasteTarget(Border header, bool isPasteTarget)
    {
        ArgumentNullException.ThrowIfNull(header);

        header.Classes.Set(PasteTargetClass, isPasteTarget);
        header.Bind(
            Border.BackgroundProperty,
            isPasteTarget
                ? header.GetResourceObservable(AccentColorKey, color => color is Color accent ? new SolidColorBrush(accent, PasteTargetTintOpacity) : null)
                : header.GetResourceObservable(HeaderBackgroundKey));
        header.Bind(Border.BorderBrushProperty, header.GetResourceObservable(isPasteTarget ? AccentBrushKey : HeaderSeparatorKey));

        if (header.Child is TextBlock text)
        {
            text.FontWeight = isPasteTarget ? FontWeight.SemiBold : FontWeight.Normal;
        }
    }

    public static bool IsPasteTarget(Border header) => header.Classes.Contains(PasteTargetClass);
}
