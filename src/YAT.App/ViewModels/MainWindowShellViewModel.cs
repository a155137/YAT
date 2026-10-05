using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using YAT.Application.Graphs;
using YAT.app.Analyses;
using YAT.app.Graphs;
using YAT.app.Lifecycle;
using YAT.app.Updates;

namespace YAT.app.ViewModels;

// The main window's view model: the File menu (project lifecycle) and window title, around the current project's
// MainWindowViewModel (Project), which is replaced whenever another project is shown (New, Open, Save As).
public sealed partial class MainWindowShellViewModel : ViewModelBase
{
    private const string ApplicationName = "YAT";

    private MainWindowViewModel? _observedProject;

    public MainWindowShellViewModel(
        ProjectLifecycleController lifecycle,
        GraphSetupController graphs,
        DescriptiveStatisticsController statistics,
        CapabilityAnalysisController capability,
        UpdateCheckController? updates = null,
        OpenGraphsViewModel? openGraphs = null)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(graphs);
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(capability);
        Lifecycle = lifecycle;
        Graphs = graphs;
        Statistics = statistics;
        Capability = capability;
        Updates = updates;
        OpenGraphs = openGraphs ?? new OpenGraphsViewModel();
        Lifecycle.ProjectReplaced += OnProjectReplaced;
        ObserveProject();
    }

    public ProjectLifecycleController Lifecycle { get; }

    // Graph menu: set up a graph over the active worksheet of the current project.
    public GraphSetupController Graphs { get; }

    // Statistics menu: run an analysis over the active worksheet of the current project.
    public DescriptiveStatisticsController Statistics { get; }

    // Statistics menu: measure the active worksheet's variables against their specifications.
    public CapabilityAnalysisController Capability { get; }

    // Help menu: check for an update (Task #051.B); null when this composition has no update check.
    public UpdateCheckController? Updates { get; }

    // The Graphs list (Task #061): the graph windows open now, which the graph window presenter lists; empty when this
    // composition opens no graph windows.
    public OpenGraphsViewModel OpenGraphs { get; }

    public MainWindowViewModel? Project => Lifecycle.Project;

    // "YAT — <project name>"; never the file name, path or a modified marker.
    public string WindowTitle => Project?.CurrentProject is { } project ? $"{ApplicationName} — {project.Name}" : ApplicationName;

    public Task<bool> StartAsync() => Lifecycle.StartAsync();

    // Whether the window may close (after the save prompt, if one was needed); the project is closed when it may.
    public Task<bool> RequestCloseAsync() => Lifecycle.CloseAsync();

    // Help > Check for Updates...: one update window at a time (asking again while one is open does nothing).
    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => Updates?.CheckForUpdatesAsync() ?? Task.CompletedTask;

    private bool CanCheckForUpdates() => Updates is not null;

    [RelayCommand]
    private Task NewProjectAsync() => Lifecycle.NewProjectAsync();

    [RelayCommand]
    private Task OpenProjectAsync() => Lifecycle.OpenProjectAsync();

    [RelayCommand]
    private Task SaveAsync() => Lifecycle.SaveAsync();

    [RelayCommand]
    private Task SaveAsAsync() => Lifecycle.SaveAsAsync();

    [RelayCommand]
    private Task ScatterPlotAsync() => ConfigureGraphAsync(GraphType.ScatterPlot);

    [RelayCommand]
    private Task HistogramAsync() => ConfigureGraphAsync(GraphType.Histogram);

    [RelayCommand]
    private Task BoxPlotAsync() => ConfigureGraphAsync(GraphType.BoxPlot);

    [RelayCommand]
    private Task ProbabilityPlotAsync() => ConfigureGraphAsync(GraphType.ProbabilityPlot);

    [RelayCommand]
    private Task EmpiricalCdfAsync() => ConfigureGraphAsync(GraphType.EmpiricalCdf);

    [RelayCommand]
    private Task DescriptiveStatisticsAsync() =>
        Statistics.ConfigureAsync(Lifecycle.CurrentSession, Project?.SelectedWorksheet, CancellationToken.None);

    [RelayCommand]
    private Task CapabilityAnalysisAsync() =>
        Capability.ConfigureAsync(Lifecycle.CurrentSession, Project?.SelectedWorksheet, CancellationToken.None);

    // Always the current project's session and its active worksheet, so a project switch is picked up automatically.
    private Task ConfigureGraphAsync(GraphType graphType) =>
        Graphs.ConfigureAsync(graphType, Lifecycle.CurrentSession, Project?.SelectedWorksheet, CancellationToken.None);

    private void OnProjectReplaced(object? sender, EventArgs e)
    {
        ObserveProject();
        OnPropertyChanged(nameof(Project));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void ObserveProject()
    {
        if (_observedProject is not null)
        {
            _observedProject.PropertyChanged -= OnProjectPropertyChanged;
        }

        _observedProject = Project;
        if (_observedProject is not null)
        {
            _observedProject.PropertyChanged += OnProjectPropertyChanged;
        }
    }

    private void OnProjectPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.CurrentProject))
        {
            OnPropertyChanged(nameof(WindowTitle));
        }
    }
}
