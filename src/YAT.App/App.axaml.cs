using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using YAT.app.Clipboard;
using YAT.app.Composition;
using YAT.app.ViewModels;
using YAT.app.Views;
using AvaloniaApplication = Avalonia.Application;

namespace YAT.app;

public partial class App : AvaloniaApplication
{
    // No project files exist yet, so the runtime project's raw data lives in a private in-memory database.
    private const string RuntimeProjectDatabase = ":memory:";

    public CompositionRoot Composition { get; } = new(TimeProvider.System);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The application owns the single runtime ProjectSession and releases it when the desktop lifetime exits.
            var projectSession = Composition.CreateProjectSession(RuntimeProjectDatabase);
            desktop.Exit += (_, _) => projectSession.Dispose();

            // The clipboard belongs to the window, so the window exists before the session that reads from it.
            var mainWindow = new MainWindow();
            var session = Composition.CreateMainWindowSession(projectSession, new AvaloniaClipboardTextReader(mainWindow));
            mainWindow.DataContext = Composition.CreateMainWindowViewModel(session);
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}