using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace YAT.app.Views;

// Window-level worksheet shortcuts are decided on the bubbling KeyDown event, after the focused control had its turn:
// a control that handled the key, or any text-editing control, keeps the key for itself.
public static class WorksheetKeyRouting
{
    // Delete (without modifiers) removes the selected worksheet columns, unless focus is inside a text box or the key
    // was already handled. Whether any column is selected is decided by the command's CanExecute.
    public static bool IsDeleteColumnsGesture(Key key, KeyModifiers modifiers, bool handled, object? source) =>
        !handled
        && key == Key.Delete
        && modifiers == KeyModifiers.None
        && !IsInTextEditor(source);

    // Header click modifiers: Shift extends a range from the active column, Ctrl (Cmd on macOS) toggles one column,
    // a plain click selects only that column. Shift takes precedence when both are held.
    public static ColumnHeaderClick GetHeaderClick(KeyModifiers modifiers) =>
        modifiers.HasFlag(KeyModifiers.Shift) ? ColumnHeaderClick.Extend
        : modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta) ? ColumnHeaderClick.Toggle
        : ColumnHeaderClick.Select;

    private static bool IsInTextEditor(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null;
}

public enum ColumnHeaderClick
{
    Select,
    Toggle,
    Extend
}
