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
            var clipboard = new AvaloniaClipboard(mainWindow);
            var session = Composition.CreateMainWindowSession(projectSession, clipboard, clipboard);
            var viewModel = Composition.CreateMainWindowViewModel(session);

            // Start with "Untitled Project" / "Sheet1". The metadata repositories are in-memory, so this normally
            // completes before the window is shown; awaiting it on Opened surfaces any unexpected failure.
            var defaultWorkspace = viewModel.CreateDefaultWorkspaceAsync();
            mainWindow.Opened += async (_, _) => await defaultWorkspace;

            mainWindow.DataContext = viewModel;
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}