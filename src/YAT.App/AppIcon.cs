using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;

namespace YAT.app;

// The YAT icon and logo, in one place (Task #048). Assets/yat.ico is the executable's icon (YAT.App.csproj) and, through
// the style below, every window's - in its title bar and on the taskbar. Assets/yat-logo.png is the same mark for the
// About dialog, drawn at twice the size it is shown at so it stays sharp on high-DPI screens.
internal static class AppIcon
{
    public const string YatIcon = "avares://YAT/Assets/yat.ico";

    public const string YatLogo = "avares://YAT/Assets/yat-logo.png";

    public static WindowIcon Load()
    {
        using var stream = AssetLoader.Open(new Uri(YatIcon));
        return new WindowIcon(stream);
    }

    public static Bitmap LoadLogo()
    {
        using var stream = AssetLoader.Open(new Uri(YatLogo));
        return new Bitmap(stream);
    }

    // A style that gives every window the icon: the main window, graph windows, result windows and dialogs alike.
    public static Style Style(WindowIcon icon) =>
        new(selector => selector.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } };
}
