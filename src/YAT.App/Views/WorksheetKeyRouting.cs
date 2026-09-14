using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace YAT.app.Views;

// Window-level worksheet shortcuts are decided on the bubbling KeyDown event, after the focused control had its turn:
// a control that handled the key, or any text-editing control, keeps the key for itself.
public static class WorksheetKeyRouting
{
    // Delete (without modifiers) removes the selected worksheet column, unless focus is inside a text box or the key
    // was already handled. Whether a column is selected is decided by the command's CanExecute.
    public static bool IsDeleteColumnGesture(Key key, KeyModifiers modifiers, bool handled, object? source) =>
        !handled
        && key == Key.Delete
        && modifiers == KeyModifiers.None
        && !IsInTextEditor(source);

    private static bool IsInTextEditor(object? source) =>
        source is Visual visual && visual.FindAncestorOfType<TextBox>(includeSelf: true) is not null;
}
