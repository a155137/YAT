using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Styling;

namespace YAT.app;

// The icon every YAT window shows - in its title bar and on the taskbar - in one place (Task #048). It is the YAT icon,
// Assets/yat.ico, once that file exists; the same file then is the executable's icon too (YAT.App.csproj). Until then
// the Avalonia template's icon stands in for the windows, as it always has for the main window.
internal static class AppIcon
{
    // Where the YAT icon goes.
    public const string YatIcon = "avares://YAT/Assets/yat.ico";

    // What stands in for it until it exists.
    public const string StandInIcon = "avares://YAT/Assets/avalonia-logo.ico";

    // The icon, or null when neither file is there.
    public static WindowIcon? Load()
    {
        foreach (var path in new[] { YatIcon, StandInIcon })
        {
            var uri = new Uri(path);
            if (AssetLoader.Exists(uri))
            {
                using var stream = AssetLoader.Open(uri);
                return new WindowIcon(stream);
            }
        }

        return null;
    }

    // A style that gives every window the icon: the main window, graph windows, result windows and dialogs alike.
    public static Style Style(WindowIcon icon) =>
        new(selector => selector.Is<Window>()) { Setters = { new Setter(Window.IconProperty, icon) } };
}
