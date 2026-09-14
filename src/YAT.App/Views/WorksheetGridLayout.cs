using System.Runtime.CompilerServices;
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

    // Class on every header element; selected column headers also carry SelectedClass, the active one ActiveClass too.
    public const string HeaderClass = "yat-grid-header";
    public const string SelectedClass = "selected";
    public const string ActiveClass = "active";

    // Approximate width of one header character at the grid font size, plus the header's horizontal padding.
    private const double CharacterWidth = 7.5;
    private const double HeaderChrome = 20;

    private const double NumericMinWidth = 88;
    private const double NumericMaxWidth = 160;
    private const double TextMinWidth = 96;
    private const double TextMaxWidth = 240;

    // Theme resources, bound dynamically so headers follow light/dark theme changes.
    private const string HeaderBackgroundKey = "SystemControlBackgroundChromeMediumLowBrush";

    // Grid lines for headers and cells (cells use it in MainWindow.axaml): the theme's low-contrast base brush.
    public const string GridLineBrushKey = "SystemControlForegroundBaseLowBrush";
    private const string AccentBrushKey = "SystemControlHighlightAccentBrush";
    private const string AccentColorKey = "SystemAccentColor";
    private const double SelectedTintOpacity = 0.14;
    private const double ActiveTintOpacity = 0.30;

    // The accent resource bindings currently applied to each header's inner element.
    private static readonly ConditionalWeakTable<Border, List<IDisposable>> MarkerBindings = new();

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
    // Structure: an outer border drawing the spreadsheet grid lines (right and bottom), holding an inner border that
    // carries the selection tint and accent bar, holding the header text.
    public static Border CreateHeader(WorksheetGridColumn? column)
    {
        var marker = new Border
        {
            Padding = new Thickness(6, 2),
            BorderThickness = new Thickness(0, 0, 0, 2),
            BorderBrush = Brushes.Transparent,
            Child = new TextBlock
            {
                Text = column?.Name ?? string.Empty,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        var header = new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = marker
        };
        header.Classes.Add(HeaderClass);
        header.Bind(Border.BackgroundProperty, header.GetResourceObservable(HeaderBackgroundKey));
        header.Bind(Border.BorderBrushProperty, header.GetResourceObservable(GridLineBrushKey));

        SetHeaderState(header, ColumnHeaderState.Normal);
        return header;
    }

    // The header state of a column given the current selection: the active column wins over plain selection.
    public static ColumnHeaderState GetHeaderState(Guid columnId, Guid? activeColumnId, IReadOnlySet<Guid> selectedColumnIds) =>
        columnId == activeColumnId ? ColumnHeaderState.Active
        : selectedColumnIds.Contains(columnId) ? ColumnHeaderState.Selected
        : ColumnHeaderState.Normal;

    // Normal: chrome only. Selected: a light accent tint and semibold text. Active (selected, and the Ctrl+V start):
    // a stronger accent tint plus the accent underline bar.
    public static void SetHeaderState(Border header, ColumnHeaderState state)
    {
        ArgumentNullException.ThrowIfNull(header);

        header.Classes.Set(SelectedClass, state != ColumnHeaderState.Normal);
        header.Classes.Set(ActiveClass, state == ColumnHeaderState.Active);
        if (header.Child is not Border { Child: TextBlock text } marker)
        {
            return;
        }

        // Resource bindings from the previous state must be disposed: a live binding re-applies its accent brush
        // whenever the resource observable publishes again (e.g. when the header is re-attached).
        var bindings = MarkerBindings.GetOrCreateValue(marker);
        bindings.ForEach(binding => binding.Dispose());
        bindings.Clear();

        if (state == ColumnHeaderState.Normal)
        {
            marker.Background = null;
        }
        else
        {
            var opacity = state == ColumnHeaderState.Active ? ActiveTintOpacity : SelectedTintOpacity;
            bindings.Add(marker.Bind(
                Border.BackgroundProperty,
                marker.GetResourceObservable(AccentColorKey, color => color is Color accent ? new SolidColorBrush(accent, opacity) : null)));
        }

        if (state == ColumnHeaderState.Active)
        {
            bindings.Add(marker.Bind(Border.BorderBrushProperty, marker.GetResourceObservable(AccentBrushKey)));
        }
        else
        {
            marker.BorderBrush = Brushes.Transparent;
        }

        text.FontWeight = state == ColumnHeaderState.Normal ? FontWeight.Normal : FontWeight.SemiBold;
    }

    public static ColumnHeaderState GetHeaderState(Border header) =>
        header.Classes.Contains(ActiveClass) ? ColumnHeaderState.Active
        : header.Classes.Contains(SelectedClass) ? ColumnHeaderState.Selected
        : ColumnHeaderState.Normal;
    public static string GetHeaderText(Border header) =>
        header.Child is Border { Child: TextBlock text } ? text.Text ?? string.Empty : string.Empty;

    public static FontWeight GetHeaderFontWeight(Border header) =>
        header.Child is Border { Child: TextBlock text } ? text.FontWeight : FontWeight.Normal;
}

public enum ColumnHeaderState
{
    Normal,
    Selected,
    Active
}
