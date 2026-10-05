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
            // The user's graph palettes (Task #050): read once, from the user's profile; a new graph starts with their
            // default.
            // Graph windows offer the same palettes in Edit Appearance... - their choices and the Palette Manager only.
            var palettes = Composition.CreateGraphPaletteLibrary();
            // The Graphs list (Task #061): every graph window the presenter opens is listed there while it is open.
            var openGraphs = new OpenGraphsViewModel();
            var graphs = Composition.CreateGraphSetup(
                new AvaloniaGraphSetupDialogs(mainWindow),
                new AvaloniaGraphWindowPresenter(mainWindow, exports, Composition.GraphPaletteAccess(palettes), openGraphs),
                palettes);
            // Analyses show their results in the shared analysis result window, which the presenter opens.
            var results = new AvaloniaAnalysisResultPresenter(mainWindow);
            var statistics = Composition.CreateDescriptiveStatistics(new AvaloniaAnalysisSetupDialogs(mainWindow), results);
            var capability = Composition.CreateCapabilityAnalysis(new AvaloniaCapabilityAnalysisSetupDialogs(mainWindow), results);
            // Help > Check for Updates... (Task #051.B): one update connection - one HttpClient - for the application's
            // lifetime, closed when it exits. Nothing connects until the user asks.
            var updateConnection = Composition.CreateUpdateConnection();
            desktop.Exit += (_, _) => updateConnection.Dispose();
            var updates = Composition.CreateUpdateCheck(new AvaloniaUpdateDialogs(mainWindow, desktop), updateConnection);
            // Started by the updater after it installed this version (Task #051.C): said once.
            var updated = Composition.TakeInstalledUpdateNotice();
            var shell = Composition.CreateMainWindowShellViewModel(lifecycle, graphs, statistics, capability, updates, openGraphs);

            mainWindow.DataContext = shell;
            desktop.MainWindow = mainWindow;

            // Start with "Untitled Project" / "Sheet1" in temporary file-backed project storage. The storage is local, so
            // this normally completes before the window is shown; awaiting it on Opened surfaces any unexpected failure.
            var startup = shell.StartAsync();
            mainWindow.Opened += async (_, _) =>
            {
                await startup;
                if (updated is not null)
                {
                    await LifecycleMessageWindow.ShowInformationAsync(mainWindow, $"YAT has been updated to v{updated}.");
                }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}