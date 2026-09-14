using Avalonia.Input;

namespace YAT.app.Views;

// Tracks a left-button drag across worksheet column headers. View-only state: the window turns its changes into
// ViewModel selection calls (the drag-start column is selected on press, then the range follows the pointer).
public sealed class ColumnHeaderDragSelection
{
    public Guid? AnchorColumnId { get; private set; }

    public Guid? CurrentColumnId { get; private set; }

    public bool IsDragging => AnchorColumnId is not null;

    // A drag starts only from a plain left press on a column header: not on its resize grip, not with the right
    // button, and not with Ctrl/Cmd or Shift (those are click gestures handled on release).
    public bool TryStart(Guid? columnId, bool isLeftButton, bool isOnResizeGrip, KeyModifiers modifiers)
    {
        End();

        if (columnId is null
            || !isLeftButton
            || isOnResizeGrip
            || WorksheetKeyRouting.GetHeaderClick(modifiers) != ColumnHeaderClick.Select)
        {
            return false;
        }

        AnchorColumnId = columnId;
        CurrentColumnId = columnId;
        return true;
    }

    // Returns true when the pointer has moved onto a different column header during a drag.
    public bool TryMoveTo(Guid? columnId)
    {
        if (!IsDragging || columnId is null || columnId == CurrentColumnId)
        {
            return false;
        }

        CurrentColumnId = columnId;
        return true;
    }

    public void End()
    {
        AnchorColumnId = null;
        CurrentColumnId = null;
    }
}
