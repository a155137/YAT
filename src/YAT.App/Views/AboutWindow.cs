using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace YAT.app.Views;

// Help > About YAT... (Task #048): the application's name, what it stands for, its version, what it is for, its
// publisher and its license. The name, description, version and publisher are the assembly's metadata
// (ApplicationInfo); nothing here writes a version of its own. On the left, the YAT logo - the mark of the application's
// icon (Task #048.1). Modal to the main window.
internal sealed class AboutWindow : Window
{
    public const string Summary = "Engineering data analysis and visualization";

    public const string License = "License: AGPL-3.0";

    // The logo's place.
    public const double LogoSize = 96;

    private AboutWindow(ApplicationInfo info)
    {
        Title = $"About {info.Product}";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var close = new Button
        {
            Content = "OK",
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            IsDefault = true,
            IsCancel = true
        };
        close.Classes.Add("accent");
        close.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24, 20),
            Spacing = 18,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 20,
                    Children =
                    {
                        new Border
                        {
                            Name = "LogoArea",
                            Width = LogoSize,
                            Height = LogoSize,
                            VerticalAlignment = VerticalAlignment.Top,
                            Child = Logo()
                        },
                        new StackPanel
                        {
                            Spacing = 4,
                            Children =
                            {
                                Line(info.Product, "Product", fontSize: 24, weight: FontWeight.SemiBold),
                                Line(info.Description, "Description"),
                                Line($"Version {info.Version}", "Version", top: 6),
                                Line(Summary, "Summary", top: 6),
                                Line(info.Company, "Company", top: 6),
                                Line(License, "License", opacity: 0.7)
                            }
                        }
                    }
                },
                close
            }
        };
    }

    // The logo, scaled down smoothly from the size it is drawn at.
    private static Image Logo()
    {
        var logo = new Image { Name = "Logo", Source = AppIcon.LoadLogo(), Width = LogoSize, Height = LogoSize, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(logo, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        return logo;
    }

    private static TextBlock Line(string text, string name, double fontSize = 0, FontWeight weight = FontWeight.Normal, double top = 0, double opacity = 1)
    {
        var line = new TextBlock { Text = text, Name = name, FontWeight = weight, Opacity = opacity, Margin = new Thickness(0, top, 0, 0) };
        if (fontSize > 0)
        {
            line.FontSize = fontSize;
        }

        return line;
    }

    // Shows the dialog over its owner until it is closed.
    public static Task ShowAsync(Window owner) => new AboutWindow(ApplicationInfo.Current).ShowDialog(owner);
}
