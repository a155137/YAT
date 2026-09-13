using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;
using AvaloniaApplication = Avalonia.Application;

namespace YAT.app;

public partial class App : AvaloniaApplication
{
    public CompositionRoot Composition { get; } = new(TimeProvider.System);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = Composition.CreateMainWindowViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}