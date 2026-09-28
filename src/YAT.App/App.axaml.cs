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
            // Every window shows the application's icon (AppIcon), before the first one is made.
            Styles.Add(AppIcon.Style(AppIcon.Load()));

            // The application owns the project workspace. Disposing it on exit closes the current project database and
            // deletes the temporary storage of an untitled project.
            var workspace = Composition.CreateProjectWorkspace();
            desktop.Exit += (_, _) => workspace.Dispose();

            // The clipboard and the lifecycle dialogs belong to the window, so the window exists before the lifecycle.
            var mainWindow = new MainWindow();
            var clipboard = new AvaloniaClipboard(mainWindow);
            var lifecycle = Composition.CreateProjectLifecycle(workspace, clipboard, clipboard, new AvaloniaProjectLifecycleDialogs(mainWindow));
            // Graph windows export through a workflow built here: the shared parts come from the composition root, and
            // each window adds its own dialogs when it opens.
            var exports = new AvaloniaGraphExportWorkflowFactory(Composition.CreateGraphExportService(), Composition.CreatePowerPointExporter());
            var graphs = Composition.CreateGraphSetup(
                new AvaloniaGraphSetupDialogs(mainWindow),
                new AvaloniaGraphWindowPresenter(mainWindow, exports));
            // Analyses show their results in the shared analysis result window, which the presenter opens.
            var results = new AvaloniaAnalysisResultPresenter(mainWindow);
            var statistics = Composition.CreateDescriptiveStatistics(new AvaloniaAnalysisSetupDialogs(mainWindow), results);
            var capability = Composition.CreateCapabilityAnalysis(new AvaloniaCapabilityAnalysisSetupDialogs(mainWindow), results);
            var shell = Composition.CreateMainWindowShellViewModel(lifecycle, graphs, statistics, capability);

            mainWindow.DataContext = shell;
            desktop.MainWindow = mainWindow;

            // Start with "Untitled Project" / "Sheet1" in temporary file-backed project storage. The storage is local, so
            // this normally completes before the window is shown; awaiting it on Opened surfaces any unexpected failure.
            var startup = shell.StartAsync();
            mainWindow.Opened += async (_, _) => await startup;
        }

        base.OnFrameworkInitializationCompleted();
    }
}