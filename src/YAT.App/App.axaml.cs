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
    public CompositionRoot Composition { get; } = new(TimeProvider.System);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // The application owns the project workspace. Disposing it on exit closes the current project database and
            // deletes the temporary storage of an untitled project.
            var workspace = Composition.CreateProjectWorkspace();
            desktop.Exit += (_, _) => workspace.Dispose();

            var mainWindow = new MainWindow();
            desktop.MainWindow = mainWindow;

            // Start with "Untitled Project" / "Sheet1" in temporary file-backed project storage. The storage is local, so
            // this normally completes before the window is shown; awaiting it on Opened surfaces any unexpected failure.
            var startup = StartAsync(workspace, mainWindow);
            mainWindow.Opened += async (_, _) => await startup;
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(ProjectWorkspace workspace, MainWindow mainWindow)
    {
        var projectSession = await workspace.CreateTemporaryProjectAsync(CancellationToken.None);

        // The clipboard belongs to the window, so the window exists before the session that reads from it.
        var clipboard = new AvaloniaClipboard(mainWindow);
        var session = Composition.CreateMainWindowSession(projectSession, clipboard, clipboard);
        var viewModel = Composition.CreateMainWindowViewModel(session);

        mainWindow.DataContext = viewModel;
        await viewModel.LoadProjectAsync();
    }
}