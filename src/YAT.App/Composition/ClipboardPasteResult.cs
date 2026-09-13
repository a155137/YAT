using YAT.Domain.Entities;

namespace YAT.app.Composition;

// Metadata-only outcome of a clipboard paste into one worksheet. It never carries parsed cells or raw values.
public sealed class ClipboardPasteResult
{
    internal static readonly ClipboardPasteResult NothingToPaste = new(isPasted: false, []);

    internal ClipboardPasteResult(bool isPasted, IReadOnlyList<WorksheetColumn> worksheetColumns)
    {
        IsPasted = isPasted;
        WorksheetColumns = worksheetColumns;
    }

    // False when the clipboard held no text to paste; nothing was persisted.
    public bool IsPasted { get; }

    // The worksheet's column metadata reloaded from the repository after the paste, ordered by Index.
    public IReadOnlyList<WorksheetColumn> WorksheetColumns { get; }
}
