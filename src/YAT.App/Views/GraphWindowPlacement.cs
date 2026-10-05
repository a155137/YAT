using Avalonia;

namespace YAT.app.Views;

// Where a graph window is put when it is chosen in the Graphs list (Task #061): where it is, when it lies wholly inside
// its screen's working area; otherwise moved the least way that brings it inside - and, when it is larger than the area,
// with its top left corner (its title bar) in view. Windows that open beyond the screen - a cascade of fifty separate
// graphs does - are found this way; where windows first open does not change.
internal static class GraphWindowPlacement
{
    public static PixelPoint IntoView(PixelRect window, PixelRect area) =>
        new(Clamp(window.X, area.X, area.Right - window.Width), Clamp(window.Y, area.Y, area.Bottom - window.Height));

    private static int Clamp(int value, int lowest, int highest) =>
        highest < lowest ? lowest : Math.Clamp(value, lowest, highest);
}
